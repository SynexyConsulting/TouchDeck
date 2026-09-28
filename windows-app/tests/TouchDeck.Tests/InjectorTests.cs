using TouchDeck.Core.Input;

namespace TouchDeck.Tests;

/// <summary>Same cases as tools/tests/test_inject.py in the firmware repo.</summary>
public class InjectorTests
{
    private readonly RecordingSink sink = new();
    private readonly Injector inj;

    public InjectorTests() => inj = new Injector(sink);

    private static KeyStroke Down(int scan, bool ext = false) => new(scan, ext, Up: false);
    private static KeyStroke Up(int scan, bool ext = false) => new(scan, ext, Up: true);

    [Fact]
    public void Shifted_letter_press_and_release()
    {
        inj.Key(0x02, 0x04);                                  // Shift + a -> 'A'
        Assert.Equal([Down(0x2A), Down(0x1E)], sink.Events);
        sink.Events.Clear();
        inj.Key(0x00, 0x00);                                  // all up: key first, then Shift
        Assert.Equal([Up(0x1E), Up(0x2A)], sink.Events);
    }

    [Fact]
    public void Escape_and_enter()
    {
        inj.Key(0, 0x29); inj.Key(0, 0); inj.Key(0, 0x28);
        Assert.Contains(Down(0x01), sink.Events);
        Assert.Contains(Down(0x1C), sink.Events);
    }

    [Fact]
    public void Shift_released_before_next_unshifted_key()
    {
        inj.Key(0x02, 0x04);                                  // 'A' held
        sink.Events.Clear();
        inj.Key(0x00, 0x05);                                  // straight to 'b', no release report
        Assert.Equal([Up(0x1E), Up(0x2A), Down(0x30)], sink.Events);
    }

    [Fact]
    public void Unknown_usage_is_ignored()
    {
        inj.Key(0, 0x99);
        Assert.Empty(sink.Events);
    }

    [Fact]
    public void Unknown_usage_with_modifier_holds_nothing()
    {
        inj.Key(0x02, 0x99);
        Assert.Empty(sink.Events);
        Assert.Equal(0, inj.HeldMods);
    }

    [Fact]
    public void Mouse_move_and_right_button_edges()
    {
        inj.Mouse(0, 5, -3);
        inj.Mouse(0x02, 0, 0);
        inj.Mouse(0x00, 0, 0);
        Assert.Equal<InputEvent>(
            [new MouseMove(5, -3), new MouseButton(MouseAction.RightDown), new MouseButton(MouseAction.RightUp)],
            sink.Events);
    }

    [Fact]
    public void Release_all_lets_go_of_everything()
    {
        inj.Key(0x02, 0x04); inj.Mouse(0x02, 0, 0);
        sink.Events.Clear();
        inj.ReleaseAll();
        Assert.Contains(Up(0x1E), sink.Events);
        Assert.Contains(Up(0x2A), sink.Events);
        Assert.Contains(new MouseButton(MouseAction.RightUp), sink.Events);
    }

    [Fact]
    public void Gui_and_right_side_modifiers_are_extended_keys()
    {
        inj.Key(0x08 | 0x10, 0x04);                           // left GUI + right Ctrl
        Assert.Contains(Down(0x5B, ext: true), sink.Events);
        Assert.Contains(Down(0x1D, ext: true), sink.Events);
    }

    [Fact]
    public void Input_struct_matches_the_win64_layout()
    {
        Assert.Equal(Environment.Is64BitProcess ? 40 : 28, SendInputSink.InputStructSize);
    }
}

public class SwitchableSinkTests
{
    [Fact]
    public void Dry_run_describes_instead_of_injecting()
    {
        var real = new RecordingSink();
        var sink = new SwitchableSink(real);
        var described = new List<string>();
        sink.DryRunEvent += described.Add;

        sink.Send([new KeyStroke(0x1E, false, false)]);
        sink.DryRun = true;
        sink.Send([new KeyStroke(0x1D, true, true), new MouseMove(3, -2), new MouseButton(MouseAction.RightDown)]);

        Assert.Single(real.Events);
        Assert.Equal(["key 0x1D ext up", "mouse move +3 -2", "mouse RightDown"], described);
    }
}
