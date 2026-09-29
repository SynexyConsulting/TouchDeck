using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace TouchDeck.App;

/// <summary>
/// Settings (style A): a transparent window laid over the main window, dimming it, with the
/// settings card centred. Closes with ✕, Esc or a click on the dimmed area.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppController app;

    public SettingsWindow(AppController app, Window owner)
    {
        this.app = app;
        InitializeComponent();
        DataContext = app;
        Owner = owner;
        AppVersionValue.Text = AppController.AppVersion;
        CoverOwner(owner);
        app.PropertyChanged += OnAppChanged;
        UpdateDeviceLabel();
        UpdateCheckButton();
    }

    private void CoverOwner(Window owner)
    {
        if (owner.WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Maximized;
            return;
        }
        Left = owner.Left;
        Top = owner.Top;
        Width = owner.ActualWidth;
        Height = owner.ActualHeight;
    }

    private void OnAppChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppController.DeviceFirmwareText)) UpdateDeviceLabel();
        if (e.PropertyName is nameof(AppController.CheckingUpdates)) UpdateCheckButton();
    }

    /// <summary>No board: the label is dimmed and the version left out.</summary>
    private void UpdateDeviceLabel() =>
        DeviceLabel.Foreground = (Brush)FindResource(string.IsNullOrEmpty(app.DeviceFirmwareText) ? "Faint" : "Text");

    private void UpdateCheckButton()
    {
        CheckNow.IsEnabled = !app.CheckingUpdates;
        CheckNow.Content = app.CheckingUpdates ? "Checking..." : "Check for updates now";
    }

    private async void OnCheckNow(object sender, RoutedEventArgs e) => await app.CheckForUpdatesAsync(manual: true);
    private async void OnInstallApp(object sender, RoutedEventArgs e) => await app.InstallAppUpdateAsync();
    private async void OnInstallFirmware(object sender, RoutedEventArgs e) => await app.InstallFirmwareUpdateAsync();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>Renders just the settings card to a PNG (smoke tests).</summary>
    public static void SaveCardSnapshot(SettingsWindow w, string path)
    {
        var card = w.Card;
        var dpi = VisualTreeHelper.GetDpi(card);
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(card.ActualWidth * dpi.DpiScaleX) + 2, (int)(card.ActualHeight * dpi.DpiScaleY) + 2,
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var bg = new DrawingVisual();
        using (var dc = bg.RenderOpen())
            dc.DrawRectangle((Brush)w.FindResource("Bg"), null, new Rect(0, 0, card.ActualWidth + 2, card.ActualHeight + 2));
        bmp.Render(bg);
        var saved = card.Effect;
        card.Effect = null;                       // the shadow would be clipped anyway
        var holder = new VisualBrush(card);
        var v = new DrawingVisual();
        using (var dc = v.RenderOpen()) dc.DrawRectangle(holder, null, new Rect(0, 0, card.ActualWidth, card.ActualHeight));
        bmp.Render(v);
        card.Effect = saved;
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
        png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = System.IO.File.Create(path);
        png.Save(fs);
    }

    private void OnScrimClick(object sender, MouseButtonEventArgs e) => Close();

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        app.PropertyChanged -= OnAppChanged;
        base.OnClosed(e);
    }
}
