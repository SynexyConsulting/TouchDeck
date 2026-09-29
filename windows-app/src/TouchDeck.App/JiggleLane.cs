using System.Windows;
using System.Windows.Media;
using TouchDeck.Core.Jiggler;

namespace TouchDeck.App;

/// <summary>A jiggler letter drawn like the device: wide accent stroke, inner lane, dot.</summary>
public sealed class JiggleLane : FrameworkElement
{
    public static readonly DependencyProperty LetterProperty = Reg(nameof(Letter), 'O');
    public static readonly DependencyProperty DotXProperty = Reg(nameof(DotX), 0.0);
    public static readonly DependencyProperty DotYProperty = Reg(nameof(DotY), 0.0);
    public static readonly DependencyProperty ActiveProperty = Reg(nameof(Active), false);

    public char Letter { get => (char)GetValue(LetterProperty); set => SetValue(LetterProperty, value); }
    public double DotX { get => (double)GetValue(DotXProperty); set => SetValue(DotXProperty, value); }
    public double DotY { get => (double)GetValue(DotYProperty); set => SetValue(DotYProperty, value); }
    public bool Active { get => (bool)GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }

    private static DependencyProperty Reg<T>(string name, T def) => DependencyProperty.Register(name, typeof(T),
        typeof(JiggleLane), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnRender(DrawingContext dc)
    {
        if (JigPaths.Find(Letter) is not { } l) return;
        // Same proportions as the RP2040 screen: 98 px box, 18 px lane, 2 px walls, 5.5 px dot.
        double size = Math.Min(ActualWidth, ActualHeight) * 98 / 120, k = size / 98;
        double ox = (ActualWidth - size) / 2, oy = (ActualHeight - size) / 2;
        Point P(double x, double y) => new(ox + x * size / 1000, oy + y * size / 1000);
        var geo = new StreamGeometry();
        using (var g = geo.Open())
        {
            g.BeginFigure(P(l.Points[0].X, l.Points[0].Y), false, l.Closed);
            g.PolyLineTo(l.Points.Skip(1).Select(p => P(p.X, p.Y)).ToList(), true, true);
        }
        geo.Freeze();
        var accent = (Brush)FindResource(Active ? "Accent" : "Surf2");
        dc.DrawGeometry(null, new Pen(accent, (18 + 4) * k) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geo);
        dc.DrawGeometry(null, new Pen((Brush)FindResource("Inner"), 18 * k) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, geo);
        if (Active) dc.DrawEllipse((Brush)FindResource("Accent"), null, P(DotX, DotY), 5.5 * k, 5.5 * k);
    }
}
