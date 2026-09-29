using System.Text;
using TouchDeck.Core.Protocol;

namespace TouchDeck.Tests;

public class AsciiTextTests
{
    [Fact]
    public void Smart_punctuation_and_accents_become_ascii()   // clip_helper.to_ascii reference case
    {
        var (text, lost) = AsciiText.Transliterate("“smart” – café…");
        Assert.Equal("\"smart\" - cafe...", text);
        Assert.Equal(0, lost);
    }

    [Fact]
    public void Untypeable_characters_become_question_marks_and_are_counted()
    {
        var (text, lost) = AsciiText.Transliterate("a日b");
        Assert.Equal("a?b", text);
        Assert.Equal(1, lost);
    }

    [Theory]
    [InlineData(" ", " ")]
    [InlineData("•", "*")]
    [InlineData("→", "->")]
    [InlineData("«", "<<")]
    public void Substitution_table_matches_the_python_helper(string input, string expected)
    {
        Assert.Equal(expected, AsciiText.Transliterate(input).Text);
    }
}

public class ClipMessageTests
{
    [Fact]
    public void Clip_is_header_line_then_raw_bytes()
    {
        Assert.Equal("CLIP 2 select\nhi", Encoding.ASCII.GetString(ClipMessage.Encode("hi", "select")));
    }

    [Fact]
    public void Clip_payload_is_capped_at_8192_bytes()
    {
        var bytes = ClipMessage.Encode(new string('x', 9000), "clipbd");
        Assert.StartsWith("CLIP 8192 clipbd\n", Encoding.ASCII.GetString(bytes));
        Assert.Equal("CLIP 8192 clipbd\n".Length + 8192, bytes.Length);
    }
}

public class BoardLineTests
{
    [Fact]
    public void Copy_request() => Assert.IsType<CopyRequest>(BoardLine.Parse("COPY"));

    [Fact]
    public void Pong() => Assert.IsType<Pong>(BoardLine.Parse("PONG"));

    [Fact]
    public void Log_line_keeps_its_text() =>
        Assert.Equal("board up", Assert.IsType<LogLine>(BoardLine.Parse("LOG board up")).Text);

    [Fact]
    public void Key_report_is_hex() =>
        Assert.Equal(new KeyReport(0x02, 0x04), BoardLine.Parse("K 02 04"));

    [Fact]
    public void Mouse_report_has_signed_decimal_deltas() =>
        Assert.Equal(new MouseReport(0x00, -7, 12), BoardLine.Parse("M 00 -7 12"));

    [Fact]
    public void Mouse_deltas_are_clamped_to_a_hid_byte() =>
        Assert.Equal(new MouseReport(0x02, -127, 127), BoardLine.Parse("M 02 -500 500"));

    [Fact]
    public void Version_reply() =>
        Assert.Equal(new VersionReply("rp2040-169", "1.5.0", "2026-09-27"),
                     BoardLine.Parse("VERSION rp2040-169 1.5.0 2026-09-27"));

    [Theory]   // the malformed cases from tools/tests/test_helper.py, plus empty and out-of-range bytes
    [InlineData("K zz 04")]
    [InlineData("K 02")]
    [InlineData("M 00 x 1")]
    [InlineData("M")]
    [InlineData("K -1 04")]
    [InlineData("K 100 04")]
    [InlineData("K 00 -4")]
    [InlineData("M -1 0 0")]
    [InlineData("M 1FF 0 0")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("VERSION rp2040")]
    public void Malformed_lines_are_unknown(string line) => Assert.IsType<UnknownLine>(BoardLine.Parse(line));

    [Fact]
    public void Diagnostics_fields_parse_from_a_dbg_log_line()
    {
        var fields = DbgFields.Parse("up=11s frames=16 screen=2 | touch chip=181 fails=0 | jscale=1.5 lag=7804");
        Assert.Equal("11s", fields["up"]);
        Assert.Equal("181", fields["chip"]);
        Assert.Equal("1.5", fields["jscale"]);
        Assert.Equal("7804", fields["lag"]);
        Assert.False(fields.ContainsKey("|"));
    }
}
