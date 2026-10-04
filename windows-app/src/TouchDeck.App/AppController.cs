using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using TouchDeck.Core;
using TouchDeck.Core.App;
using TouchDeck.Core.Devices;
using TouchDeck.Core.Firmware;
using TouchDeck.Core.Input;
using TouchDeck.Core.Mirror;
using TouchDeck.Core.Protocol;
using TouchDeck.Core.Selection;
using TouchDeck.Core.Session;
using TouchDeck.Core.Updates;

namespace TouchDeck.App;

public enum Health { Idle, Ok, Bad }

/// <summary>
/// The app-wide side of the window and tray: the boards (one <see cref="BoardController"/> per tab),
/// which one is selected, settings, app updates, recent clips and the activity log. Core events
/// arrive on background threads and are marshalled to the UI thread here.
/// </summary>
public sealed class AppController : INotifyPropertyChanged, IDisposable
{
    private const int LogLimit = 500;

    private readonly Dispatcher ui;
    private readonly string settingsPath;
    private readonly SwitchableSink sink = new(new SendInputSink());
    private readonly SelectionProvider selection = SelectionProvider.CreateDefault();
    private readonly DeviceManager manager;
    private readonly Autostart autostart = new();
    private readonly DispatcherTimer bootDriveWatch, mirrorWatch;
    private AppSettings settings;

    public AppController(Dispatcher ui, string? settingsPath = null, UpdateSource? updateSource = null)
    {
        updates = new UpdateService(new UpdateClient(updateSource ?? UpdateSource.Official), UpdateService.DefaultDownloadDir);
        UpdateSourceIsTest = updateSource?.IsTest ?? false;
        this.ui = ui;
        this.settingsPath = settingsPath ?? AppSettings.DefaultPath;
        settings = AppSettings.Load(this.settingsPath);
        sink.DryRun = settings.DryRun;
        sink.DryRunEvent += e => Post(() => AddLog($"dry run: {e}"));

        FirmwareDir = Path.Combine(AppContext.BaseDirectory, "firmware");
        Bundled = BundledFirmware.LoadManifest(FirmwareDir);
        placeholder = BoardController.Placeholder(this, ui);
        selected = placeholder;

        manager = new DeviceManager(
            DeviceScanner.Scan,
            d => new SerialPortTransport(d),
            t => new DeviceSession(t, new Injector(sink), new WindowsKeyboardState(), selection, new SystemClock()));
        manager.SessionStarted += OnSessionStarted;
        manager.SlotChanged += (port, s) => Post(() => GetOrAdd(port, port).ApplyState(s));
        manager.SlotRemoved += port => Post(() => RemoveBoard(port));
        History.Changed += () => Post(() =>
        {
            HistoryItems.Clear();
            foreach (var h in History.Items) HistoryItems.Add(h);
        });

        // The installer drops the Run entry on upgrade/uninstall and the path can move: re-assert it.
        // Only the installed copy re-points the entry (an upgrade or a moved install); a dev or
        // test build must not hijack the user's Start with Windows.
        if (settings.StartWithWindows && Autostart.IsInstalledCopy(Environment.ProcessPath!) &&
            (!autostart.IsEnabled(Environment.ProcessPath!) || autostart.IsMinimized(Environment.ProcessPath!) != settings.StartMinimized))
            autostart.Set(true, Environment.ProcessPath!, settings.StartMinimized);

        // The board mirrors: known once a session has either seen STATE or given up waiting.
        mirrorWatch = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
        {
            foreach (var b in Boards) b.RefreshMirror();
        }, ui);
        bootDriveWatch = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => CheckNewBoards(), ui);
    }

    public static string AppVersion => CoreInfo.Version;
    public string FirmwareDir { get; }
    public IReadOnlyList<BundledFirmware> Bundled { get; }
    public string BundledSummary => Bundled.Count == 0
        ? "no firmware bundled"
        : string.Join(", ", Bundled.Select(b => $"{b.Board} {b.Version}"));

    public ClipHistory History { get; } = new();
    public ObservableCollection<string> HistoryItems { get; } = [];
    public ObservableCollection<LogEntry> LogLines { get; } = [];

    public event Action<string, string>? Notify;             // (title, text) for tray balloons
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Start()
    {
        manager.Start();
        bootDriveWatch.Start();
        mirrorWatch.Start();
        StartUpdateTimer();
        AddLog($"Touch Deck {AppVersion} started; bundled firmware: {BundledSummary}");
    }

    // ---------- boards ----------

    private readonly BoardController placeholder;
    private BoardController selected;

    /// <summary>Every attached board, one tab each, in the order they appeared.</summary>
    public ObservableCollection<BoardController> Boards { get; } = [];

    /// <summary>
    /// The board the window shows; the placeholder ("No board") while none is attached. Setting it is
    /// the user's choice (a tab click, a just-installed board) and is remembered as the last-used port.
    /// </summary>
    public BoardController Selected
    {
        get => selected;
        set => Select(value, remember: true);
    }

    /// <summary>Shows a board without remembering it as the user's choice (smoke runs).</summary>
    internal void Show(BoardController b) => Select(b, remember: false);

    /// <param name="remember">False when the app picks the tab itself (boards coming and going):
    /// only the user's choice is the "last used" board that is selected again when it returns.</param>
    private void Select(BoardController? value, bool remember)
    {
        var next = value is not null && Boards.Contains(value) ? value : Boards.FirstOrDefault() ?? placeholder;
        if (remember && next.Port.Length > 0 && settings.PreferredPort != next.Port) SaveSettings(settings with { PreferredPort = next.Port });
        if (next == selected) return;
        selected.IsSelected = false;
        selected = next;
        selected.IsSelected = !selected.IsPlaceholder;
        Raise(nameof(Selected));
    }

    /// <summary>The tab strip and the log filter appear once there is more than one board.</summary>
    public bool ShowTabs => Boards.Count > 1;

    public BoardController? Find(string key) => Boards.FirstOrDefault(b => b.Key == key);

    private BoardController GetOrAdd(string key, string? port, NewBoard? newBoard = null)
    {
        if (Find(key) is { } b) return b;
        b = new BoardController(this, ui, key, port);
        if (newBoard is not null) b.SetNewBoard(newBoard);
        b.PropertyChanged += OnBoardChanged;
        Boards.Add(b);
        Reselect(added: key);
        BoardsChanged();
        return b;
    }

    private void RemoveBoard(string key)
    {
        if (Find(key) is not { } b) return;
        if (Installing == b) return;                  // being installed: its port comes and goes
        b.Detached = true;
        b.PropertyChanged -= OnBoardChanged;
        Boards.Remove(b);
        if (b == selected) selected.IsSelected = false;
        Reselect(added: null);
        BoardsChanged();
    }

    private void Reselect(string? added)
    {
        var key = BoardSelection.Next(Boards.Select(b => b.Key).ToList(),
            selected.IsPlaceholder || !Boards.Contains(selected) ? null : selected.Key, added, settings.PreferredPort);
        Select(key is null ? placeholder : Find(key)!, remember: false);
    }

    private void BoardsChanged()
    {
        Raise(nameof(ShowTabs));
        if (!ShowTabs && OnlySelectedLog) OnlySelectedLog = false;
        RefreshSummary();
    }

    private void OnBoardChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BoardController.Health) or nameof(BoardController.StatusText) or nameof(BoardController.FirmwareVersion)
            or nameof(BoardController.IsConnected) or nameof(BoardController.Label))
            RefreshSummary();
    }

    /// <summary>The tray's view of all boards: green if one is connected, red if one has a problem.</summary>
    public Health Health { get => health; private set => Set(ref health, value); }
    /// <summary>One line per board (the tray tooltip).</summary>
    public string Summary { get => summary; private set => Set(ref summary, value); }
    public bool AnyConnected => Boards.Any(b => b.IsConnected);
    private Health health;
    private string summary = "";

    private void RefreshSummary()
    {
        Health = Boards.Any(b => b.IsConnected) ? Health.Ok : Boards.Any(b => b.Health == Health.Bad) ? Health.Bad : Health.Idle;
        Summary = Boards.Count == 0 ? "Looking for a Touch Deck..."
            : string.Join("\n", Boards.Select(b => b.IsConnected ? $"{b.Label}, firmware {b.State.Firmware?.Version}" : $"{b.Label}: {b.StatusText}"));
        Raise(nameof(AnyConnected));
    }

    /// <summary>A board's balloon names the board once there is more than one.</summary>
    public void NotifyBoard(BoardController b, string title, string text) =>
        Notify?.Invoke(title, ShowTabs ? $"{b.Label}: {text}" : text);

    private void OnSessionStarted(string port, DeviceSession s)
    {
        // Runs on the manager's thread before the session reads: hook its events now, act on the UI thread.
        var mirror = new MirrorState(UiModels.For(s.Kind, s.Firmware?.Board));   // both RP boards are CAFE:4011
        BoardController? Board() => Find(port) is { } b && b.Session == s ? b : null;
        Post(() => GetOrAdd(port, port).Attach(s, mirror));
        s.Log += text => Post(() => Board()?.Log($"board: {text}"));
        s.Diagnostics += d => Post(() => Board()?.ShowDiagnostics(d));
        s.StateReceived += st => Post(() =>
        {
            if (Board() is not { } b) return;
            b.ApplyBoardState(st);
            b.ApplyMirror(mirror, st);
        });
        s.TextReceived += t => Post(() => Board()?.ApplyMirror(mirror, t));
        s.ClipTextReceived += c => Post(() => Board()?.ApplyMirror(mirror, c));
        s.ClipSent += (text, src, lost) => Post(() =>
        {
            History.Add(text);
            var note = lost > 0 ? $", {lost} non-ASCII characters as '?'" : "";
            var line = $"Sent {text.Length} characters from {Source(src)}{note}";
            if (Board() is { } b) b.Log(line); else AddLog(line);
        });
    }

    private static string Source(string src) => src switch
    {
        "select" => "the selection",
        "clipbd" => "the clipboard",
        _ => "the app",
    };

    /// <summary>Global hotkey: the same as tapping COPY on the selected board.</summary>
    public void SendSelection()
    {
        if (Selected.Session is not { } s)
        {
            Notify?.Invoke("Touch Deck", "No board connected.");
            return;
        }
        Task.Run(() =>
        {
            var (text, src) = selection.Grab();
            if (string.IsNullOrEmpty(text))
            {
                Post(() => Notify?.Invoke("Touch Deck", "Nothing selected and the clipboard is empty."));
                return;
            }
            s.SendText(text, src);
            var (ascii, _) = AsciiText.Transliterate(text);
            Post(() => Notify?.Invoke("Sent to Touch Deck", Preview(ascii)));
        });
    }

    public static string Preview(string text)
    {
        var one = text.ReplaceLineEndings(" ").Trim();
        return one.Length <= 60 ? one : one[..57] + "...";
    }

    // ---------- new boards (no Touch Deck firmware) ----------

    private bool scanningNewBoards;

    // Every 2 s: Raspberry Pi boards on their factory firmware or in their bootloader (by USB ID),
    // each its own tab. Paused while an install runs: the board being flashed passes through its
    // bootloader. The WMI query runs off the UI thread.
    private async void CheckNewBoards()
    {
        if (Installing is not null || scanningNewBoards) return;
#if DEBUG
        if (demoBoards) return;
#endif
        scanningNewBoards = true;
        try
        {
            var found = await Task.Run(() =>
            {
                try { return NewBoards.Scan(); }
                catch (System.Management.ManagementException) { return []; }
            });
            if (disposed || Installing is not null) return;
            var keys = new HashSet<string>();
            foreach (var nb in found)
            {
                var key = NewBoardKey(nb);
                keys.Add(key);
                var b = GetOrAdd(key, nb.Port, nb);
                if (!b.IsConnected) b.SetNewBoard(nb);
            }
            foreach (var b in Boards.Where(b => b.NewBoard is not null && !keys.Contains(b.Key)).ToList())
            {
                if (manager.SessionFor(b.Key) is not null) b.SetNewBoard(null);   // its port runs Touch Deck now
                else RemoveBoard(b.Key);
            }
        }
        finally
        {
            scanningNewBoards = false;
        }
    }

    private static string NewBoardKey(NewBoard nb) => nb.Port ?? $"boot:{nb.Chip}";

#if DEBUG
    private bool demoBoards;

    /// <summary>
    /// --smoke-demo-boards (debug builds): two pretend tabs, a board in its bootloader and a busy port,
    /// so the tab strip and the log filter can be checked with one real board attached.
    /// </summary>
    public void AddDemoBoards()
    {
        demoBoards = true;                                // the new-board scan would drop the pretend one
        var boot = new NewBoard(Uf2Chip.Rp2350, NewBoardState.Bootloader, null);
        GetOrAdd(NewBoardKey(boot), null, boot);
        GetOrAdd("COM99", "COM99").ApplyState(new LinkState(LinkStatus.PortBusy,
            new DeviceCandidate("COM99", BoardKind.Rp2040, new UsbId(0xCAFE, 0x4011))));
    }
#endif

    // ---------- firmware installs (one at a time) ----------

    private BoardController? installing;
    /// <summary>The board being installed; the others keep working, but wait to be installed.</summary>
    public BoardController? Installing
    {
        get => installing;
        private set
        {
            if (installing == value) return;
            installing = value;
            Raise(nameof(Installing));
            foreach (var b in Boards) { b.RefreshUpdateOffer(); b.RefreshFeedOffer(); }
        }
    }

    /// <summary>
    /// Installs a UF2 (bundled, or downloaded and verified) for <paramref name="model"/> on
    /// <paramref name="board"/>: chip and model are checked before the board is touched. Success
    /// is a session that wasn't there before reporting the model: another board of the same model
    /// can't be mistaken for it.
    /// </summary>
    public async Task<bool> FlashAsync(BoardController board, string uf2, string label, BoardModel model, Action enterBootloader,
                                       IProgress<string> boardProgress)
    {
        if (Installing is not null) return false;
        Installing = board;
        var before = manager.Sessions.ToHashSet();
        DeviceSession? fresh = null;
        bool ok = false;
        board.Log($"Installing firmware {label}");
        var steps = new UpdateSteps
        {
            EnterBootloader = enterBootloader,
            FindBootDrive = () => Uf2.FindBootDrive(Uf2.RemovableRoots(), model.Chip),
            BootloaderChip = () => Uf2.FindBootDrive(Uf2.RemovableRoots(), Uf2Chip.Rp2350) is not null ? Uf2Chip.Rp2350
                                 : Uf2.FindBootDrive(Uf2.RemovableRoots(), Uf2Chip.Rp2040) is not null ? Uf2Chip.Rp2040 : null,
            CopyImage = CopyToBootDrive,
            ReadRunningFirmware = () =>
            {
                fresh = manager.Sessions.FirstOrDefault(s => !before.Contains(s) && s.Firmware?.Board == model.Board);
                return fresh?.Firmware;
            },
        };
        var progress = new Progress<string>(m => { board.Log(m); boardProgress.Report(m); });
        try
        {
            var result = await FirmwareUpdater.InstallAsync(uf2, model, steps, progress);
            ok = result.Ok;
            board.Log(result.Message);
            NotifyBoard(board, result.Ok ? "Firmware updated" : "Firmware update failed", result.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // e.g. antivirus holding the new file on the boot drive: the board is still in its
            // bootloader, and the app offers the install again from there.
            var msg = $"Firmware update failed: {e.Message}";
            board.Log(msg);
            NotifyBoard(board, "Firmware update failed", msg);
        }
        finally
        {
            Installing = null;
        }
        // The installed board's tab is the one to show: its port may be new (a new board gets one).
        var port = fresh is null ? null : manager.Links.FirstOrDefault(l => l.Session == fresh)?.Port;
        if (port is not null && port != board.Key)
        {
            var now = GetOrAdd(port, port);
            if (!board.IsConnected && manager.SessionFor(board.Key) is null) RemoveBoard(board.Key);
            Selected = now;
        }
        else if (manager.SessionFor(board.Key) is null && board.NewBoard is null && !board.IsConnected)
        {
            RemoveBoard(board.Key);                   // gone and not back: drop the tab
        }
        return ok;
    }

    private static void CopyToBootDrive(string src, string drive)
    {
        try
        {
            File.Copy(src, Path.Combine(drive, Path.GetFileName(src)), overwrite: true);
        }
        catch (IOException) when (!Directory.Exists(drive))
        {
            // The board reboots as soon as the last block lands, taking the drive with it.
        }
    }

    // ---------- settings ----------

    public bool DryRun
    {
        get => settings.DryRun;
        set
        {
            sink.DryRun = value;
            SaveSettings(settings with { DryRun = value });
            AddLog(value ? "Dry run on: keys and mouse from the board are logged, not performed" : "Dry run off");
        }
    }

    public bool DiagnosticsEnabled
    {
        get => settings.Diagnostics;
        set
        {
            foreach (var s in manager.Sessions) s.DiagnosticsEnabled = value;
            SaveSettings(settings with { Diagnostics = value });
            if (!value) foreach (var b in Boards) b.DiagnosticItems.Clear();
        }
    }

    public bool StartWithWindows
    {
        get => autostart.IsEnabled(Environment.ProcessPath!);
        set
        {
            autostart.Set(value, Environment.ProcessPath!, settings.StartMinimized);
            SaveSettings(settings with { StartWithWindows = value });
        }
    }

    /// <summary>With Start with Windows: start hidden in the tray.</summary>
    public bool StartMinimized
    {
        get => settings.StartMinimized;
        set
        {
            if (StartWithWindows) autostart.Set(true, Environment.ProcessPath!, value);
            SaveSettings(settings with { StartMinimized = value });
        }
    }

    public bool CheckForUpdates
    {
        get => settings.CheckForUpdates;
        set => SaveSettings(settings with { CheckForUpdates = value });
    }

    // ---------- the activity log ----------

    /// <summary>Show only the selected board's lines (and the app's own); offered with more than one board.</summary>
    public bool OnlySelectedLog { get => onlySelectedLog; set => Set(ref onlySelectedLog, value); }
    private bool onlySelectedLog;

    public void AddLog(string line) => AddLog(null, line);

    public void AddLog(string? board, string line)
    {
        LogLines.Add(new LogEntry(DateTime.Now, board, line));
        while (LogLines.Count > LogLimit) LogLines.RemoveAt(0);
    }

    // ---------- updates ----------

    private readonly UpdateService updates;
    private UpdateChoice? lastChoice;
    private bool checkedOnce;
    private DispatcherTimer? updateTimer;
    public bool UpdateSourceIsTest { get; }

    /// <summary>The last verified feed: each board's firmware offer is worked out from it.</summary>
    public UpdateFeed? LastFeed { get; private set; }
    /// <summary>What a board's Firmware line says while there is no feed.</summary>
    public string FeedNote { get; private set; } = "";

    public string AppUpdateText { get => appUpdateText; private set => Set(ref appUpdateText, value); }
    public bool CanInstallApp { get => canInstallApp; private set => Set(ref canInstallApp, value); }
    public bool CheckingUpdates { get => checkingUpdates; private set => Set(ref checkingUpdates, value); }
    private string appUpdateText = "Not checked yet";
    private bool canInstallApp, checkingUpdates;

    /// <summary>Raised when the app must quit so the installer can replace it.</summary>
    public event Action? QuitForUpdate;

    private void StartUpdateTimer()
    {
        // First check shortly after start, then hourly ticks that check once a day.
        updateTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, async (_, _) =>
        {
            updateTimer!.Interval = TimeSpan.FromHours(1);
            if (!CheckForUpdates) return;
            if (checkedOnce && settings.LastUpdateCheck is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(24)) return;
            await CheckForUpdatesAsync(manual: false);
        }, ui);
        updateTimer.Start();
    }

    public async Task<UpdateCheckOutcome> CheckForUpdatesAsync(bool manual)
    {
        if (CheckingUpdates) return new UpdateCheckOutcome(lastChoice, false, null, LastFeed);
        CheckingUpdates = true;
        if (manual)
        {
            AppUpdateText = "Checking...";
            FeedNote = "";
        }
        try
        {
            var device = Selected.IsConnected ? Selected.State.Firmware : null;
            var outcome = await Task.Run(() => updates.CheckAsync(new Version(AppVersion), device, CancellationToken.None));
            checkedOnce = true;
            SaveSettings(settings with { LastUpdateCheck = DateTime.UtcNow });
            ApplyOutcome(outcome);
            return outcome;
        }
        finally
        {
            CheckingUpdates = false;
        }
    }

    private void ApplyOutcome(UpdateCheckOutcome o)
    {
        lastChoice = o.Choice;
        if (o.Error is { } err)
        {
            (AppUpdateText, CanInstallApp, FeedNote) = ($"Couldn't check: {err}", false, "");
            AddLog($"Update check failed: {err}");
        }
        else if (o.NothingPublished)
        {
            (AppUpdateText, CanInstallApp, FeedNote, LastFeed) = ("No updates published yet", false, "", null);
        }
        else
        {
            LastFeed = o.Feed;
            var app = o.Choice?.App;
            AppUpdateText = app is null ? "Up to date" : $"{app.Version.ToString(3)} available";
            CanInstallApp = app is not null;
            if (app is not null) Notify?.Invoke("Touch Deck update", $"Version {app.Version.ToString(3)} is available. Open Settings to install it.");
        }
        foreach (var b in Boards)
        {
            b.RefreshFeedOffer();
            if (b.CanInstallFirmware) b.Log($"Firmware {b.FirmwareUpdateText} on the update feed");
        }
        placeholder.RefreshFeedOffer();
    }

    public async Task InstallAppUpdateAsync()
    {
        if (lastChoice?.App is not { } app) return;
        CanInstallApp = false;
        AppUpdateText = "Downloading...";
        try
        {
            var progress = new Progress<double>(f => AppUpdateText = $"Downloading... {f:P0}");
            var msi = await updates.DownloadAppAsync(app, progress, CancellationToken.None);
            AddLog($"Downloaded and verified Touch Deck {app.Version.ToString(3)}; starting the installer");
            AppUpdateText = "Installing... Touch Deck will restart.";
            System.Diagnostics.Process.Start(InstallerLaunch.Create(msi, Environment.ProcessPath!))?.Dispose();
            QuitForUpdate?.Invoke();
        }
        catch (Exception e) when (e is UpdateVerificationException or IOException or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception)
        {
            AppUpdateText = $"Update failed: {e.Message}";
            CanInstallApp = true;
            AddLog(AppUpdateText);
        }
    }

    /// <summary>Downloads a firmware the feed offers (verified while streaming).</summary>
    public Task<string> DownloadFirmwareAsync(FirmwarePackage fw) => updates.DownloadFirmwareAsync(fw, null, CancellationToken.None);

    private void SaveSettings(AppSettings next)
    {
        settings = next;
        try { settings.Save(settingsPath); }
        catch (IOException e) { AddLog($"Couldn't save settings: {e.Message}"); }
        foreach (var name in new[] { nameof(DryRun), nameof(DiagnosticsEnabled), nameof(StartWithWindows), nameof(StartMinimized), nameof(CheckForUpdates) })
            Raise(name);
    }

    // ---------- plumbing ----------

    private void Post(Action a) => ui.BeginInvoke(() =>
    {
        if (!disposed) a();                       // late events from a closing session
    });

    private void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool disposed;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        bootDriveWatch.Stop();
        mirrorWatch.Stop();
        updateTimer?.Stop();
        manager.Dispose();               // ends every session, which releases any held key or button
    }
}
