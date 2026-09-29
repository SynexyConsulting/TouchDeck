using System.Text;

namespace TouchDeck.Core.Protocol;

/// <summary>PC -> board: new clip text, "CLIP &lt;n&gt; &lt;src&gt;\n" followed by n raw bytes.</summary>
public static class ClipMessage
{
    /// <summary>The boards hold at most this many bytes of clip text.</summary>
    public const int MaxBytes = 8192;

    /// <summary>Frames already-ASCII text; anything past <see cref="MaxBytes"/> is cut.</summary>
    public static byte[] Encode(string asciiText, string source)
    {
        var data = Encoding.ASCII.GetBytes(asciiText);
        if (data.Length > MaxBytes) Array.Resize(ref data, MaxBytes);
        var header = Encoding.ASCII.GetBytes($"CLIP {data.Length} {source}\n");
        return [.. header, .. data];
    }
}
