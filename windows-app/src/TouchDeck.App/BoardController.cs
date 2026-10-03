using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using TouchDeck.Core.App;
using TouchDeck.Core.Devices;
using TouchDeck.Core.Firmware;
using TouchDeck.Core.Jiggler;
using TouchDeck.Core.Mirror;
using TouchDeck.Core.Protocol;
using TouchDeck.Core.Session;
using TouchDeck.Core.Updates;

namespace TouchDeck.App;

/// <summary>
/// One board's tab: its link, its mirror, its Remote actions, its firmware offers. A Touch Deck
/// port (a <see cref="DeviceManager"/> slot), a board without Touch Deck (a <see cref="NewBoard"/>),
/// or the placeholder shown while nothing is attached. Lives on the UI thread; the
/// <see cref="AppController"/> routes Core events to it.
/// </summary>
public sealed class BoardController : INotifyPropertyChanged
{
    private readonly AppController app;
    private readonly Dispatcher ui;

    public BoardController(AppController app, Dispatcher ui, string key, string? port)
    {
        this.app = app;
        this.ui = ui;
        Key = key;
        Port = port ?? "";
        RefreshTexts();
        RefreshMirror();
    }

    /// <summary>The placeholder that stands for "no board" (never in <see cref="AppController.Boards"/>).</summary>
    public static BoardController Placeholder(AppController app, Dispatcher ui) => new(app, ui, "", null);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The port, or "boot:RP2040" for a board in its bootloader (a drive has no port).</summary>
    public string Key { get; }
    public bool IsPlaceholder => Key.Length == 0;
    /// <summary>Set when the board went away; late events for it are ignored.</summary>
    public bool Detached { get; set; }

    // ---------- link ----------

    public LinkState State { get; private set; } = LinkState.Searching;
    public DeviceSession? Session { get; private set; }
    public Health Health { get => health; private set => Set(ref health, value); }
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    public string BoardName { get => boardName; private set => Set(ref boardName, value); }
    public string Port { get => port; private set => Set(ref port, value); }
    public string FirmwareVersion { get => firmwareVersion; private set => Set(ref firmwareVersion, value); }
    public string FirmwareBuild { get => firmwareBuild; private set => Set(ref firmwareBuild, value); }
    public bool IsConnected { get => isConnected; private set => Set(ref isConnected, value); }
    /// <summary>The tab text: "RP2040 1.69 · COM6".</summary>
    public string Label { get => label; private set => Set(ref label, value); }
    public bool IsSelected { get => isSelected; set { Set(ref isSelected, value); Raise(nameof(TabTag)); } }
    /// <summary>The selected tab's pill is amber (the Primary button style).</summary>
    public string? TabTag => IsSelected ? "Primary" : null;
    public ObservableCollection<KeyValuePair<string, string>> DiagnosticItems { get; } = [];

    private Health health;
    private string statusText = "", boardName = "", port = "", firmwareVersion = "", firmwareBuild = "", label = "";
    private bool isConnected, isSelected;

    /// <summary>A new state for this port from the manager.</summary>
    public void ApplyState(LinkState s)
    {
        var was = State;
        State = s;
        IsConnected = s.Status == LinkStatus.Connected;
        if (IsConnected && NewBoard is not null) SetNewBoard(null);   // its port now runs Touch Deck
        if (!IsConnected)
        {
            Session = null;
            JigCfg = null;
            DiagnosticItems.Clear();
            ResetMirror();
        }
        if (s.Device?.Port is { } p) Port = p;
        RefreshTexts();
        RefreshMirror();
        RefreshUpdateOffer();
        RefreshFeedOffer();

        if (s.Status == was.Status && s.Device == was.Device) return;
        if (IsConnected)
        {
            Log($"Connected: {BoardName}, firmware {FirmwareVersion}");
            app.NotifyBoard(this, "Touch Deck connected", $"{BoardName} on {Port}, firmware {s.Firmware?.Version}");
        }
        else if (was.Status == LinkStatus.Connected)
        {
            Log("Disconnected");
            if (!Busy) app.NotifyBoard(this, "Touch Deck disconnected", "Plug it back in; the app reconnects by itself.");
        }
        else if (s.Status != LinkStatus.Searching)
        {
            Log(StatusText);
        }
    }

    private void RefreshTexts()
    {
        var s = State;
        var kind = s.Device?.Kind ?? BoardKind.Rp2040;
        BoardName = NewBoard is { } nb ? BoardLabels.Model(nb)
            : s.Device is null ? "No board" : BoardKinds.DisplayName(kind, s.Firmware?.Board);
        FirmwareVersion = s.Firmware switch
        {
            null => "",
            { Known: false } => "unknown (older than 1.5.0)",
            var f => $"{f.Version}  ({f.Board})",
        };
        FirmwareBuild = s.Firmware?.Build ?? "";
        Label = NewBoard is { } n ? BoardLabels.Tab(BoardLabels.Model(n), n.Port)
            : BoardLabels.Tab(BoardLabels.Model(kind, s.Firmware?.Board), Port.Length > 0 ? Port : null);
        (Health, StatusText) = NewBoard is not null ? (Health.Idle, "No Touch Deck firmware on this board yet.")
            : s.Status switch
            {
                LinkStatus.Connected => (Health.Ok, "Connected"),
                LinkStatus.PortBusy => (Health.Bad, $"{Port} is in use by another program."),
                LinkStatus.NotResponding => (Health.Bad, $"{Port} doesn't answer. Is it running Touch Deck firmware?"),
                _ => (Health.Idle, Busy ? "Installing firmware..." : IsPlaceholder ? "Looking for a Touch Deck..." : "Reconnecting..."),
            };
        Raise(nameof(DeviceFirmwareText));
        Raise(nameof(JigNote));
    }

    /// <summary>The session came up: hooked by the app before it starts reading.</summary>
    public void Attach(DeviceSession s, MirrorState mirror)
    {
        Session = s;
        s.DiagnosticsEnabled = app.DiagnosticsEnabled;
        StartMirror(mirror);
    }

    public void ShowDiagnostics(IReadOnlyDictionary<string, string> d)
    {
        if (!app.DiagnosticsEnabled || !IsConnected) return;
        DiagnosticItems.Clear();
        foreach (var kv in d) DiagnosticItems.Add(kv);
    }

    /// <summary>What this board's log lines are tagged with: its port, or the label of a board without one.</summary>
    public string LogTag => Port.Length > 0 ? BoardLabels.ShortPort(Port) : Label;

    public void Log(string text) => app.AddLog(LogTag, text);

    // ---------- actions ----------

    public void SendText(string text)
    {
        if (Session is { } s && !string.IsNullOrEmpty(text)) s.SendText(text);
    }

    public void Swipe(bool left) => Session?.Swipe(left);
    /// <summary>A click on the device view: the board's own hit testing decides what it hits.</summary>
    public void Tap(int x, int y) => Session?.Tap(x, y);
    /// <summary>ANIM 1|0 (smoke steps): animate the jiggler page without HID.</summary>
    public void Animate(bool on) => Session?.Animate(on);
    public void ToggleJiggler() => Session?.SetJiggler(!JigOn);
    public void CycleScale() => Session?.SetScale((jigScale + 1) % 3);
    public void ClearBoardClip() => Session?.ClearClip();
    public void PressButton(bool longPress) => Session?.PressButton(longPress);

    public void RebootToBootloader()
    {
        if (Session is not { } s) return;
        Log("Rebooting the board into its bootloader");
        s.RequestBootloader();
    }

    // ---------- board mirror (firmware 1.6.0+) ----------

    public bool JigOn { get => jigOn; private set => Set(ref jigOn, value); }
    public char JigLetter { get => jigLetter; private set => Set(ref jigLetter, value); }
    public double JigX { get => jigX; private set => Set(ref jigX, value); }
    public double JigY { get => jigY; private set => Set(ref jigY, value); }
    public string JigScaleText { get => jigScaleText; private set => Set(ref jigScaleText, value); }
    public string JigStatus { get => jigStatus; private set => Set(ref jigStatus, value); }
    public string BoardClipText { get => boardClipText; private set => Set(ref boardClipText, value); }
    public bool CanClearBoardClip { get => canClearBoardClip; private set => Set(ref canClearBoardClip, value); }
    /// <summary>True when the board streams STATE (jiggler card live).</summary>
    public bool MirrorAvailable { get => mirrorAvailable; private set => Set(ref mirrorAvailable, value); }
    public string MirrorFallbackText { get => mirrorFallbackText; private set => Set(ref mirrorFallbackText, value); }

    private bool jigOn, canClearBoardClip, mirrorAvailable;
    private char jigLetter = 'O';
    private double jigX, jigY;
    private int jigScale;
    private string jigScaleText = "1.0X", jigStatus = "", boardClipText = "", mirrorFallbackText = "";

    public void ApplyBoardState(StateReport st)
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

    /// <summary>Once a second, and on link changes: is the mirror known, and what note goes under it.</summary>
    public void RefreshMirror()
    {
        var supported = Session?.MirrorSupported;
        if (!IsConnected || supported is null)
        {
            if (!IsConnected)
            {
                // A board must not keep its last jiggler state or screen.
                ResetMirror();
                MirrorAvailable = false;
                MirrorFallbackText = NewBoard is not null || Busy ? "" : "Connect a board to see and control its jiggler.";
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
            Log($"Device view unavailable: {error}");
        }
        else RendererError = null;
    }

    public void ApplyMirror(MirrorState m, BoardMessage message)
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
            if (Detached || mirror is null || !FullMirror) return;
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
        if (Session is not { } s || JigCfg is null) return;
        var n = c with { OpenS = Math.Clamp(c.OpenS, 0, JigConfig.MaxSeconds), PauseS = Math.Clamp(c.PauseS, 0, JigConfig.MaxSeconds) };
        s.SetJigConfig(n.MenuOn, n.F15, n.OpenS, n.PauseS);
        Log(n.Describe());
    }

    // ---------- new boards (no Touch Deck firmware yet) ----------

    /// <summary>A Raspberry Pi board without Touch Deck (factory firmware or its bootloader).</summary>
    public NewBoard? NewBoard { get => newBoard; private set => Set(ref newBoard, value); }
    /// <summary>The models the app has firmware for on that board's chip (one choice today per chip).</summary>
    public IReadOnlyList<BoardModel> NewBoardModels { get => newBoardModels; private set => Set(ref newBoardModels, value); }
    public BoardModel? SelectedModel
    {
        get => selectedModel;
        set { Set(ref selectedModel, value); RefreshUpdateOffer(); }
    }

    private NewBoard? newBoard;
    private IReadOnlyList<BoardModel> newBoardModels = [];
    private BoardModel? selectedModel;

    public void SetNewBoard(NewBoard? board)
    {
        if (board == NewBoard) return;
        var first = NewBoard is null && board is not null;
        NewBoard = board;
        NewBoardModels = board is null ? [] : BoardModels.For(board.Chip);
        if (board is not null) MirrorModel = board.Chip == Uf2Chip.Rp2350 ? UiModel.Rp2350Round : UiModel.Rp2040Rect;   // the empty screen in the board's shape
        SelectedModel = NewBoardModels.FirstOrDefault(m => BundledFirmware.For(app.Bundled, m.Board) is not null) ?? NewBoardModels.FirstOrDefault();
        RefreshTexts();
        RefreshMirror();
        RefreshUpdateOffer();
        if (first) Log($"Found an {board!.Describe()}");
    }

    // ---------- bundled firmware: install, update ----------

    public string UpdateText { get => updateText; private set => Set(ref updateText, value); }
    public bool CanUpdate { get => canUpdate; private set => Set(ref canUpdate, value); }
    /// <summary>This board is being installed.</summary>
    public bool Busy { get => busy; private set { Set(ref busy, value); RefreshTexts(); RefreshUpdateOffer(); } }

    private string updateText = "";
    private bool canUpdate, busy;

    private BundledFirmware? UpdateCandidate()
    {
        var board = State.Firmware is { Known: true } f ? f.Board : "rp2040-169";   // pre-VER boards were all the RP2040 build
        if (State.Device?.Kind == BoardKind.Esp32C3) return null;                     // flashed with PlatformIO, not UF2
        return BundledFirmware.For(app.Bundled, board);
    }

    public void RefreshUpdateOffer()
    {
        var fw = UpdateCandidate();
        if (Busy) { CanUpdate = false; return; }
        if (app.Installing is { } other && other != this) { CanUpdate = false; return; }   // one install at a time
        if (!IsConnected)
        {
            if (NewBoard is not { } nb) (CanUpdate, UpdateText) = (false, "");
            else if (SelectedModel is { } m && BundledFirmware.For(app.Bundled, m.Board) is { } nfw)
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

    /// <summary>Install firmware (bundled): a new board gets Touch Deck, a Touch Deck gets the bundled build.</summary>
    public async Task UpdateFirmwareAsync()
    {
        if (Busy || app.Installing is not null) return;
        if (!IsConnected)
        {
            if (NewBoard is not { } nb || SelectedModel is not { } model || BundledFirmware.For(app.Bundled, model.Board) is not { } nfw) return;
            // A stock program reboots at 1200 baud; a board already in its bootloader needs nothing.
            Action reboot = nb is { State: NewBoardState.StockFirmware, Port: { } p } ? () => NewBoards.RebootToBootloader(p) : () => { };
            await InstallAsync(Path.Combine(app.FirmwareDir, nfw.File), $"{nfw.Version} ({nfw.File}) on a new {model.Name}", model, reboot);
            return;
        }
        if (UpdateCandidate() is not { } fw || BoardModels.Find(fw.Board) is not { } m) return;
        await InstallAsync(Path.Combine(app.FirmwareDir, fw.File), $"{fw.Version} ({fw.File})", m);
    }

    private async Task<bool> InstallAsync(string uf2, string label, BoardModel model, Action? enterBootloader = null)
    {
        Busy = true;
        var progress = new Progress<string>(m => UpdateText = m);
        try
        {
            return await app.FlashAsync(this, uf2, label, model, enterBootloader ?? (() => Session?.RequestBootloader()), progress);
        }
        finally
        {
            Busy = false;
        }
    }

    // ---------- firmware from the update feed ----------

    public string FirmwareUpdateText { get => firmwareUpdateText; private set => Set(ref firmwareUpdateText, value); }
    public bool CanInstallFirmware { get => canInstallFirmware; private set => Set(ref canInstallFirmware, value); }
    public string DeviceFirmwareText => IsConnected && State.Firmware is { Known: true } f ? $"{f.Version}  ({f.Board})" : "";
    private FirmwarePackage? feedFirmware;
    private string firmwareUpdateText = "";
    private bool canInstallFirmware;

    /// <summary>This board's offer from the last verified feed (no network: the feed is kept).</summary>
    public void RefreshFeedOffer()
    {
        var device = IsConnected ? State.Firmware : null;
        var feed = app.LastFeed;
        var fw = feed is null ? null : UpdateSelector.Select(feed, new Version(AppController.AppVersion), device).Firmware;
        // The app flashes the RP boards (UF2); the ESP32-C3 is updated with PlatformIO.
        feedFirmware = fw is not null && BoardModels.Find(fw.Board) is not null ? fw : null;
        FirmwareUpdateText = feed is null ? app.FeedNote
            : device is not { Known: true } ? "Connect a board to check its firmware"
            : BoardModels.Find(device.Board) is null ? "This board is updated with PlatformIO"
            : feedFirmware is null ? "Up to date" : $"{feedFirmware.Version.ToString(3)} available";
        CanInstallFirmware = feedFirmware is not null && device is not null && !Busy && app.Installing is null;
    }

    public async Task InstallFirmwareUpdateAsync()
    {
        if (feedFirmware is not { } fw || State.Firmware is not { Known: true } dev || dev.Board != fw.Board
            || BoardModels.Find(fw.Board) is not { } model || Busy || app.Installing is not null) return;
        CanInstallFirmware = false;
        FirmwareUpdateText = "Downloading...";
        try
        {
            var uf2 = await app.DownloadFirmwareAsync(fw);
            FirmwareUpdateText = "Installing...";
            bool ok = await InstallAsync(uf2, $"{fw.Version.ToString(3)} (downloaded, verified)", model);
            FirmwareUpdateText = ok ? $"Updated to {fw.Version.ToString(3)}" : "Install failed: see the activity log";
            CanInstallFirmware = !ok;
        }
        catch (Exception e) when (e is UpdateVerificationException or IOException or UnauthorizedAccessException)
        {
            FirmwareUpdateText = $"Update failed: {e.Message}";
            CanInstallFirmware = true;
            Log(FirmwareUpdateText);
        }
    }

    // ---------- plumbing ----------

    private void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
