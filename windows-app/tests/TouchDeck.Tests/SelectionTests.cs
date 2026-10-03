using TouchDeck.Core.Selection;

namespace TouchDeck.Tests;

public class SelectionTests
{
    private sealed class Fixed(string? text) : ITextSource
    {
        public int Reads { get; private set; }
        public string? Read() { Reads++; return text; }
    }

    private sealed class Hangs : ITextSource
    {
        public string? Read() { Thread.Sleep(2000); return "late"; }
    }

    [Fact]
    public void Selection_wins_when_there_is_one()
    {
        var clip = new Fixed("clipboard");
        Assert.Equal(("picked", "select"), new SelectionProvider(new Fixed("picked"), clip).Grab());
        Assert.Equal(0, clip.Reads);
    }

    [Theory]
    [InlineData(null)]     // app has no TextPattern
    [InlineData("")]       // TextPattern but nothing selected
    public void Falls_back_to_the_clipboard(string? selection) =>
        Assert.Equal(("clipboard", "clipbd"), new SelectionProvider(new Fixed(selection), new Fixed("clipboard")).Grab());

    [Fact]
    public void Unreadable_clipboard_sends_empty_text() =>
        Assert.Equal(("", "clipbd"), new SelectionProvider(new Fixed(null), new Fixed(null)).Grab());

    [Fact]
    public void A_hung_automation_call_is_abandoned()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.Null(new TimeBoxedSource(new Hangs(), TimeSpan.FromMilliseconds(100)).Read());
        Assert.True(sw.ElapsedMilliseconds < 1000);
    }

    [Fact]
    public void Clipboard_round_trips_unicode_on_this_machine()
    {
        var clip = new Win32Clipboard();
        var saved = clip.Read();
        try
        {
            Assert.True(Win32Clipboard.Write("Touch Deck é– test"));
            Assert.Equal("Touch Deck é– test", clip.Read());
        }
        finally
        {
            if (saved is not null) Win32Clipboard.Write(saved);
        }
    }

    [Fact]
    public void Uia_read_does_not_throw_whatever_has_focus() =>
        _ = new TimeBoxedSource(new UiaSelection(), TimeSpan.FromSeconds(2)).Read();

    [SkippableFact]
    public void With_Touch_Deck_in_front_reads_the_control_last_focused_in_another_app()
    {
        Skip.If(Environment.GetEnvironmentVariable("GITHUB_ACTIONS") is not null, "needs an interactive desktop");
        var tracker = FocusTracker.Shared;                   // watching before the other app takes focus
        using var other = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
            "-NoProfile -Command \"Add-Type -A System.Windows.Forms; $f = New-Object Windows.Forms.Form; " +
            "$t = New-Object Windows.Forms.TextBox; $t.Text = 'hello focus test'; $f.Controls.Add($t); " +
            "$f.Add_Shown({ $f.Activate(); $t.Focus(); $t.SelectAll() }); [Windows.Forms.Application]::Run($f)\"")
            { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            string? text = null;
            for (var sw = System.Diagnostics.Stopwatch.StartNew(); sw.Elapsed < TimeSpan.FromSeconds(15) && text != "hello focus test";)
            {
                Thread.Sleep(200);
                text = new UiaSelection(tracker, ownWindowInFront: () => true).Read();
            }
            Assert.Equal("hello focus test", text);
        }
        finally
        {
            other.Kill();
        }
    }
}
