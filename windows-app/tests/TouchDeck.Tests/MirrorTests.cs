using System.Text;
using TouchDeck.Core.Devices;
using TouchDeck.Core.Input;
using TouchDeck.Core.Mirror;
using TouchDeck.Core.Protocol;
using TouchDeck.Core.Session;

namespace TouchDeck.Tests;

public class MirrorProtocolTests
{
    [Fact]
    public void Full_mirror_state_keeps_every_numeric_field()
    {
        var m = (StateReport)BoardLine.Parse(
            "STATE jig=1 letter=W scale=2 phase=0 x=412 y=733 clip=58 paste=0 page=1 sub=0 t=36000 pc=1 link=1 pk=482913");
        Assert.True(m.IsFullMirror);
        Assert.Equal(36000, m.Fields.Get("t"));
        Assert.Equal(482913, m.Fields.Get("pk"));
        Assert.Equal(412, m.Fields.Get("x"));
    }

    [Fact]
    public void State_from_1_6_is_not_a_full_mirror()
    {
        var m = (StateReport)BoardLine.Parse("STATE jig=0 letter=O scale=0 phase=0 x=0 y=500 clip=0 paste=0");
        Assert.False(m.IsFullMirror);
    }

    [Theory]
    [InlineData("TEXT msg Copied 5 chars", "msg", "Copied 5 chars")]
    [InlineData("TEXT msg ", "msg", "")]
    [InlineData("TEXT msg", "msg", "")]
    [InlineData("TEXT host  two  spaces ", "host", " two  spaces ")]
    public void Parses_text_fields(string line, string key, string value) =>
        Assert.Equal(new TextField(key, value), BoardLine.Parse(line));

    [Fact]
    public void Parses_and_unescapes_clip_text()
    {
        var m = (ClipText)BoardLine.Parse(@"CLIPTEXT a\\b\nc\r\td\x01\xFF\x20");
        Assert.Equal(new byte[] { (byte)'a', (byte)'\\', (byte)'b', 10, (byte)'c', 13, 9, (byte)'d', 1, 0xFF, 0x20 }, m.Bytes);
        Assert.Empty(((ClipText)BoardLine.Parse("CLIPTEXT")).Bytes);
    }
}

public class MirrorRendererTests
{
    public static TheoryData<BoardKind> Boards => [BoardKind.Rp2040, BoardKind.Esp32C3];

    [Theory]
    [MemberData(nameof(Boards))]
    public void Renderer_loads_and_matches_the_struct_layout(BoardKind kind)
    {
        Assert.True(NativeUi.Available(kind, out var error), error);
        Assert.Equal(1248, UiState.Size);
    }

    [Theory]
    [MemberData(nameof(Boards))]
    public void Each_page_renders_differently(BoardKind kind)
    {
        var frames = Enumerable.Range(0, 3).Select(p => NativeUi.Render(kind, new UiState { Screen = p, LinkOk = 1 })).ToList();
        var (w, h) = NativeUi.Size(kind);
        Assert.All(frames, f => Assert.Equal(w * h, f.Length));
        Assert.Equal(3, frames.Select(f => string.Join(",", f)).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Boards))]
    public void State_line_round_trips_through_the_parser(BoardKind kind)
    {
        var rnd = new Random(5);
        for (int i = 0; i < 40; i++)
        {
            var s = new UiState
            {
                Screen = rnd.Next(3), Sub = rnd.Next(2), TimeS = rnd.Next(86400), Helper = rnd.Next(2), LinkOk = rnd.Next(2),
                Muted = rnd.Next(2), TimerS = rnd.Next(99999), BtMode = rnd.Next(2), BtAvail = rnd.Next(2), BtState = rnd.Next(5),
                BtReady = rnd.Next(2), BtSecsLeft = rnd.Next(121), BtPasskey = (uint)rnd.Next(1000000), ClipLen = rnd.Next(8193),
                ClipState = rnd.Next(3), PastePos = rnd.Next(8192), JigOn = rnd.Next(2), JigDemo = rnd.Next(2),
                JigPaused = rnd.Next(2), JigPhase = rnd.Next(6), JigLetter = rnd.Next(14), JigScale = rnd.Next(3),
                JigX = rnd.Next(1001), JigY = rnd.Next(1001), JigNextS = rnd.Next(200), JigUpS = rnd.Next(99999),
                JigMenus = (uint)rnd.Next(999),
            };
            var line = NativeUi.StateLine(kind, s);
            var mirror = new MirrorState(kind);
            Assert.True(mirror.Apply(BoardLine.Parse(line)));
            Assert.True(mirror.Complete);
            var back = mirror.State;
            Assert.Equal(Numbers(s), Numbers(back));
        }
    }

    private static int[] Numbers(UiState s) =>
    [
        s.Screen, s.Sub, s.TimeS, s.Helper, s.LinkOk, s.Muted, s.TimerS, s.BtMode, s.BtAvail, s.BtState, s.BtReady,
        s.BtSecsLeft, (int)s.BtPasskey, s.ClipLen, s.ClipState, s.PastePos, s.JigOn, s.JigDemo, s.JigPaused, s.JigPhase,
        s.JigLetter, s.JigScale, (int)s.JigX, (int)s.JigY, s.JigNextS, s.JigUpS, (int)s.JigMenus,
    ];

    [Fact]
    public void Text_and_clip_lines_change_the_render()
    {
        var m = new MirrorState(BoardKind.Rp2040);
        m.Apply(BoardLine.Parse("STATE jig=0 letter=O scale=0 phase=0 x=0 y=0 clip=5 paste=0 page=1 link=1"));
        var before = NativeUi.Render(m.Kind, m.State);
        Assert.True(m.Apply(BoardLine.Parse("CLIPTEXT hello")));
        var withText = NativeUi.Render(m.Kind, m.State);
        Assert.NotEqual(before, withText);
        Assert.True(m.Apply(BoardLine.Parse("TEXT msg Copied 5 chars")));
        Assert.Equal("Copied 5 chars", m.Message);
        Assert.NotEqual(withText, NativeUi.Render(m.Kind, m.State));
        Assert.False(m.Apply(BoardLine.Parse("TEXT nope x")));
        Assert.False(m.Apply(new Pong()));
    }

    [Fact]
    public void Letter_names_map_to_the_renderers_order()
    {
        Assert.Equal(0, NativeUi.LetterIndex(BoardKind.Rp2040, 'O'));
        Assert.True(NativeUi.LetterIndex(BoardKind.Esp32C3, 'W') > 0);
        Assert.Equal(-1, NativeUi.LetterIndex(BoardKind.Rp2040, '?'));
    }
}

public class MirrorInputTests
{
    [Theory]
    [InlineData(100, 100, 100, 100, "tap 100 100")]
    [InlineData(100.7, 50.2, 108, 55, "tap 100 50")]
    [InlineData(150, 140, 60, 150, "swipe L")]
    [InlineData(60, 140, 150, 130, "swipe R")]
    [InlineData(100, 100, 100, 180, "none")]       // vertical drag
    [InlineData(100, 100, 125, 100, "none")]       // too short for a swipe, too long for a tap
    [InlineData(100, 100, 140, 110, "swipe R")]
    [InlineData(-3, 20, -3, 20, "none")]           // off the panel
    [InlineData(240, 20, 240, 20, "none")]
    public void Classifies_gestures(double x0, double y0, double x1, double y1, string want)
    {
        var g = MirrorInput.Classify(x0, y0, x1, y1, 240, 280);
        var got = g switch
        {
            MirrorTap t => $"tap {t.X} {t.Y}",
            MirrorSwipe s => s.Left ? "swipe L" : "swipe R",
            _ => "none",
        };
        Assert.Equal(want, got);
    }
}

public class MirrorSessionTests
{
    private readonly FakeTransport t = new();

    private DeviceSession Make() => new(t, new Injector(new RecordingSink()), new FakeKeyboard(), new FakeSelection("", "select"), new FakeClock());

    [Fact]
    public void Tap_is_sent_on_the_session_thread()
    {
        var s = Make();
        s.Tap(12, 250);
        Assert.DoesNotContain("TAP 12 250", t.Written);
        s.Step();
        Assert.Contains("TAP 12 250", t.Written);
    }

    [Fact]
    public void Text_and_clip_lines_raise_events_and_keep_trailing_spaces()
    {
        var s = Make();
        var texts = new List<TextField>();
        var clips = new List<ClipText>();
        s.TextReceived += texts.Add;
        s.ClipTextReceived += clips.Add;
        t.Incoming.Enqueue("TEXT msg Copied 5 chars ");
        t.Incoming.Enqueue(@"CLIPTEXT hi\n");
        s.Step();
        s.Step();
        Assert.Equal([new TextField("msg", "Copied 5 chars ")], texts);
        Assert.Equal("hi\n", Encoding.ASCII.GetString(clips.Single().Bytes));
    }
}
