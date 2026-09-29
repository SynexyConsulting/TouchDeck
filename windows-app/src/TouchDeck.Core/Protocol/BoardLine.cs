using System.Globalization;

namespace TouchDeck.Core.Protocol;

/// <summary>A line sent by a Touch Deck board (protocol: src/usb_io.c in the firmware repo).</summary>
public abstract record BoardMessage;

/// <summary>The user tapped COPY: send the PC's selected text.</summary>
public sealed record CopyRequest : BoardMessage;

/// <summary>Answer to HELLO / PING.</summary>
public sealed record Pong : BoardMessage;

/// <summary>Debug or diagnostics text (DBG replies arrive as LOG lines).</summary>
public sealed record LogLine(string Text) : BoardMessage;

/// <summary>PC output mode keyboard report: HID modifier byte and usage (0 = release all).</summary>
public sealed record KeyReport(int Mods, int Usage) : BoardMessage;

/// <summary>PC output mode mouse report: HID button byte and relative motion.</summary>
public sealed record MouseReport(int Buttons, int Dx, int Dy) : BoardMessage;

/// <summary>Answer to VER.</summary>
public sealed record VersionReply(string Board, string Version, string Build) : BoardMessage;

/// <summary>Live board state after WATCH 1 (firmware 1.6.0+). X/Y: jiggler dot in 0..1000 letter-box units.</summary>
public sealed record StateReport(bool JigOn, char Letter, int Scale, int Phase, int X, int Y, int ClipLength, bool Pasting)
    : BoardMessage;

/// <summary>Anything else, including malformed K/M lines (ignored, never half-applied).</summary>
public sealed record UnknownLine(string Raw) : BoardMessage;

public static class BoardLine
{
    public static BoardMessage Parse(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return new UnknownLine(line);
        switch (parts[0])
        {
            case "COPY" when parts.Length == 1:
                return new CopyRequest();
            case "PONG" when parts.Length == 1:
                return new Pong();
            case "LOG":
                return new LogLine(line.Length > 4 ? line[4..] : "");
            case "K" when parts.Length == 3 && TryByte(parts[1], out var mods) && TryByte(parts[2], out var usage):
                return new KeyReport(mods, usage);
            case "M" when parts.Length == 4 && TryByte(parts[1], out var buttons)
                          && int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var dx)
                          && int.TryParse(parts[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var dy):
                return new MouseReport(buttons, Math.Clamp(dx, -127, 127), Math.Clamp(dy, -127, 127));
            case "STATE" when TryState(parts, out var st):
                return st;
            case "VERSION" when parts.Length >= 3:
                return new VersionReply(parts[1], parts[2], string.Join(' ', parts.Skip(3)));
            default:
                return new UnknownLine(line);
        }
    }

    private static bool TryState(string[] parts, out StateReport state)
    {
        state = null!;
        var f = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in parts.Skip(1))
        {
            int eq = p.IndexOf('=');
            if (eq > 0) f[p[..eq]] = p[(eq + 1)..];
        }
        int Num(string k) => f.TryGetValue(k, out var v) && int.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) ? n : int.MinValue;
        int jig = Num("jig"), scale = Num("scale"), phase = Num("phase"), x = Num("x"), y = Num("y"), clip = Num("clip"), paste = Num("paste");
        if (!f.TryGetValue("letter", out var letter) || letter.Length != 1 ||
            new[] { jig, scale, phase, x, y, clip, paste }.Contains(int.MinValue)) return false;
        state = new StateReport(jig == 1, letter[0], scale, phase, x, y, clip, paste == 1);
        return true;
    }

    // One report byte: hex, 0..FF. Signs and anything larger make the line malformed.
    private static bool TryByte(string s, out int value) =>
        int.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value) && value <= 0xFF;
}

/// <summary>key=value fields of a DBG diagnostics line (the '|' separators are ignored).</summary>
public static class DbgFields
{
    public static IReadOnlyDictionary<string, string> Parse(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = token.IndexOf('=');
            if (eq > 0) fields[token[..eq]] = token[(eq + 1)..];
        }
        return fields;
    }
}
