using System.IO;
using TouchDeck.Core.Devices;
using TouchDeck.Core.Input;

namespace TouchDeck.Core.Session;

public enum LinkStatus { Searching, PortBusy, NotResponding, Connected }

/// <summary>What the app shows about the board link.</summary>
public sealed record LinkState(LinkStatus Status, DeviceCandidate? Device = null, FirmwareInfo? Firmware = null)
{
    public static LinkState Searching { get; } = new(LinkStatus.Searching);
}

/// <summary>
/// Finds a Touch Deck and keeps one <see cref="DeviceSession"/> running on it. When the board
/// goes away the session ends (releasing any held input) and scanning resumes.
/// </summary>
public sealed class DeviceManager(
    Func<IReadOnlyList<DeviceCandidate>> scan,
    Func<DeviceCandidate, ISerialTransport> openTransport,
    Func<ISerialTransport, DeviceSession> makeSession) : IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? stop;
    private Task? loop;
    private Task? running;
    private CancellationTokenSource? sessionStop;

    public LinkState State { get; private set; } = LinkState.Searching;
    public DeviceSession? Session { get; private set; }

    /// <summary>A port to prefer when several boards are plugged in (e.g. the last one used).</summary>
    public string? PreferredPort { get; set; }

    public event Action<LinkState>? StateChanged;
    /// <summary>A new session is up: hook its events here.</summary>
    public event Action<DeviceSession>? SessionStarted;

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

    /// <summary>One scan-and-connect attempt, if no session is running.</summary>
    public void Tick()
    {
        lock (gate)
        {
            if (running is { IsCompleted: false }) return;
        }
        var found = scan();
        var device = found.FirstOrDefault(d => d.Port == PreferredPort) ?? found.FirstOrDefault();
        if (device is null)
        {
            Publish(LinkState.Searching);
            return;
        }
        if (RequiredBoard is { } required)
        {
            // Only the board being flashed may connect: try each candidate, keep the one whose VER matches.
            foreach (var d in found.OrderBy(d => d.Port == PreferredPort ? 0 : 1))
                if (TryConnect(d, required)) return;
            Publish(LinkState.Searching);
            return;
        }
        TryConnect(device, null);
    }

    /// <summary>
    /// While set (during a firmware install), only a board whose VER reports this model is connected:
    /// both RP boards are CAFE:4011, and the manager must not settle on the other one.
    /// </summary>
    public string? RequiredBoard { get; set; }

    private bool TryConnect(DeviceCandidate device, string? requiredBoard)
    {
        var transport = openTransport(device);
        try
        {
            transport.Open();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            transport.Dispose();                 // another program holds it
            if (requiredBoard is null) Publish(new LinkState(LinkStatus.PortBusy, device));
            return false;
        }

        var session = makeSession(transport);
        if (!session.Handshake())
        {
            transport.Dispose();
            if (requiredBoard is null) Publish(new LinkState(LinkStatus.NotResponding, device));
            return false;
        }
        if (requiredBoard is not null && session.Firmware?.Board != requiredBoard)
        {
            transport.Dispose();                 // another board: leave it for after the install
            return false;
        }

        var cts = new CancellationTokenSource();
        lock (gate)
        {
            session.Kind = device.Kind;
            Session = session;
            sessionStop = cts;
            SessionStarted?.Invoke(session);
            Publish(new LinkState(LinkStatus.Connected, device, session.Firmware));
            running = Task.Run(() =>
            {
                try { session.Run(cts.Token); }
                catch (Exception) { /* unplugged: the finally in Run released held input */ }
                finally
                {
                    transport.Dispose();
                    lock (gate) Session = null;
                    if (!cts.IsCancellationRequested) Publish(LinkState.Searching);
                }
            });
        }
        return true;
    }

    /// <summary>Ends the current session (e.g. before flashing firmware) and waits for it.</summary>
    public void Disconnect()
    {
        Task? r;
        lock (gate)
        {
            sessionStop?.Cancel();
            r = running;
        }
        r?.Wait(TimeSpan.FromSeconds(2));
        Publish(LinkState.Searching);
    }

    private void Publish(LinkState state)
    {
        if (state == State) return;
        State = state;
        StateChanged?.Invoke(state);
    }

    public void Dispose()
    {
        stop?.Cancel();
        try { loop?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
        Disconnect();
    }
}
