namespace TouchDeck.Core.Mirror;

/// <summary>What a pointer gesture on the device mirror means for the board.</summary>
public abstract record MirrorGesture;

/// <summary>TAP x y, in device pixels: the firmware's own hit testing decides what it hits.</summary>
public sealed record MirrorTap(int X, int Y) : MirrorGesture;

/// <summary>SWIPE L (finger moves left: next page) or SWIPE R.</summary>
public sealed record MirrorSwipe(bool Left) : MirrorGesture;

public static class MirrorInput
{
    /// <summary>Press and release within this many device px: a tap.</summary>
    public const double TapSlop = 12;
    /// <summary>Horizontal travel for a swipe (device px), and it must be mostly horizontal.</summary>
    public const double SwipeMin = 36;

    /// <summary>
    /// Maps a press at (x0, y0) and release at (x1, y1), in device pixels, to a gesture, or null
    /// when it is neither (a short or vertical drag, or a tap off the panel).
    /// </summary>
    public static MirrorGesture? Classify(double x0, double y0, double x1, double y1, int width, int height)
    {
        double dx = x1 - x0, dy = y1 - y0;
        if (Math.Abs(dx) >= SwipeMin && Math.Abs(dx) > 1.5 * Math.Abs(dy)) return new MirrorSwipe(dx < 0);
        if (Math.Sqrt(dx * dx + dy * dy) <= TapSlop)
        {
            if (x0 < 0 || y0 < 0 || x0 >= width || y0 >= height) return null;
            return new MirrorTap((int)Math.Floor(x0), (int)Math.Floor(y0));
        }
        return null;
    }
}
