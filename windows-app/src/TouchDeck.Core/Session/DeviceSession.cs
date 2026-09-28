using System.IO;
using System.Collections.Concurrent;
using TouchDeck.Core.Devices;
using TouchDeck.Core.Input;
using TouchDeck.Core.Protocol;

namespace TouchDeck.Core.Session;

public interface IClock
{
    DateTime Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime Now => DateTime.Now;
}

/// <summary>Where COPY text comes from: the focused selection, else the clipboard.</summary>
public interface ISelectionProvider
{
    /// <returns>The text and a short source tag ("select" or "clipbd") shown on the board.</returns>
    (string Text, string Source) Grab();
}

/// <summary>Board identity from VER. Firmware older than 1.5.0 has no VER and is <see cref="Unknown"/>.</summary>
public sealed record FirmwareInfo(string Board, string Version, string Build)
{
    public static FirmwareInfo Unknown { get; } = new("?", "unknown", "");
    public bool Known => this != Unknown;

    public Version? SemVer => System.Version.TryParse(Version, out var v) ? v : null;
}

/// <summary>
/// One connected board: the port of tools/clip_helper.py's run loop. Single-threaded by
/// design: <see cref="Run"/> owns the transport, other threads only queue requests.
/// </summary>
public sealed class DeviceSession(
    ISerialTransport transport,
    Injector injector,
    IKeyboardState keyboard,
    ISelectionProvider selection,
    IClock clock)
{
    private static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CapsPollEvery = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan TimeSyncEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan ReadSlice = TimeSpan.FromMilliseconds(20);

    private readonly ConcurrentQueue<Action> requests = new();
    private readonly Queue<string> held = new();   // lines read while waiting for a reply
    private DateTime lastHeartbeat, lastCapsPoll, lastTimeSync;
    private bool? caps;                      // null forces the first LEDS report
    private bool started;

    public Injector Injector => injector;
    public FirmwareInfo? Firmware { get; private set; }

    /// <summary>Poll DBG instead of PING, feeding <see cref="Diagnostics"/>.</summary>
    public bool DiagnosticsEnabled { get; set; }

    public event Action<string>? Log;
    /// <summary>Text sent to the board: (ascii text, source, characters that became '?').</summary>
    public event Action<string, string, int>? ClipSent;
    public event Action<IReadOnlyDictionary<string, string>>? Diagnostics;

    /// <summary>HELLO must be answered by PONG, else the port isn't a Touch Deck. Then VER and TIME.</summary>
    public bool Handshake(TimeSpan? timeout = null)
    {
        var wait = timeout ?? TimeSpan.FromSeconds(1.5);
        transport.WriteLine("");             // flush any half line the board holds
        transport.WriteLine("HELLO");
        if (!WaitFor<Pong>(wait, out _)) return false;
        transport.WriteLine("VER");
        Firmware = WaitFor<VersionReply>(TimeSpan.FromMilliseconds(500), out var v)
            ? new FirmwareInfo(v!.Board, v.Version, v.Build)
            : FirmwareInfo.Unknown;
        SendTime();
        return true;
    }

    /// <summary>Runs until cancelled or the transport fails; never leaves input held on the PC.</summary>
    public void Run(CancellationToken ct)
    {
        try
        {
            if (!started && !Handshake()) throw new IOException("no PONG: not a Touch Deck");
            while (!ct.IsCancellationRequested) Step(ReadSlice);
        }
        finally
        {
            injector.ReleaseAll();
        }
    }

    /// <summary>One pass: queued requests, at most one board line, then timers.</summary>
    public void Step() => Step(TimeSpan.Zero);

    private void Step(TimeSpan wait)
    {
        var now = clock.Now;
        if (!started)
        {
            started = true;
            lastHeartbeat = lastTimeSync = now;
            lastCapsPoll = now - CapsPollEvery;
        }
        while (requests.TryDequeue(out var request)) request();

        var line = held.Count > 0 ? held.Dequeue() : transport.ReadLine(wait);
        if (line is not null) Handle(BoardLine.Parse(line.Trim()));

        now = clock.Now;
        if (now - lastCapsPoll >= CapsPollEvery)
        {
            lastCapsPoll = now;
            bool c = keyboard.CapsLock;
            if (c != caps)
            {
                caps = c;
                transport.WriteLine(c ? "LEDS 02" : "LEDS 00");
            }
        }
        if (now - lastHeartbeat >= HeartbeatEvery)
        {
            lastHeartbeat = now;
            transport.WriteLine(DiagnosticsEnabled ? "DBG" : "PING");
        }
        if (now - lastTimeSync >= TimeSyncEvery)
        {
            lastTimeSync = now;
            SendTime();
        }
    }

    /// <summary>Push text into the board's clip (queued; sent on the session thread).</summary>
    public void SendText(string text, string source = "app") => requests.Enqueue(() => SendClip(text, source));

    /// <summary>Reboot the board into its bootloader (RP2040: UF2 drive).</summary>
    public void RequestBootloader() => requests.Enqueue(() => transport.WriteLine("BOOT"));

    /// <summary>Change the board's page, as a finger swipe would.</summary>
    public void Swipe(bool left) => requests.Enqueue(() => transport.WriteLine(left ? "SWIPE L" : "SWIPE R"));

    /// <summary>Press the board's BOOT button (stopwatch on the watch, scale on the jiggler).</summary>
    public void PressButton(bool longPress) => requests.Enqueue(() => transport.WriteLine(longPress ? "BTN LONG" : "BTN"));

    private void Handle(BoardMessage message)
    {
        switch (message)
        {
            case CopyRequest:
                var (text, source) = selection.Grab();
                SendClip(text, source);
                break;
            case KeyReport k:
                injector.Key(k.Mods, k.Usage);
                break;
            case MouseReport m:
                injector.Mouse(m.Buttons, m.Dx, m.Dy);
                break;
            case LogLine { Text: var t } when t.StartsWith("up=", StringComparison.Ordinal):
                Diagnostics?.Invoke(DbgFields.Parse(t));   // the DBG reply always starts with uptime
                break;
            case LogLine l:
                Log?.Invoke(l.Text);
                break;
        }
    }

    private void SendClip(string text, string source)
    {
        var (ascii, lost) = AsciiText.Transliterate(text);
        if (ascii.Length > ClipMessage.MaxBytes) ascii = ascii[..ClipMessage.MaxBytes];
        transport.Write(ClipMessage.Encode(ascii, source));
        ClipSent?.Invoke(ascii, source, lost);
    }

    private void SendTime() => transport.WriteLine(clock.Now.ToString("'TIME 'HH':'mm':'ss"));

    private bool WaitFor<T>(TimeSpan timeout, out T? reply) where T : BoardMessage
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var line = transport.ReadLine(TimeSpan.FromMilliseconds(50));
            if (line is null) continue;
            if (BoardLine.Parse(line.Trim()) is T t)
            {
                reply = t;
                return true;
            }
            held.Enqueue(line);                   // e.g. a K report racing the handshake
        }
        reply = null;
        return false;
    }
}
