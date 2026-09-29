using System.Text;
using TouchDeck.Core.Devices;
using TouchDeck.Core.Input;
using TouchDeck.Core.Protocol;
using TouchDeck.Core.Session;

namespace TouchDeck.Tests;

internal sealed class FakeTransport : ISerialTransport
{
    public Queue<string> Incoming { get; } = new();
    public List<string> Written { get; } = [];
    public bool FailReads { get; set; }
    public bool Opened { get; private set; }

    public void Open() => Opened = true;
    public void WriteLine(string line) => Written.Add(line);
    public void Write(byte[] data) => Written.Add(Encoding.ASCII.GetString(data));
    public string? ReadLine(TimeSpan timeout)
    {
        if (FailReads) throw new IOException("device unplugged");
        return Incoming.Count > 0 ? Incoming.Dequeue() : null;
    }
    public void Dispose() { }
}

internal sealed class FakeClock : IClock
{
    public DateTime Now { get; set; } = new(2026, 9, 27, 21, 5, 9);
}

internal sealed class FakeKeyboard : IKeyboardState
{
    public bool CapsLock { get; set; }
}

internal sealed class FakeSelection(string text, string source) : ISelectionProvider
{
    public (string Text, string Source) Grab() => (text, source);
}

public class SessionTests
{
    private readonly FakeTransport t = new();
    private readonly FakeClock clock = new();
    private readonly FakeKeyboard kb = new();
    private readonly RecordingSink sink = new();

    private DeviceSession Make(string selection = "héllo", string source = "select") =>
        new(t, new Injector(sink), kb, new FakeSelection(selection, source), clock);

    [Fact]
    public void Handshake_says_hello_asks_version_and_sets_the_time()
    {
        t.Incoming.Enqueue("PONG");
        t.Incoming.Enqueue("VERSION rp2040-169 1.5.0 Sep 27 2026");
        var s = Make();
        Assert.True(s.Handshake());
        Assert.Equal(new FirmwareInfo("rp2040-169", "1.5.0", "Sep 27 2026"), s.Firmware);
        Assert.Contains("HELLO", t.Written);
        Assert.Contains("VER", t.Written);
        Assert.Contains("TIME 21:05:09", t.Written);
    }

    [Fact]
    public void Handshake_fails_without_pong() => Assert.False(Make().Handshake());

    [Fact]
    public void Firmware_without_ver_is_unknown()
    {
        t.Incoming.Enqueue("PONG");
        var s = Make();
        Assert.True(s.Handshake());
        Assert.False(s.Firmware!.Known);
    }

    [Fact]
    public void Copy_request_sends_the_selection_transliterated()
    {
        var s = Make();
        (string, string, int)? sent = null;
        s.ClipSent += (text, src, lost) => sent = (text, src, lost);
        t.Incoming.Enqueue("COPY");
        s.Step();
        Assert.Contains("CLIP 5 select\nhello", t.Written);
        Assert.Equal(("hello", "select", 0), sent);
    }

    [Fact]
    public void Key_and_mouse_reports_are_injected()
    {
        var s = Make();
        t.Incoming.Enqueue("K 02 04");
        t.Incoming.Enqueue("M 00 3 -2");
        s.Step(); s.Step();
        Assert.Contains(new KeyStroke(0x1E, false, false), sink.Events);
        Assert.Contains(new MouseMove(3, -2), sink.Events);
    }

    [Fact]
    public void Caps_lock_is_reported_on_start_and_on_change_only()
    {
        var s = Make();
        s.Step();
        s.Step();
        kb.CapsLock = true;
        clock.Now = clock.Now.AddMilliseconds(300);
        s.Step();
        Assert.Equal(["LEDS 00", "LEDS 02"], t.Written.Where(w => w.StartsWith("LEDS")).ToList());
    }

    [Fact]
    public void Heartbeat_is_ping_or_dbg_every_two_seconds()
    {
        var s = Make();
        s.Step();
        clock.Now = clock.Now.AddSeconds(2.1);
        s.Step();
        s.DiagnosticsEnabled = true;
        clock.Now = clock.Now.AddSeconds(2.1);
        s.Step();
        Assert.Equal(["PING", "DBG"], t.Written.Where(w => w is "PING" or "DBG").ToList());
    }

    [Fact]
    public void Dbg_replies_become_diagnostics_not_log_lines()
    {
        var s = Make();
        IReadOnlyDictionary<string, string>? diag = null;
        var logs = new List<string>();
        s.Diagnostics += d => diag = d;
        s.Log += logs.Add;
        t.Incoming.Enqueue("LOG up=11s frames=16 | touch chip=181 fails=0");
        t.Incoming.Enqueue("LOG ev=1 at 10,20");
        s.Step(); s.Step();
        Assert.Equal("181", diag!["chip"]);
        Assert.Equal(["ev=1 at 10,20"], logs);
    }

    [Fact]
    public void Text_and_bootloader_requests_go_out_on_the_next_step()
    {
        var s = Make();
        s.SendText("ok");
        s.RequestBootloader();
        s.Step();
        Assert.Contains("CLIP 2 app\nok", t.Written);
        Assert.Contains("BOOT", t.Written);
    }

    [Fact]
    public void Remote_controls_map_to_scripting_commands()
    {
        var s = Make();
        s.Swipe(left: true);
        s.Swipe(left: false);
        s.PressButton(longPress: false);
        s.PressButton(longPress: true);
        s.Step();
        Assert.Equal(["SWIPE L", "SWIPE R", "BTN", "BTN LONG"], t.Written.Where(w => w.StartsWith("SWIPE") || w.StartsWith("BTN")).ToList());
    }

    [Fact]
    public void Unplugging_mid_paste_releases_every_held_key()
    {
        t.Incoming.Enqueue("PONG");
        t.Incoming.Enqueue("K 02 04");                     // Shift+a held when the board vanishes
        var s = Make();
        var run = Task.Run(() => s.Run(CancellationToken.None));
        // Unplug only once the key is really down (a slow CI machine can take a while to get here).
        Assert.True(SpinWait.SpinUntil(() => s.Injector.HeldKey != 0, 10000), "the key report was never applied");
        t.FailReads = true;
        Assert.ThrowsAny<IOException>(() => run.GetAwaiter().GetResult());
        Assert.Equal(0, s.Injector.HeldKey);
        Assert.Contains(new KeyStroke(0x1E, false, Up: true), sink.Events);
        Assert.Contains(new KeyStroke(0x2A, false, Up: true), sink.Events);
    }

    [Fact]
    public void Handshake_turns_on_the_mirror()
    {
        t.Incoming.Enqueue("PONG");
        Assert.True(Make().Handshake());
        Assert.Contains("WATCH 1", t.Written);
    }

    [Fact]
    public void State_lines_raise_StateReceived_and_mark_mirror_supported()
    {
        var s = Make();
        StateReport? got = null;
        s.StateReceived += r => got = r;
        t.Incoming.Enqueue("STATE jig=1 letter=M scale=1 phase=0 x=10 y=20 clip=3 paste=0");
        s.Step();
        Assert.Equal('M', got!.Letter);
        Assert.True(s.MirrorSupported);
        Assert.Equal(got, s.LastState);
    }

    [Fact]
    public void Old_firmware_is_detected_by_missing_state()
    {
        t.Incoming.Enqueue("PONG");
        var s = Make();
        s.Handshake();
        s.Step();
        clock.Now = clock.Now.AddSeconds(2);
        s.Step();
        Assert.False(s.MirrorSupported);
    }

    [Fact]
    public void Jiggler_and_clip_commands_are_sent()
    {
        var s = Make();
        s.SetJiggler(true); s.SetJiggler(false); s.SetScale(2); s.ClearClip();
        s.Step();
        Assert.Equal(["JIG ON", "JIG OFF", "JIG SCALE 2", "CLIP CLEAR"],
            t.Written.Where(w => w.StartsWith("JIG") || w.StartsWith("CLIP CLEAR")).ToList());
    }

    [Theory]
    [InlineData("VERSION rp2040-169 1.5.0 Sep 27 2026")]
    [InlineData(null)]                                  // pre-VER firmware
    public void Firmware_older_than_1_6_has_no_mirror_right_away(string? ver)
    {
        t.Incoming.Enqueue("PONG");
        if (ver is not null) t.Incoming.Enqueue(ver);
        var s = Make();
        Assert.True(s.Handshake());
        Assert.False(s.MirrorSupported);
    }

    [Fact]
    public void Firmware_1_6_waits_for_state()
    {
        t.Incoming.Enqueue("PONG");
        t.Incoming.Enqueue("VERSION rp2040-169 1.6.0 Sep 28 2026");
        var s = Make();
        Assert.True(s.Handshake());
        Assert.Null(s.MirrorSupported);
    }
}
