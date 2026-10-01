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
            4 => s.Fields.Get("jkey") == 1 ? "F15" : "Esc",
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

    /// <summary>The board's Jiggler settings (firmware 1.8.0+), or null for older firmware.</summary>
    public static JigConfig? Config(StateReport s) =>
        s.Fields.Contains("jmenu")
            ? new JigConfig(s.Fields.Get("jmenu") != 0, s.Fields.Get("jkey") == 1, (int)s.Fields.Get("jopen"), (int)s.Fields.Get("jpause"))
            : null;

    /// <summary>The trash button: same rule as the board's (text, and no paste typing it).</summary>
    public static bool CanClear(StateReport s) => s.ClipLength > 0 && !s.Pasting;
}

/// <summary>
/// The Jiggler settings on the board (its "Jiggler menu" panel): right-click and hold the context menu
/// (or not), ESC or F15, how long the menu stays open and the pause before the next letter (0-60 s).
/// </summary>
public sealed record JigConfig(bool MenuOn, bool F15, int OpenS, int PauseS)
{
    public const int MaxSeconds = 60;

    public string Describe() => MenuOn
        ? $"Jiggler: menu on, {(F15 ? "F15" : "Esc")}, open {OpenS} s, pause {PauseS} s"
        : $"Jiggler: menu off, {(F15 ? "F15" : "Esc")}, pause {PauseS} s";
}
