using TouchDeck.Core.Devices;
using TouchDeck.Core.Input;
using TouchDeck.Core.Session;

namespace TouchDeck.Tests;

public class ManagerTests
{
    private static readonly DeviceCandidate Rp = new("COM6", BoardKind.Rp2040, new UsbId(0xCAFE, 0x4011));
    private static readonly DeviceCandidate Esp = new("COM7", BoardKind.Esp32C3, new UsbId(0x303A, 0x1001));

    private readonly List<DeviceCandidate> present = [];
    private readonly Dictionary<string, FakeTransport> ports = [];
    private readonly List<LinkState> states = [];

    private DeviceManager Make(Func<DeviceCandidate, ISerialTransport>? open = null)
    {
        var m = new DeviceManager(
            () => present.ToList(),
            open ?? (d => ports[d.Port]),
            t => new DeviceSession(t, new Injector(new RecordingSink()), new FakeKeyboard(), new FakeSelection("x", "select"), new FakeClock()));
        m.StateChanged += states.Add;
        return m;
    }

    private FakeTransport Board(DeviceCandidate d, bool answers = true)
    {
        var t = new FakeTransport();
        if (answers)
        {
            t.Incoming.Enqueue("PONG");
            t.Incoming.Enqueue("VERSION rp2040-169 1.5.0 Sep 27 2026");
        }
        ports[d.Port] = t;
        present.Add(d);
        return t;
    }

    [Fact]
    public void Nothing_plugged_in_stays_searching()
    {
        var m = Make();
        m.Tick();
        Assert.Equal(LinkStatus.Searching, m.State.Status);
        Assert.Empty(states);                         // no change, no event
    }

    [Fact]
    public void Connects_and_reports_firmware()
    {
        Board(Rp);
        var m = Make();
        m.Tick();
        Assert.Equal(LinkStatus.Connected, m.State.Status);
        Assert.Equal("1.5.0", m.State.Firmware!.Version);
        Assert.Equal(Rp, m.State.Device);
        Thread.Sleep(100);                            // let Run() start on its thread
        m.Dispose();
        Assert.Single(ports["COM6"].Written, w => w == "HELLO");   // handshake happens once
    }

    [Fact]
    public void Preferred_port_wins_over_the_default_order()
    {
        Board(Rp);
        Board(Esp);
        var m = Make();
        m.PreferredPort = "COM7";
        m.Tick();
        Assert.Equal(Esp, m.State.Device);
        m.Dispose();
    }

    [Fact]
    public void Busy_port_is_reported_not_thrown()
    {
        present.Add(Rp);
        var m = Make(_ => throw new UnauthorizedAccessException());
        Assert.Throws<UnauthorizedAccessException>(m.Tick);   // factory itself failing is a bug, surfaced

        var busy = new ThrowingOpenTransport();
        m = Make(_ => busy);
        m.Tick();
        Assert.Equal(LinkStatus.PortBusy, m.State.Status);
        Assert.True(busy.Disposed);
    }

    [Fact]
    public void Silent_port_is_not_a_touch_deck()
    {
        Board(Rp, answers: false);
        var m = Make();
        m.Tick();
        Assert.Equal(LinkStatus.NotResponding, m.State.Status);
    }

    [Fact]
    public void Unplug_returns_to_searching()
    {
        var t = Board(Rp);
        var m = Make();
        m.Tick();
        present.Clear();
        t.FailReads = true;
        Assert.True(SpinWait.SpinUntil(() => m.State.Status == LinkStatus.Searching, 2000));
        Assert.Null(m.Session);
        Assert.Equal([LinkStatus.Connected, LinkStatus.Searching], states.Select(s => s.Status).ToList());
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
        Skip.If(m.State.Status == LinkStatus.PortBusy, "port busy (helper running?)");
        Assert.Equal(LinkStatus.Connected, m.State.Status);
        Assert.True(m.State.Firmware!.Known, "firmware predates VER");
        Assert.Matches(@"^\d+\.\d+\.\d+$", m.State.Firmware.Version);
    }

    [SkippableFact]
    public void The_real_board_mirrors_its_state()
    {
        Skip.If(DeviceScanner.Scan().Count == 0, "no Touch Deck connected");
        using var m = DeviceManager.CreateDefault(new FakeSelection("", "select"), new RecordingSink());
        m.Tick();
        Skip.If(m.State.Status == LinkStatus.PortBusy, "port busy (helper running?)");
        Skip.If(m.State.Firmware?.SemVer is { } v && v < new Version(1, 6, 0), "firmware older than 1.6.0");
        Assert.True(SpinWait.SpinUntil(() => m.Session?.LastState is not null, 3000), "no STATE after WATCH 1");
        Assert.True(m.Session!.MirrorSupported);
        Assert.Contains(m.Session.LastState!.Letter, "OWMNZXCVHJLBGD");
    }
}
