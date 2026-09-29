using TouchDeck.Core.Jiggler;
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
