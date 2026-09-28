using System.Globalization;
using System.Text;

namespace TouchDeck.Core.Protocol;

/// <summary>
/// The boards type US-layout ASCII only, so text is transliterated before it
/// is sent (same table and rules as tools/clip_helper.py's to_ascii).
/// </summary>
public static class AsciiText
{
    private static readonly Dictionary<int, string> Subs = new()
    {
        [0x2018] = "'", [0x2019] = "'", [0x201A] = "'", [0x201C] = "\"", [0x201D] = "\"", [0x201E] = "\"",
        [0x2013] = "-", [0x2014] = "-", [0x2212] = "-", [0x2026] = "...", [0x00A0] = " ", [0x2022] = "*",
        [0x00D7] = "x", [0x2192] = "->", [0x2190] = "<-", [0x00AB] = "<<", [0x00BB] = ">>",
    };

    /// <summary>ASCII text, plus how many characters had no equivalent and became '?'.</summary>
    public static (string Text, int Lost) Transliterate(string input)
    {
        var sb = new StringBuilder(input.Length);
        int lost = 0;
        foreach (Rune rune in input.EnumerateRunes())
        {
            if (rune.Value < 0x80) { sb.Append((char)rune.Value); continue; }
            if (Subs.TryGetValue(rune.Value, out var sub)) { sb.Append(sub); continue; }
            // Accented letters fold to their base letter (é -> e); anything else is lost.
            var folded = rune.ToString().Normalize(NormalizationForm.FormKD);
            var ascii = new StringBuilder();
            foreach (char c in folded)
                if (c < 0x80) ascii.Append(c);
            if (ascii.Length > 0) sb.Append(ascii);
            else { sb.Append('?'); lost++; }
        }
        return (sb.ToString(), lost);
    }
}
