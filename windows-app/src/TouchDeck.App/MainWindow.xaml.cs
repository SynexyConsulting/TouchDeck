using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TouchDeck.Core.Devices;
using TouchDeck.Core.Mirror;
using TouchDeck.Core.Protocol;
using TouchDeck.Core.Selection;

namespace TouchDeck.App;

public partial class MainWindow : Window
{
    private const int HotkeyId = 0x7D01;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000, VkC = 0x43;
    private const int WmHotkey = 0x0312;

    private readonly AppController app;
    private HwndSource? source;

    /// <summary>Set by Quit: a real close rather than hiding to the tray.</summary>
    public bool AllowClose { get; set; }

    public MainWindow(AppController app)
    {
        this.app = app;
        InitializeComponent();
        DataContext = app;
        Title = $"Touch Deck {AppController.AppVersion}";

        app.PropertyChanged += OnAppChanged;
        app.LogLines.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        };
        app.HistoryItems.CollectionChanged += (_, _) => UpdateHistoryEmpty();
        UpdateHistoryEmpty();
        UpdateStatusDot();
        UpdateDiagnosticsCard();
        UpdateSendInfo();
        UpdateJigPill();
        Mirror.Gesture += OnMirrorGesture;
        app.MirrorFrameChanged += ShowMirrorFrame;
        UpdateMirror();
    }

    /// <summary>Creates the window handle without showing it, so the hotkey works from the tray.</summary>
    public void Attach()
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        source = HwndSource.FromHwnd(handle);
        source.AddHook(WndProc);
        UseDarkTitleBar(handle);
        if (!RegisterHotKey(handle, HotkeyId, ModControl | ModAlt | ModNoRepeat, VkC))
            app.AddLog("Ctrl+Alt+C is taken by another program; the send-selection hotkey is off");
    }

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>Renders the window's content to a PNG (smoke tests, bug reports).</summary>
    public void SaveSnapshot(string path)
    {
        var content = (FrameworkElement)Content;
        var m = content.Margin;                     // the render includes the margin offset
        double w = content.ActualWidth + m.Left + m.Right, h = content.ActualHeight + m.Top + m.Bottom;
        var dpi = VisualTreeHelper.GetDpi(this);
        var bmp = new RenderTargetBitmap((int)(w * dpi.DpiScaleX), (int)(h * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
            dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));
        bmp.Render(bg);
        bmp.Render(content);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        png.Save(f);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            app.SendSelection();
            handled = true;
        }
        return IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;              // keep running in the tray
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (source is not null) UnregisterHotKey(source.Handle, HotkeyId);
        base.OnClosed(e);
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppController.Health):
                UpdateStatusDot();
                break;
            case nameof(AppController.JigOn):
                UpdateJigPill();
                break;
            case nameof(AppController.MirrorFallbackText):
            case nameof(AppController.MirrorAvailable):
            case nameof(AppController.FullMirror):
            case nameof(AppController.MirrorKind):
                UpdateMirror();
                break;
            case nameof(AppController.DiagnosticsEnabled):
            case nameof(AppController.IsConnected):
                UpdateDiagnosticsCard();
                UpdateSendInfo();
                UpdateMirror();
                break;
            case nameof(AppController.UpdateText):
                UpdateButton.Content = app.IsConnected && !app.UpdateIsUpgrade ? "Reinstall firmware" : "Install firmware";
                break;
        }
    }

    private void UpdateStatusDot() => StatusDot.Fill = (Brush)FindResource(app.Health switch
    {
        Health.Ok => "Ok",
        Health.Bad => "Bad",
        _ => "Faint",
    });

    private void UpdateDiagnosticsCard() =>
        DiagCard.Visibility = app.DiagnosticsEnabled && app.IsConnected ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateHistoryEmpty() =>
        HistoryEmpty.Visibility = app.HistoryItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateSendInfo()
    {
        var text = SendBox.Text;
        SendButton.IsEnabled = app.IsConnected && text.Length > 0;
        if (!app.IsConnected)
        {
            SendInfo.Text = "Connect a board to send text.";
            return;
        }
        var (ascii, lost) = AsciiText.Transliterate(text);
        var parts = new List<string> { $"{ascii.Length} characters" };
        if (lost > 0) parts.Add($"{lost} will be typed as '?'");
        if (ascii.Length > ClipMessage.MaxBytes) parts.Add($"only the first {ClipMessage.MaxBytes} fit");
        SendInfo.Text = text.Length == 0 ? "The board types this text when you tap PASTE." : string.Join(" · ", parts);
    }

    // ---------- handlers ----------

    private void OnSendTextChanged(object sender, TextChangedEventArgs e) => UpdateSendInfo();

    private void OnSend(object sender, RoutedEventArgs e) => app.SendText(SendBox.Text);

    private void OnHistoryResend(object sender, RoutedEventArgs e) => app.SendText((string)((Button)sender).Tag);

    private void OnHistoryCopy(object sender, RoutedEventArgs e)
    {
        if (Win32Clipboard.Write((string)((Button)sender).Tag)) app.AddLog("Copied a recent clip to the Windows clipboard");
    }

    private void OnClearHistory(object sender, RoutedEventArgs e) => app.History.Clear();

    private void OnCopyLog(object sender, RoutedEventArgs e) => Win32Clipboard.Write(string.Join(Environment.NewLine, app.LogLines));

    private void OnSettings(object sender, RoutedEventArgs e) => OpenSettings();

    /// <summary>The Settings overlay (modal); returns once it is closed.</summary>
    public void OpenSettings()
    {
        ShowFromTray();
        new SettingsWindow(app, this).ShowDialog();
    }

    private void OnJigToggle(object sender, RoutedEventArgs e) => app.ToggleJiggler();
    private void OnLaneClick(object sender, MouseButtonEventArgs e) => app.ToggleJiggler();
    private void OnScale(object sender, RoutedEventArgs e) => app.CycleScale();
    private void OnClearBoardClip(object sender, RoutedEventArgs e) => app.ClearBoardClip();

    private void UpdateJigPill()
    {
        JigPill.Content = app.JigOn ? "ON" : "OFF";
        JigPill.Tag = app.JigOn ? "Primary" : null;
    }

    /// <summary>
    /// The device card: the live device view (firmware 1.7.0+), the jiggler card for firmware 1.6.x
    /// (whose STATE has no page), or the empty device with a note.
    /// </summary>
    private void UpdateMirror()
    {
        bool full = app.FullMirror, legacy = app.IsConnected && !full && app.MirrorAvailable;
        Mirror.Visibility = legacy ? Visibility.Collapsed : Visibility.Visible;
        MirrorHint.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        LegacyJiggler.Visibility = legacy ? Visibility.Visible : Visibility.Collapsed;
        MirrorFallback.Visibility = string.IsNullOrEmpty(app.MirrorFallbackText) ? Visibility.Collapsed : Visibility.Visible;
        BootButtons.Visibility = app.IsConnected && app.MirrorKind == BoardKind.Esp32C3 ? Visibility.Collapsed : Visibility.Visible;
        if (Mirror.Kind != app.MirrorKind) Mirror.SetKind(app.MirrorKind);
        if (!full || app.MirrorFrame is null)
            Mirror.Placeholder = !app.IsConnected ? "Connect a Touch Deck" : full ? "" : app.MirrorAvailable ? null : "Waiting for the board...";
    }

    private void ShowMirrorFrame()
    {
        if (app.MirrorFrame is { } frame && app.FullMirror)
        {
            Mirror.Show(app.MirrorKind, frame);
            Mirror.Placeholder = null;
        }
        else UpdateMirror();
    }

    private void OnMirrorGesture(MirrorGesture g)
    {
        switch (g)
        {
            case MirrorTap t: app.Tap(t.X, t.Y); break;
            case MirrorSwipe sw: app.Swipe(sw.Left); break;
        }
    }

    /// <summary>The device view at 1:1 as a PNG (smoke tests: compare with the board).</summary>
    public bool SaveMirror(string path)
    {
        if (app.MirrorFrame is not { } frame || !app.FullMirror) return false;
        var (w, h) = NativeUi.Size(app.MirrorKind);
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgr565, null, frame, w * 2);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        using var f = File.Create(path);
        png.Save(f);
        return true;
    }

    private void OnSwipeLeft(object sender, RoutedEventArgs e) => app.Swipe(left: true);
    private void OnSwipeRight(object sender, RoutedEventArgs e) => app.Swipe(left: false);
    private void OnButton(object sender, RoutedEventArgs e) => app.PressButton(longPress: false);
    private void OnLongButton(object sender, RoutedEventArgs e) => app.PressButton(longPress: true);

    private void OnBootloader(object sender, RoutedEventArgs e)
    {
        var ok = MessageBox.Show(this,
            "The board will restart as a USB drive (RPI-RP2) and stop working as a Touch Deck until firmware is copied to it or it is unplugged and replugged.",
            "Reboot to bootloader", MessageBoxButton.OKCancel, MessageBoxImage.Information);
        if (ok == MessageBoxResult.OK) app.RebootToBootloader();
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        var ok = MessageBox.Show(this,
            "The board restarts into its bootloader, the bundled firmware is copied to it, and it reconnects. Keep it plugged in. Continue?",
            "Install firmware", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (ok == MessageBoxResult.OK) await app.UpdateFirmwareAsync();
    }

    // ---------- Win32 ----------

    private static void UseDarkTitleBar(IntPtr hwnd)
    {
        int on = 1;
        _ = DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int));
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
