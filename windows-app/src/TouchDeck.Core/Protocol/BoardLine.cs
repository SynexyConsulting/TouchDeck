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

/// <summary>
/// Live board state after WATCH 1 (firmware 1.6.0+). X/Y: jiggler dot in 0..1000 letter-box units.
/// <see cref="Fields"/> holds every numeric key=value of the line, including the 1.7.0 device-mirror
/// fields (page, t, link, ...); <c>page</c> is present from 1.7.0 on.
/// </summary>
public sealed record StateReport(bool JigOn, char Letter, int Scale, int Phase, int X, int Y, int ClipLength, bool Pasting)
    : BoardMessage
{
    public StateFields Fields { get; init; } = StateFields.Empty;

    /// <summary>Firmware 1.7.0+: the line carries the whole UI state (the device mirror).</summary>
    public bool IsFullMirror => Fields.Contains("page");
}

/// <summary>A STATE line's numeric fields; compared by value.</summary>
public sealed class StateFields : IEquatable<StateFields>
{
    private readonly SortedDictionary<string, long> values;

    public static StateFields Empty { get; } = new(new SortedDictionary<string, long>(StringComparer.Ordinal));

    public StateFields(IDictionary<string, long> values) => this.values = new(values, StringComparer.Ordinal);

    public bool Contains(string key) => values.ContainsKey(key);
    public long Get(string key, long fallback = 0) => values.TryGetValue(key, out var v) ? v : fallback;
    public IEnumerable<string> Keys => values.Keys;

    public bool Equals(StateFields? other) => other is not null && values.Count == other.values.Count &&
                                              values.All(kv => other.values.TryGetValue(kv.Key, out var v) && v == kv.Value);
    public override bool Equals(object? obj) => Equals(obj as StateFields);
    public override int GetHashCode() => values.Aggregate(values.Count, (h, kv) => HashCode.Combine(h, kv.Key, kv.Value));
}

/// <summary>TEXT key value (firmware 1.7.0+): a string field of the device mirror (msg, src, host, down).</summary>
public sealed record TextField(string Key, string Value) : BoardMessage;

/// <summary>CLIPTEXT (firmware 1.7.0+): the first 1024 bytes of the board's clip, unescaped.</summary>
public sealed record ClipText(byte[] Bytes) : BoardMessage
{
    public bool Equals(ClipText? other) => other is not null && Bytes.AsSpan().SequenceEqual(other.Bytes);
    public override int GetHashCode() => Bytes.Length;
}

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
            case "TEXT" when parts.Length >= 2:
                var head = "TEXT " + parts[1];
                return new TextField(parts[1], line.Length > head.Length + 1 && line.StartsWith(head + " ", StringComparison.Ordinal)
                    ? line[(head.Length + 1)..] : "");
            case "CLIPTEXT":
                return new ClipText(ClipEscape.Unescape(line.Length > 9 ? line[9..] : ""));
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
        var numbers = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (k, v) in f)
            if (long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) numbers[k] = n;
        state = new StateReport(jig == 1, letter[0], scale, phase, x, y, clip, paste == 1) { Fields = new StateFields(numbers) };
        return true;
    }

    // One report byte: hex, 0..FF. Signs and anything larger make the line malformed.
    private static bool TryByte(string s, out int value) =>
        int.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value) && value <= 0xFF;
}

/// <summary>CLIPTEXT escapes (ui_sync.c): backslash-backslash, \n, \r, \t and \xHH.</summary>
public static class ClipEscape
{
    public static byte[] Unescape(string s)
    {
        var o = new List<byte>(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                char n = s[++i];
                if (n == 'x' && i + 2 < s.Length &&
                    byte.TryParse(s.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var b))
                {
                    o.Add(b);
                    i += 2;
                }
                else o.Add(n switch { 'n' => (byte)'\n', 'r' => (byte)'\r', 't' => (byte)'\t', _ => (byte)n });
            }
            else o.Add((byte)c);
        }
        return [.. o];
    }
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
