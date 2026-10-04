using TouchDeck.Core.Devices;
using TouchDeck.Core.Input;
using TouchDeck.Core.Session;

namespace TouchDeck.Tests;

public class ManagerTests
{
    private static readonly DeviceCandidate Rp = new("COM6", BoardKind.Rp2040, new UsbId(0xCAFE, 0x4011));
    private static readonly DeviceCandidate Esp = new("COM7", BoardKind.Esp32C3, new UsbId(0x303A, 0x1001));
    private static readonly DeviceCandidate Round = new("COM11", BoardKind.Rp2040, new UsbId(0xCAFE, 0x4011));

    private readonly List<DeviceCandidate> present = [];
    private readonly Dictionary<string, FakeTransport> ports = [];
    private readonly List<(string Port, LinkStatus Status)> changes = [];
    private readonly List<string> removed = [];

    private DeviceManager Make(Func<DeviceCandidate, ISerialTransport>? open = null)
    {
        var m = new DeviceManager(
            () => present.ToList(),
            open ?? (d => ports[d.Port]),
            t => new DeviceSession(t, new Injector(new RecordingSink()), new FakeKeyboard(), new FakeSelection("x", "select"), new FakeClock()));
        m.SlotChanged += (port, s) => { lock (changes) changes.Add((port, s.Status)); };
        m.SlotRemoved += port => { lock (removed) removed.Add(port); };
        return m;
    }

    private FakeTransport Board(DeviceCandidate d, bool answers = true, string board = "rp2040-169")
    {
        var t = new FakeTransport();
        if (answers)
        {
            t.Incoming.Enqueue("PONG");
            t.Incoming.Enqueue($"VERSION {board} 1.5.0 Sep 27 2026");
        }
        ports[d.Port] = t;
        present.Add(d);
        return t;
    }

    private static LinkState StateOf(DeviceManager m, string port) => m.Links.Single(l => l.Port == port).State;

    [Fact]
    public void Nothing_plugged_in_has_no_slots()
    {
        var m = Make();
        m.Tick();
        Assert.Empty(m.Links);
        Assert.Empty(changes);                        // no change, no event
    }

    [Fact]
    public void Connects_and_reports_firmware()
    {
        Board(Rp);
        var m = Make();
        m.Tick();
        var s = StateOf(m, "COM6");
        Assert.Equal(LinkStatus.Connected, s.Status);
        Assert.Equal("1.5.0", s.Firmware!.Version);
        Assert.Equal(Rp, s.Device);
        Assert.NotNull(m.SessionFor("COM6"));
        Thread.Sleep(100);                            // let Run() start on its thread
        m.Tick();                                     // a live session is left alone
        m.Dispose();
        Assert.Single(ports["COM6"].Written, w => w == "HELLO");   // handshake happens once
    }

    [Fact]
    public void Every_board_gets_its_own_session()
    {
        Board(Rp);
        Board(Esp);
        Board(Round, board: "rp2350-128");
        var m = Make();
        m.Tick();
        Assert.Equal(["COM6", "COM7", "COM11"], m.Links.Select(l => l.Port).ToList());
        Assert.All(m.Links, l => Assert.Equal(LinkStatus.Connected, l.State.Status));
        Assert.Equal(3, m.Sessions.Count);
        Assert.Equal("rp2350-128", m.SessionFor("COM11")!.Firmware!.Board);
        m.Dispose();
    }

    [Fact]
    public void Unplugging_one_board_leaves_the_others_connected()
    {
        var rp = Board(Rp);
        Board(Esp);
        var m = Make();
        m.Tick();
        present.Remove(Rp);
        rp.FailReads = true;
        Assert.True(SpinWait.SpinUntil(() => m.SessionFor("COM6") is null, 2000));
        Assert.True(SpinWait.SpinUntil(() => m.Links.Single(l => l.Port == "COM6").State.Status == LinkStatus.Searching, 2000));
        m.Tick();
        Assert.Equal(["COM6"], removed);
        Assert.Equal(["COM7"], m.Links.Select(l => l.Port).ToList());
        Assert.Equal(LinkStatus.Connected, StateOf(m, "COM7").Status);
        m.Dispose();
    }

    [Fact]
    public void A_board_that_comes_back_on_its_port_reconnects()
    {
        var first = Board(Rp);
        var m = Make();
        m.Tick();
        first.FailReads = true;                       // e.g. it rebooted after a firmware install
        Assert.True(SpinWait.SpinUntil(() => m.Links.Single().State.Status == LinkStatus.Searching, 2000));
        present.Clear();
        var second = Board(Rp);                       // the same port, a fresh transport
        m.Tick();
        Assert.Empty(removed);                        // still there: reconnected, not removed
        Assert.Equal(LinkStatus.Connected, StateOf(m, "COM6").Status);
        Assert.Contains("HELLO", second.Written);
        m.Dispose();
    }

    [Fact]
    public void A_busy_port_is_its_own_slot_and_is_reported_not_thrown()
    {
        Board(Esp);
        present.Insert(0, Rp);
        var busy = new ThrowingOpenTransport();
        var m = Make(d => d.Port == "COM6" ? busy : ports[d.Port]);
        m.Tick();
        Assert.Equal(LinkStatus.PortBusy, StateOf(m, "COM6").Status);
        Assert.Equal(LinkStatus.Connected, StateOf(m, "COM7").Status);
        Assert.True(busy.Disposed);
        m.Dispose();
    }

    [Fact]
    public void A_board_unplugged_mid_handshake_doesnt_stop_the_others()
    {
        var dying = Board(Rp, answers: false);
        dying.FailReads = true;                      // the read throws, as when the cable comes out
        Board(Esp);
        var m = Make();
        m.Tick();
        Assert.Equal(LinkStatus.NotResponding, StateOf(m, "COM6").Status);
        Assert.True(dying.Disposed);                 // its handle is let go, not left to the finalizer
        Assert.Equal(LinkStatus.Connected, StateOf(m, "COM7").Status);
        m.Dispose();
    }

    [Fact]
    public void A_failing_transport_factory_is_a_bug_and_surfaces()
    {
        present.Add(Rp);
        var m = Make(_ => throw new UnauthorizedAccessException());
        Assert.Throws<UnauthorizedAccessException>(m.Tick);
    }

    [Fact]
    public void A_silent_port_is_not_a_touch_deck()
    {
        Board(Rp, answers: false);
        var m = Make();
        m.Tick();
        Assert.Equal(LinkStatus.NotResponding, StateOf(m, "COM6").Status);
        Assert.Null(m.SessionFor("COM6"));
    }

    [Fact]
    public void Unplug_reports_searching_then_removes_the_slot()
    {
        var t = Board(Rp);
        var m = Make();
        m.Tick();
        present.Clear();
        t.FailReads = true;
        Assert.True(SpinWait.SpinUntil(() => { lock (changes) return changes.Count == 2; }, 2000));
        m.Tick();
        Assert.Empty(m.Links);
        Assert.Equal([("COM6", LinkStatus.Connected), ("COM6", LinkStatus.Searching)], changes);
        Assert.Equal(["COM6"], removed);
    }

    private sealed class ThrowingOpenTransport : ISerialTransport
    {
        public bool Disposed { get; private set; }
        public void Open() => throw new UnauthorizedAccessException("Access to the port 'COM6' is denied.");
        public void WriteLine(string line) { }
        public void Write(byte[] data) { }
        public string? ReadLine(TimeSpan timeout) => null;
        public void Dispose() => Disposed = true;
    }
}

/// <summary>Real hardware: skipped unless a Touch Deck is plugged in and free.</summary>
[Collection("Board")]   // one test at a time on the real serial port
public class ManagerSmokeTests
{
    [SkippableFact]
    public void Connects_to_the_real_board_and_reads_its_firmware_version()
    {
        Skip.If(DeviceScanner.Scan().Count == 0, "no Touch Deck connected");
        using var m = DeviceManager.CreateDefault(new FakeSelection("", "select"), new RecordingSink());
        m.Tick();
        var link = HardwareLinks.First(m);
        Skip.If(link.State.Status == LinkStatus.PortBusy, "port busy (helper running?)");
        Assert.Equal(LinkStatus.Connected, link.State.Status);
        Assert.True(link.State.Firmware!.Known, "firmware predates VER");
        Assert.Matches(@"^\d+\.\d+\.\d+$", link.State.Firmware.Version);
    }

    [SkippableFact]
    public void The_real_board_mirrors_its_state()
    {
        Skip.If(DeviceScanner.Scan().Count == 0, "no Touch Deck connected");
        using var m = DeviceManager.CreateDefault(new FakeSelection("", "select"), new RecordingSink());
        m.Tick();
        var link = HardwareLinks.First(m);
        Skip.If(link.State.Status == LinkStatus.PortBusy, "port busy (helper running?)");
        Skip.If(link.State.Firmware?.SemVer is { } v && v < new Version(1, 6, 0), "firmware older than 1.6.0");
        Assert.True(SpinWait.SpinUntil(() => link.Session?.LastState is not null, 3000), "no STATE after WATCH 1");
        Assert.True(link.Session!.MirrorSupported);
        Assert.Contains(link.Session.LastState!.Letter, "OWMNZXCVHJLBGD");
    }
}

/// <summary>The real board a hardware test uses: the first connected one, else the first port seen.</summary>
internal static class HardwareLinks
{
    public static BoardLink First(DeviceManager m) =>
        m.Links.FirstOrDefault(l => l.State.Status == LinkStatus.Connected) ?? m.Links.First();
}
