namespace TouchDeck.Tests;

public class HarnessTests
{
    [Fact]
    public void Core_assembly_loads()
    {
        Assert.Equal("TouchDeck.Core", typeof(TouchDeck.Core.CoreInfo).Assembly.GetName().Name);
    }
}
