using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using TouchDeck.Core;
using TouchDeck.Core.App;
using TouchDeck.Core.Devices;
using TouchDeck.Core.Firmware;
using TouchDeck.Core.Input;
using TouchDeck.Core.Jiggler;
using TouchDeck.Core.Mirror;
using TouchDeck.Core.Protocol;
using TouchDeck.Core.Selection;
using TouchDeck.Core.Session;
using TouchDeck.Core.Updates;

namespace TouchDeck.App;

public enum Health { Idle, Ok, Bad }

/// <summary>
/// Everything the window and tray show and do. Core events arrive on background
/// threads and are marshalled to the UI thread here.
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
    private readonly DispatcherTimer bootDriveWatch;
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

        manager = new DeviceManager(
            DeviceScanner.Scan,
            d => new SerialPortTransport(d),
            t => new DeviceSession(t, new Injector(sink), new WindowsKeyboardState(), selection, new SystemClock()))
        {
            PreferredPort = settings.PreferredPort,
        };
        manager.SessionStarted += OnSessionStarted;
        manager.StateChanged += s => Post(() => ApplyState(s));
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

        // The board mirror: known once the session has either seen STATE or given up waiting.
        mirrorWatch = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshMirror(), ui);

        bootDriveWatch = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => CheckNewBoards(), ui);
        ApplyState(LinkState.Searching);
    }

    public static string AppVersion => CoreInfo.Version;
    public string FirmwareDir { get; }
    public IReadOnlyList<BundledFirmware> Bundled { get; }
    public string BundledSummary => Bundled.Count == 0
        ? "no firmware bundled"
        : string.Join(", ", Bundled.Select(b => $"{b.Board} {b.Version}"));

    public ClipHistory History { get; } = new();
    public ObservableCollection<string> HistoryItems { get; } = [];
    public ObservableCollection<string> LogLines { get; } = [];
    public ObservableCollection<KeyValuePair<string, string>> DiagnosticItems { get; } = [];

    public event Action<string, string>? Notify;             // (title, text) for tray balloons
    public event PropertyChangedEventHandler? PropertyChanged;

    // ---------- link state ----------

    public LinkState State { get; private set; } = LinkState.Searching;
    public Health Health { get => health; private set => Set(ref health, value); }
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    public string BoardName { get => boardName; private set => Set(ref boardName, value); }
    public string Port { get => port; private set => Set(ref port, value); }
    public string FirmwareVersion { get => firmwareVersion; private set => Set(ref firmwareVersion, value); }
    public string FirmwareBuild { get => firmwareBuild; private set => Set(ref firmwareBuild, value); }
    public bool IsConnected { get => isConnected; private set => Set(ref isConnected, value); }
    /// <summary>A Raspberry Pi board without Touch Deck (factory firmware or its bootloader), while none is connected.</summary>
    public NewBoard? NewBoard { get => newBoard; private set => Set(ref newBoard, value); }
    /// <summary>The models the app has firmware for on that board's chip (one choice today per chip).</summary>
    public IReadOnlyList<BoardModel> NewBoardModels { get => newBoardModels; private set => Set(ref newBoardModels, value); }
    public BoardModel? SelectedModel
    {
        get => selectedModel;
        set { Set(ref selectedModel, value); RefreshUpdateOffer(); }
    }
    public string UpdateText { get => updateText; private set => Set(ref updateText, value); }
    public bool CanUpdate { get => canUpdate; private set => Set(ref canUpdate, value); }
    public bool Busy { get => busy; private set { Set(ref busy, value); RefreshUpdateOffer(); } }

    private Health health;
    private string statusText = "", boardName = "", port = "", firmwareVersion = "", firmwareBuild = "", updateText = "";
    private bool isConnected, canUpdate, busy;
    private NewBoard? newBoard;
    private IReadOnlyList<BoardModel> newBoardModels = [];
    private BoardModel? selectedModel;
    private bool scanningNewBoards;

    public void Start()
    {
        manager.Start();
        bootDriveWatch.Start();
        mirrorWatch.Start();
        StartUpdateTimer();
        AddLog($"Touch Deck {AppVersion} started; bundled firmware: {BundledSummary}");
    }

    private void ApplyState(LinkState s)
    {
        var was = State;
        State = s;
        IsConnected = s.Status == LinkStatus.Connected;
        if (!IsConnected) JigCfg = null;
        Port = s.Device?.Port ?? "";
        BoardName = s.Device is null ? "No board" : BoardKinds.DisplayName(s.Device.Kind, s.Firmware?.Board);
        FirmwareVersion = s.Firmware switch
        {
            null => "",
            { Known: false } => "unknown (older than 1.5.0)",
            var f => $"{f.Version}  ({f.Board})",
        };
        FirmwareBuild = s.Firmware?.Build ?? "";
        Raise(nameof(DeviceFirmwareText));
        (Health, StatusText) = s.Status switch
        {
            LinkStatus.Connected => (Health.Ok, "Connected"),
            LinkStatus.PortBusy => (Health.Bad, $"{s.Device!.Port} is in use by another program (the Python helper?)"),
            LinkStatus.NotResponding => (Health.Bad, $"{s.Device!.Port} doesn't answer. Is it running Touch Deck firmware?"),
            _ => (Health.Idle, "Looking for a Touch Deck..."),
        };
        if (!DiagnosticsEnabled || !IsConnected) DiagnosticItems.Clear();
        if (!IsConnected) ResetMirror();         // the board's screen goes with it, at once
        RefreshUpdateOffer();

        if (s.Status == was.Status && s.Device == was.Device) return;
        if (IsConnected)
        {
            AddLog($"Connected: {BoardName} on {Port}, firmware {FirmwareVersion}");
            Notify?.Invoke("Touch Deck connected", $"{BoardName} on {Port}, firmware {s.Firmware?.Version}");
            if (settings.PreferredPort != Port) SaveSettings(settings with { PreferredPort = Port });
        }
        else if (was.Status == LinkStatus.Connected)
        {
            AddLog("Disconnected");
            if (!Busy) Notify?.Invoke("Touch Deck disconnected", "Plug it back in; the app reconnects by itself.");
        }
        else if (s.Status != LinkStatus.Searching)
        {
            AddLog(StatusText);
        }
    }

    private void OnSessionStarted(DeviceSession s)
    {
        s.DiagnosticsEnabled = settings.Diagnostics;
        s.Log += text => Post(() => AddLog($"board: {text}"));
        s.Diagnostics += d => Post(() => ShowDiagnostics(d));
        var mirror = new MirrorState(UiModels.For(s.Kind, s.Firmware?.Board));   // both RP boards are CAFE:4011
        Post(() => StartMirror(mirror));
        s.StateReceived += st => Post(() => { ApplyBoardState(st); ApplyMirror(mirror, st); });
        s.TextReceived += t => Post(() => ApplyMirror(mirror, t));
        s.ClipTextReceived += c => Post(() => ApplyMirror(mirror, c));
        s.ClipSent += (text, src, lost) => Post(() =>
        {
            History.Add(text);
            var note = lost > 0 ? $", {lost} non-ASCII characters as '?'" : "";
            AddLog($"Sent {text.Length} characters from {Source(src)}{note}");
        });
    }

    private static string Source(string src) => src switch
    {
        "select" => "the selection",
        "clipbd" => "the clipboard",
        _ => "the app",
    };

    private void ShowDiagnostics(IReadOnlyDictionary<string, string> d)
    {
        DiagnosticItems.Clear();
        foreach (var kv in d) DiagnosticItems.Add(kv);
    }

    // ---------- actions ----------

    public void SendText(string text)
    {
        if (manager.Session is { } s && !string.IsNullOrEmpty(text)) s.SendText(text);
    }

    /// <summary>Global hotkey: the same as tapping COPY on the board.</summary>
    public void SendSelection()
    {
        if (manager.Session is not { } s)
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

    public void Swipe(bool left) => manager.Session?.Swipe(left);

    // ---------- board mirror (firmware 1.6.0+) ----------

    public bool JigOn { get => jigOn; private set => Set(ref jigOn, value); }
    public char JigLetter { get => jigLetter; private set => Set(ref jigLetter, value); }
    public double JigX { get => jigX; private set => Set(ref jigX, value); }
    public double JigY { get => jigY; private set => Set(ref jigY, value); }
    public string JigScaleText { get => jigScaleText; private set => Set(ref jigScaleText, value); }
    public string JigStatus { get => jigStatus; private set => Set(ref jigStatus, value); }
    public string BoardClipText { get => boardClipText; private set => Set(ref boardClipText, value); }
    public bool CanClearBoardClip { get => canClearBoardClip; private set => Set(ref canClearBoardClip, value); }
    /// <summary>True when the connected board streams STATE (jiggler card live).</summary>
    public bool MirrorAvailable { get => mirrorAvailable; private set => Set(ref mirrorAvailable, value); }
    public string MirrorFallbackText { get => mirrorFallbackText; private set => Set(ref mirrorFallbackText, value); }

    private bool jigOn, canClearBoardClip, mirrorAvailable;
    private char jigLetter = 'O';
    private double jigX, jigY;
    private int jigScale;
    private string jigScaleText = "1.0X", jigStatus = "", boardClipText = "", mirrorFallbackText = "";
    private readonly DispatcherTimer mirrorWatch;

    private void ApplyBoardState(StateReport st)
    {
        JigOn = st.JigOn;
        JigLetter = st.Letter;
        JigX = st.X;
        JigY = st.Y;
        jigScale = st.Scale;
        JigScaleText = JigView.ScaleText(st.Scale);
        JigStatus = JigView.Status(st);
        BoardClipText = JigView.ClipText(st);
        CanClearBoardClip = JigView.CanClear(st);
        MirrorAvailable = true;             // the fallback text comes from RefreshMirror (once a second)
        JigCfg = JigView.Config(st);
    }

    private void RefreshMirror()
    {
        var supported = manager.Session?.MirrorSupported;
        if (!IsConnected || supported is null)
        {
            if (!IsConnected)
            {
                // A new board must not inherit the last one's jiggler state or screen.
                ResetMirror();
                MirrorAvailable = false;
                MirrorFallbackText = "Connect a board to see and control its jiggler.";
                JigOn = false;
                JigLetter = 'O';
                JigStatus = "";
                BoardClipText = "";
                CanClearBoardClip = false;
            }
            return;
        }
        MirrorAvailable = supported.Value;
        MirrorFallbackText = FullMirror ? ""
            : !supported.Value ? "This board's firmware is older than 1.6.0. Use Install firmware below to see and control its jiggler here."
            : RendererError is not null ? "The device view couldn't load, so only the jiggler is shown."
            : State.Firmware?.SemVer is { } v && v < new Version(1, 7, 0)
                ? "Install firmware 1.7.0 or later (below) to see and use the whole device here."
                : "";
    }

    // ---------- device mirror (firmware 1.7.0+) ----------

    private MirrorState? mirror;
    private bool mirrorFramePending;

    /// <summary>The board streams its whole UI and the renderer loaded: the device view is live.</summary>
    public bool FullMirror { get => fullMirror; private set => Set(ref fullMirror, value); }
    /// <summary>Which device the mirror draws (its shape and renderer).</summary>
    public UiModel MirrorModel { get => mirrorModel; private set => Set(ref mirrorModel, value); }
    /// <summary>The latest device frame: RGB565, <see cref="NativeUi.Size"/> of <see cref="MirrorModel"/>.</summary>
    public ushort[]? MirrorFrame { get; private set; }
    /// <summary>Raised on the UI thread when <see cref="MirrorFrame"/> has a new frame.</summary>
    public event Action? MirrorFrameChanged;
    /// <summary>Set when the renderer failed to load (the app falls back to the jiggler card).</summary>
    public string? RendererError { get; private set; }

    private bool fullMirror;
    private UiModel mirrorModel = UiModel.Rp2040Rect;

    private void StartMirror(MirrorState m)
    {
        mirror = m;
        MirrorModel = m.Model;
        FullMirror = false;
        MirrorFrame = null;
        MirrorFrameChanged?.Invoke();
        if (!NativeUi.Available(m.Model, out var error))
        {
            RendererError = error;
            AddLog($"Device view unavailable: {error}");
        }
        else RendererError = null;
    }

    private void ApplyMirror(MirrorState m, BoardMessage message)
    {
        if (m != mirror || !m.Apply(message) || !m.Complete || RendererError is not null) return;
        if (!FullMirror)
        {
            FullMirror = true;
            RefreshMirror();
        }
        // Lines arrive in bursts (TEXT, CLIPTEXT, STATE): draw once when the burst is applied.
        if (mirrorFramePending) return;
        mirrorFramePending = true;
        ui.BeginInvoke(DispatcherPriority.Render, () =>
        {
            mirrorFramePending = false;
            if (disposed || mirror is null || !FullMirror) return;
            var (w, h) = NativeUi.Size(mirror.Model);
            var frame = MirrorFrame is { } f && f.Length == w * h ? f : new ushort[w * h];   // reused: 134 KB per frame would churn the LOH
            NativeUi.Render(mirror.Model, mirror.State, frame);
            MirrorFrame = frame;
            MirrorFrameChanged?.Invoke();
        });
    }

    private void ResetMirror()
    {
        if (mirror is null && !FullMirror) return;
        mirror = null;
        FullMirror = false;
        MirrorFrame = null;
        MirrorFrameChanged?.Invoke();
    }

    /// <summary>A click on the device view: the board's own hit testing decides what it hits.</summary>
    public void Tap(int x, int y) => manager.Session?.Tap(x, y);

    /// <summary>ANIM 1|0 (smoke steps): animate the jiggler page without HID.</summary>
    public void Animate(bool on) => manager.Session?.Animate(on);

    public void ToggleJiggler() => manager.Session?.SetJiggler(!JigOn);
    public void CycleScale() => manager.Session?.SetScale((jigScale + 1) % 3);
    public void ClearBoardClip() => manager.Session?.ClearClip();
    public void PressButton(bool longPress) => manager.Session?.PressButton(longPress);

    public void RebootToBootloader()
    {
        if (manager.Session is not { } s) return;
        AddLog("Rebooting the board into its bootloader");
        s.RequestBootloader();
    }

    private BundledFirmware? UpdateCandidate()
    {
        var board = State.Firmware is { Known: true } f ? f.Board : "rp2040-169";   // pre-VER boards were all the RP2040 build
        if (State.Device?.Kind == BoardKind.Esp32C3) return null;                     // flashed with PlatformIO, not UF2
        return BundledFirmware.For(Bundled, board);
    }

    private void RefreshUpdateOffer()
    {
        var fw = UpdateCandidate();
        if (Busy)
        {
            CanUpdate = false;
            return;
        }
        if (!IsConnected)
        {
            if (NewBoard is not { } nb) (CanUpdate, UpdateText) = (false, "");
            else if (SelectedModel is { } m && BundledFirmware.For(Bundled, m.Board) is { } nfw)
                (CanUpdate, UpdateText) = (true, $"Found an {nb.Describe()}. Install Touch Deck {nfw.Version} for the {m.Name}?");
            else (CanUpdate, UpdateText) = (false, $"Found an {nb.Describe()}, but this app has no firmware for it.");
        }
        else if (State.Device?.Kind == BoardKind.Esp32C3)
        {
            (CanUpdate, UpdateText) = (false, "ESP32-C3 firmware is updated with PlatformIO.");
        }
        else if (fw is null)
        {
            (CanUpdate, UpdateText) = (false, "");
        }
        else
        {
            bool newer = fw.IsNewerThan(State.Firmware);
            CanUpdate = true;
            UpdateText = newer ? $"Firmware {fw.Version} is available." : $"Up to date (bundled {fw.Version}).";
        }
    }

    public bool UpdateIsUpgrade => UpdateCandidate() is { } fw && fw.IsNewerThan(State.Firmware);

    // Every 2 s while no Touch Deck is connected: a Raspberry Pi board on its factory firmware or
    // in its bootloader (by USB ID). The WMI query runs off the UI thread.
    private async void CheckNewBoards()
    {
        if (Busy || IsConnected)
        {
            if (NewBoard is not null) { NewBoard = null; RefreshUpdateOffer(); }
            return;
        }
        if (scanningNewBoards) return;
        scanningNewBoards = true;
        try
        {
            var found = await Task.Run(() =>
            {
                try { return NewBoards.Scan(); }
                catch (System.Management.ManagementException) { return []; }
            });
            if (disposed || Busy || IsConnected) return;
            var board = found.FirstOrDefault();
            if (board == NewBoard) return;
            NewBoard = board;
            NewBoardModels = board is null ? [] : BoardModels.For(board.Chip);
            SelectedModel = NewBoardModels.FirstOrDefault(m => BundledFirmware.For(Bundled, m.Board) is not null) ?? NewBoardModels.FirstOrDefault();
            if (board is not null) AddLog($"Found an {board.Describe()}");
            RefreshUpdateOffer();
        }
        finally
        {
            scanningNewBoards = false;
        }
    }

    public async Task UpdateFirmwareAsync()
    {
        if (Busy) return;
        if (!IsConnected)
        {
            if (NewBoard is not { } nb || SelectedModel is not { } model || BundledFirmware.For(Bundled, model.Board) is not { } nfw) return;
            // A stock program reboots at 1200 baud; a board already in its bootloader needs nothing.
            Action reboot = nb is { State: NewBoardState.StockFirmware, Port: { } p } ? () => NewBoards.RebootToBootloader(p) : () => { };
            await FlashAsync(Path.Combine(FirmwareDir, nfw.File), $"{nfw.Version} ({nfw.File}) on a new {model.Name}", model, reboot);
            return;
        }
        if (UpdateCandidate() is not { } fw || BoardModels.Find(fw.Board) is not { } m) return;
        await FlashAsync(Path.Combine(FirmwareDir, fw.File), $"{fw.Version} ({fw.File})", m);
    }

    /// <summary>
    /// Installs a UF2 (bundled, or downloaded and verified) for <paramref name="model"/>: chip and model
    /// are checked before the board is touched. <paramref name="enterBootloader"/> overrides BOOT over
    /// the session (a new board has no Touch Deck session).
    /// </summary>
    private async Task<bool> FlashAsync(string uf2, string label, BoardModel model, Action? enterBootloader = null)
    {
        if (Busy) return false;
        Busy = true;
        manager.RequiredBoard = model.Board;      // another RP board (also CAFE:4011) must not take the session
        var old = manager.Session;
        bool ok = false;
        AddLog($"Installing firmware {label}");
        var steps = new UpdateSteps
        {
            EnterBootloader = enterBootloader ?? (() => old?.RequestBootloader()),
            FindBootDrive = () => Uf2.FindBootDrive(Uf2.RemovableRoots(), model.Chip),
            BootloaderChip = () => Uf2.FindBootDrive(Uf2.RemovableRoots(), Uf2Chip.Rp2350) is not null ? Uf2Chip.Rp2350
                                 : Uf2.FindBootDrive(Uf2.RemovableRoots(), Uf2Chip.Rp2040) is not null ? Uf2Chip.Rp2040 : null,
            CopyImage = CopyToBootDrive,
            // Only a new session counts: the old one may not have noticed the reboot yet.
            ReadRunningFirmware = () => manager.Session is { } s && s != old ? s.Firmware : null,
        };
        var progress = new Progress<string>(m => { AddLog(m); UpdateText = m; });
        try
        {
            var result = await FirmwareUpdater.InstallAsync(uf2, model, steps, progress);
            ok = result.Ok;
            AddLog(result.Message);
            Notify?.Invoke(result.Ok ? "Firmware updated" : "Firmware update failed", result.Message);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // e.g. antivirus holding the new file on the boot drive: the board is still in its
            // bootloader, and the app offers the install again from there.
            var msg = $"Firmware update failed: {e.Message}";
            AddLog(msg);
            Notify?.Invoke("Firmware update failed", msg);
        }
        finally
        {
            manager.RequiredBoard = null;
            Busy = false;
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
            if (manager.Session is { } s) s.DiagnosticsEnabled = value;
            SaveSettings(settings with { Diagnostics = value });
            if (!value) DiagnosticItems.Clear();
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

    // ---------- updates ----------

    private readonly UpdateService updates;
    private UpdateChoice? lastChoice;
    private bool checkedOnce;
    private DispatcherTimer? updateTimer;
    public bool UpdateSourceIsTest { get; }

    public string AppUpdateText { get => appUpdateText; private set => Set(ref appUpdateText, value); }
    public string FirmwareUpdateText { get => firmwareUpdateText; private set => Set(ref firmwareUpdateText, value); }
    public bool CanInstallApp { get => canInstallApp; private set => Set(ref canInstallApp, value); }
    public bool CanInstallFirmware { get => canInstallFirmware; private set => Set(ref canInstallFirmware, value); }
    public bool CheckingUpdates { get => checkingUpdates; private set => Set(ref checkingUpdates, value); }
    public string DeviceFirmwareText => IsConnected && State.Firmware is { Known: true } f ? $"{f.Version}  ({f.Board})" : "";
    private string appUpdateText = "Not checked yet", firmwareUpdateText = "";
    private bool canInstallApp, canInstallFirmware, checkingUpdates;

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
        if (CheckingUpdates) return new UpdateCheckOutcome(lastChoice, false, null);
        CheckingUpdates = true;
        if (manual) { AppUpdateText = "Checking..."; FirmwareUpdateText = ""; }
        try
        {
            var device = IsConnected ? State.Firmware : null;
            var outcome = await Task.Run(() => updates.CheckAsync(new Version(AppVersion), device, CancellationToken.None));
            checkedOnce = true;
            SaveSettings(settings with { LastUpdateCheck = DateTime.UtcNow });
            ApplyOutcome(outcome, device);
            return outcome;
        }
        finally
        {
            CheckingUpdates = false;
        }
    }

    private void ApplyOutcome(UpdateCheckOutcome o, FirmwareInfo? device)
    {
        lastChoice = o.Choice;
        if (o.Error is { } err)
        {
            (AppUpdateText, FirmwareUpdateText, CanInstallApp, CanInstallFirmware) = ($"Couldn't check: {err}", "", false, false);
            AddLog($"Update check failed: {err}");
            return;
        }
        if (o.NothingPublished)
        {
            (AppUpdateText, FirmwareUpdateText, CanInstallApp, CanInstallFirmware) = ("No updates published yet", "", false, false);
            return;
        }
        var app = o.Choice?.App;
        AppUpdateText = app is null ? "Up to date" : $"{app.Version.ToString(3)} available";
        CanInstallApp = app is not null;
        // The app flashes the RP boards (UF2); the ESP32-C3 is updated with PlatformIO.
        var fw = o.Choice?.Firmware is { } f && BoardModels.Find(f.Board) is not null ? f : null;
        FirmwareUpdateText = device is not { Known: true } ? "Connect a board to check its firmware"
            : BoardModels.Find(device.Board) is null ? "This board is updated with PlatformIO"
            : fw is null ? "Up to date" : $"{fw.Version.ToString(3)} available";
        CanInstallFirmware = fw is not null && device is not null;
        if (app is not null) Notify?.Invoke("Touch Deck update", $"Version {app.Version.ToString(3)} is available. Open Settings to install it.");
        if (fw is not null) AddLog($"Firmware {fw.Version.ToString(3)} is available for {fw.Board}");
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

    public async Task InstallFirmwareUpdateAsync()
    {
        if (lastChoice?.Firmware is not { } fw || State.Firmware is not { Known: true } dev || dev.Board != fw.Board
            || BoardModels.Find(fw.Board) is not { } model) return;
        CanInstallFirmware = false;
        FirmwareUpdateText = "Downloading...";
        try
        {
            var uf2 = await updates.DownloadFirmwareAsync(fw, null, CancellationToken.None);
            FirmwareUpdateText = "Installing...";
            bool ok = await FlashAsync(uf2, $"{fw.Version.ToString(3)} (downloaded, verified)", model);
            FirmwareUpdateText = ok ? $"Updated to {fw.Version.ToString(3)}" : "Install failed: see the activity log";
            CanInstallFirmware = !ok;
        }
        catch (Exception e) when (e is UpdateVerificationException or IOException or UnauthorizedAccessException)
        {
            FirmwareUpdateText = $"Update failed: {e.Message}";
            CanInstallFirmware = true;
            AddLog(FirmwareUpdateText);
        }
    }


    private void SaveSettings(AppSettings next)
    {
        settings = next;
        try { settings.Save(settingsPath); }
        catch (IOException e) { AddLog($"Couldn't save settings: {e.Message}"); }
        foreach (var name in new[] { nameof(DryRun), nameof(DiagnosticsEnabled), nameof(StartWithWindows), nameof(StartMinimized), nameof(CheckForUpdates) })
            Raise(name);
    }

    // ---------- plumbing ----------

    public void AddLog(string line)
    {
        LogLines.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (LogLines.Count > LogLimit) LogLines.RemoveAt(0);
    }

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

    // ---------- the board's Jiggler settings (firmware 1.8.0+) ----------

    private JigConfig? jigCfg;
    /// <summary>The board's Jiggler settings as it last reported them (STATE); null when not known.</summary>
    public JigConfig? JigCfg
    {
        get => jigCfg;
        private set
        {
            if (Equals(jigCfg, value)) return;
            jigCfg = value;
            foreach (var n in new[] { nameof(JigCfg), nameof(HasJigConfig), nameof(JigNote), nameof(JigMenuOn), nameof(JigF15),
                                      nameof(JigOpenText), nameof(JigPauseText) })
                Raise(n);
        }
    }
    public bool HasJigConfig => JigCfg is not null;
    public string JigNote => JigCfg is not null ? "Saved on the board. Also on the board: the cog on its Jiggler page."
        : IsConnected ? "Update the board's firmware to 1.8.0 or later to change these here."
        : "Connect a board to change its jiggler settings.";
    public bool JigMenuOn => JigCfg?.MenuOn ?? true;
    public bool JigF15 => JigCfg?.F15 ?? false;
    public string JigOpenText => $"{JigCfg?.OpenS ?? 2} s";
    public string JigPauseText => $"{JigCfg?.PauseS ?? 0} s";

    /// <summary>Sends new settings to the board; the dialog shows them once the board reports them back.</summary>
    public void SetJigConfig(JigConfig c)
    {
        if (manager.Session is not { } s || JigCfg is null) return;
        var n = c with { OpenS = Math.Clamp(c.OpenS, 0, JigConfig.MaxSeconds), PauseS = Math.Clamp(c.PauseS, 0, JigConfig.MaxSeconds) };
        s.SetJigConfig(n.MenuOn, n.F15, n.OpenS, n.PauseS);
        AddLog(n.Describe());
    }

    private bool disposed;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        bootDriveWatch.Stop();
        mirrorWatch.Stop();
        updateTimer?.Stop();
        manager.Dispose();               // ends the session, which releases any held key or button
    }
}
