using TouchDeck.Core.Jiggler;
using TouchDeck.Core.Protocol;
namespace TouchDeck.Tests;
public class JigPathsTests
{
    [Fact]
    public void Has_the_fifteen_letters_with_loop_flags()
    {
        Assert.Equal("OWMNZXCVHJLBGD", string.Concat(JigPaths.All.Select(l => l.Name)));
        Assert.Equal("OBD", string.Concat(JigPaths.All.Where(l => l.Closed).Select(l => l.Name)));
        Assert.All(JigPaths.All, l => Assert.All(l.Points, p => Assert.InRange(p.X, 0, 1000)));
        Assert.Null(JigPaths.Find('Q'));
    }
}

public class JigViewTests
{
    private static StateReport S(int phase = 0, bool on = true, int clip = 0, bool paste = false, int scale = 0) =>
        new(on, 'W', scale, phase, 500, 500, clip, paste);

    [Theory]
    [InlineData(0, "Moving")]
    [InlineData(1, "Pausing")]
    [InlineData(2, "Right-click menu")]
    [InlineData(3, "Right-click menu")]
    [InlineData(4, "Esc")]
    [InlineData(5, "Resuming")]
    public void Status_follows_the_phase(int phase, string text) => Assert.Equal(text, JigView.Status(S(phase)));

    [Fact]
    public void Status_when_off_or_pasting()
    {
        Assert.Equal("Off", JigView.Status(S(on: false)));
        Assert.Equal("Paused: pasting", JigView.Status(S(paste: true)));
    }

    [Theory]
    [InlineData(0, "1.0X")]
    [InlineData(1, "1.5X")]
    [InlineData(2, "2.0X")]
    [InlineData(7, "?")]
    public void Scale_text(int scale, string text) => Assert.Equal(text, JigView.ScaleText(scale));

    [Fact]
    public void Board_clip_line_and_trash()
    {
        Assert.Equal("Board clip: empty", JigView.ClipText(S(clip: 0)));
        Assert.Equal("Board clip: 58 chars", JigView.ClipText(S(clip: 58)));
        Assert.Equal("Board clip: 1 char", JigView.ClipText(S(clip: 1)));
        Assert.False(JigView.CanClear(S(clip: 0)));
        Assert.True(JigView.CanClear(S(clip: 58)));
        Assert.False(JigView.CanClear(S(clip: 58, paste: true)));
    }
}
