using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TouchDeck.Core.Devices;
using TouchDeck.Core.Mirror;

namespace TouchDeck.App;

/// <summary>
/// The device mirror: the board's screen as the firmware draws it (native renderer), in a
/// bezel shaped like the board. RP2040: a 240x280 panel behind glass with 44 px corners;
/// ESP32-C3: a 240 px circle. A click is a tap on the panel, a sideways drag a swipe.
/// </summary>
public sealed class DeviceMirror : Grid
{
    public const double Scale = 1.25;
    private const double BezelPad = 14;

    private readonly Border bezel = new();
    private readonly Image screen = new() { Stretch = Stretch.Fill, Cursor = Cursors.Hand };
    private readonly TextBlock placeholder = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(40, 0, 40, 0),
    };
    private WriteableBitmap? bitmap;
    private UiModel kind = UiModel.Rp2040Rect;
    private Point? pressed;

    /// <summary>A click or drag on the panel, in device pixels.</summary>
    public event Action<MirrorGesture>? Gesture;

    public DeviceMirror()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        bezel.Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1F, 0x27));
        bezel.BorderBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x35, 0x42));
        bezel.BorderThickness = new Thickness(1.5);
        bezel.Padding = new Thickness(BezelPad);
        var panel = new Grid();
        panel.Children.Add(screen);
        panel.Children.Add(placeholder);
        bezel.Child = panel;
        Children.Add(bezel);
        RenderOptions.SetBitmapScalingMode(screen, BitmapScalingMode.HighQuality);
        screen.MouseLeftButtonDown += OnDown;
        screen.MouseLeftButtonUp += OnUp;
        screen.LostMouseCapture += (_, _) => pressed = null;
        SetModel(UiModel.Rp2040Rect);
    }

    public UiModel Model => kind;

    /// <summary>Text over a dark panel (no board, or nothing to show yet); null shows the frame.</summary>
    public string? Placeholder
    {
        get => placeholder.Text;
        set
        {
            placeholder.Text = value ?? "";
            placeholder.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
            screen.Opacity = value is null ? 1 : 0;
        }
    }

    public void SetModel(UiModel k)
    {
        kind = k;
        var (w, h) = NativeUi.Size(k);
        double pw = w * Scale, ph = h * Scale;
        screen.Width = pw;
        screen.Height = ph;
        bool round = k.IsRound();
        // The glass: panel corners (RP2040: 44 px) or a circle, and a bezel that follows it.
        double r = round ? pw / 2 : 44 * Scale;
        screen.Clip = new RectangleGeometry(new Rect(0, 0, pw, ph), r, r);
        bezel.CornerRadius = new CornerRadius(round ? (pw + 2 * BezelPad) / 2 : r + BezelPad);
        bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr565, null);
        screen.Source = bitmap;
        placeholder.Foreground = (Brush)Application.Current.FindResource("Faint");
        placeholder.FontSize = 14;
    }

    /// <summary>Shows a frame from the renderer (RGB565, which WPF calls Bgr565).</summary>
    public void Show(UiModel k, ushort[] pixels)
    {
        if (k != kind || bitmap is null) SetModel(k);
        var (w, h) = NativeUi.Size(k);
        bitmap!.WritePixels(new Int32Rect(0, 0, w, h), pixels, w * 2, 0);
    }

    private Point ToDevice(MouseEventArgs e)
    {
        var p = e.GetPosition(screen);
        return new Point(p.X / Scale, p.Y / Scale);
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (screen.Opacity == 0) return;
        pressed = ToDevice(e);
        screen.CaptureMouse();
        e.Handled = true;
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (pressed is not { } a) return;
        pressed = null;
        screen.ReleaseMouseCapture();
        var b = ToDevice(e);
        var (w, h) = NativeUi.Size(kind);
        if (MirrorInput.Classify(a.X, a.Y, b.X, b.Y, w, h) is { } g) Gesture?.Invoke(g);
        e.Handled = true;
    }
}
