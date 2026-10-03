using System.IO;
using TouchDeck.Core.Devices;
using TouchDeck.Core.Input;

namespace TouchDeck.Core.Session;

public enum LinkStatus { Searching, PortBusy, NotResponding, Connected }

/// <summary>What the app shows about one board's link.</summary>
public sealed record LinkState(LinkStatus Status, DeviceCandidate? Device = null, FirmwareInfo? Firmware = null)
{
    public static LinkState Searching { get; } = new(LinkStatus.Searching);
}

/// <summary>One port the manager knows: its link state and, while connected, its session.</summary>
public sealed record BoardLink(string Port, LinkState State, DeviceSession? Session);

/// <summary>
/// Keeps one <see cref="DeviceSession"/> running on every Touch Deck that is plugged in: one slot
/// per port. A board that goes away ends its own session (releasing any held input); the others
/// carry on. A port that is busy or doesn't answer is a slot too, so the app can say why.
/// </summary>
public sealed class DeviceManager(
    Func<IReadOnlyList<DeviceCandidate>> scan,
    Func<DeviceCandidate, ISerialTransport> openTransport,
    Func<ISerialTransport, DeviceSession> makeSession) : IDisposable
{
    private sealed class Slot(DeviceCandidate device)
    {
        public DeviceCandidate Device = device;
        public LinkState State = LinkState.Searching;
        public DeviceSession? Session;
        public CancellationTokenSource? Stop;
        public Task? Running;
        public bool Live => Running is { IsCompleted: false };
    }

    private readonly object gate = new();
    private readonly Dictionary<string, Slot> slots = [];
    private CancellationTokenSource? stop;
    private Task? loop;

    /// <summary>A port's link changed (raised on a background thread). A new port is announced this way too.</summary>
    public event Action<string, LinkState>? SlotChanged;
    /// <summary>A port went away and has no session left.</summary>
    public event Action<string>? SlotRemoved;
    /// <summary>A new session is up on a port: hook its events here (before it starts reading).</summary>
    public event Action<string, DeviceSession>? SessionStarted;

    /// <summary>Every known port, in the order they were first seen.</summary>
    public IReadOnlyList<BoardLink> Links
    {
        get { lock (gate) return slots.Select(kv => new BoardLink(kv.Key, kv.Value.State, kv.Value.Session)).ToList(); }
    }

    public IReadOnlyList<DeviceSession> Sessions
    {
        get { lock (gate) return slots.Values.Select(s => s.Session).OfType<DeviceSession>().ToList(); }
    }

    public DeviceSession? SessionFor(string port)
    {
        lock (gate) return slots.TryGetValue(port, out var s) ? s.Session : null;
    }

    /// <summary>Production wiring: WMI scan, real serial port, SendInput.</summary>
    public static DeviceManager CreateDefault(ISelectionProvider selection, IInputSink? sink = null) => new(
        DeviceScanner.Scan,
        d => new SerialPortTransport(d),
        t => new DeviceSession(t, new Injector(sink ?? new SendInputSink()), new WindowsKeyboardState(), selection, new SystemClock()));

    public void Start(TimeSpan? pollEvery = null)
    {
        var every = pollEvery ?? TimeSpan.FromSeconds(2);
        stop = new CancellationTokenSource();
        var ct = stop.Token;
        loop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { Tick(); }
                catch (Exception) { /* WMI hiccup: try again next round */ }
                try { await Task.Delay(every, ct); } catch (OperationCanceledException) { }
            }
        }, ct);
    }

    /// <summary>One scan: connect every port without a session, drop the ports that are gone.</summary>
    public void Tick()
    {
        var found = scan();
        foreach (var device in found)
        {
            Slot slot;
            lock (gate)
            {
                if (!slots.TryGetValue(device.Port, out slot!)) slots[device.Port] = slot = new Slot(device);
                if (slot.Live) continue;
                slot.Device = device;
            }
            TryConnect(slot);
        }

        var present = found.Select(d => d.Port).ToHashSet();
        List<string> gone;
        lock (gate)
        {
            gone = slots.Where(kv => !present.Contains(kv.Key) && !kv.Value.Live).Select(kv => kv.Key).ToList();
            foreach (var port in gone) slots.Remove(port);
        }
        foreach (var port in gone) SlotRemoved?.Invoke(port);
    }

    private void TryConnect(Slot slot)
    {
        var device = slot.Device;
        var transport = openTransport(device);
        try
        {
            transport.Open();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            transport.Dispose();                 // another program holds it
            Publish(slot, new LinkState(LinkStatus.PortBusy, device));
            return;
        }

        var session = makeSession(transport);
        if (!session.Handshake())
        {
            transport.Dispose();
            Publish(slot, new LinkState(LinkStatus.NotResponding, device));
            return;
        }

        var cts = new CancellationTokenSource();
        session.Kind = device.Kind;
        lock (gate)
        {
            slot.Session = session;
            slot.Stop = cts;
        }
        SessionStarted?.Invoke(device.Port, session);
        Publish(slot, new LinkState(LinkStatus.Connected, device, session.Firmware));
        lock (gate)
        {
            slot.Running = Task.Run(() =>
            {
                try { session.Run(cts.Token); }
                catch (Exception) { /* unplugged: the finally in Run released held input */ }
                finally
                {
                    transport.Dispose();
                    lock (gate) slot.Session = null;
                    if (!cts.IsCancellationRequested) Publish(slot, new LinkState(LinkStatus.Searching, device));
                }
            });
        }
    }

    private void Publish(Slot slot, LinkState state)
    {
        lock (gate)
        {
            if (state == slot.State) return;
            slot.State = state;
        }
        SlotChanged?.Invoke(slot.Device.Port, state);
    }

    /// <summary>Ends every session and waits for them (each releases its held input).</summary>
    public void DisconnectAll()
    {
        List<Task> running;
        lock (gate)
        {
            foreach (var s in slots.Values) s.Stop?.Cancel();
            running = slots.Values.Select(s => s.Running).OfType<Task>().ToList();
        }
        try { Task.WaitAll([.. running], TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
    }

    public void Dispose()
    {
        stop?.Cancel();
        try { loop?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        DisconnectAll();
    }
}
