using TouchDeck.Core.Protocol;

namespace TouchDeck.Core.Jiggler;

/// <summary>What the app shows for a board STATE report (same wording as the device where it overlaps).</summary>
public static class JigView
{
    private static readonly string[] Scales = ["1.0X", "1.5X", "2.0X"];

    public static string ScaleText(int scale) => scale >= 0 && scale < Scales.Length ? Scales[scale] : "?";

    public static string Status(StateReport s)
    {
        if (!s.JigOn) return "Off";
        if (s.Pasting) return "Paused: pasting";
        return s.Phase switch
        {
            0 => "Moving",
            1 => "Pausing",
            2 or 3 => "Right-click menu",
            4 => "Esc",
            5 => "Resuming",
            _ => "",
        };
    }

    public static string ClipText(StateReport s) => s.ClipLength switch
    {
        0 => "Board clip: empty",
        1 => "Board clip: 1 char",
        var n => $"Board clip: {n} chars",
    };

    /// <summary>The trash button: same rule as the board's (text, and no paste typing it).</summary>
    public static bool CanClear(StateReport s) => s.ClipLength > 0 && !s.Pasting;
}
