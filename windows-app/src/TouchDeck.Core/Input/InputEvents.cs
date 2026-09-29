namespace TouchDeck.Core.Input;

/// <summary>One low-level input action, as handed to SendInput.</summary>
public abstract record InputEvent;

/// <summary>A PS/2 set-1 scancode press or release (Extended = E0-prefixed key).</summary>
public sealed record KeyStroke(int Scan, bool Extended, bool Up) : InputEvent;

/// <summary>Relative pointer motion (subject to Windows pointer acceleration, like a real mouse).</summary>
public sealed record MouseMove(int Dx, int Dy) : InputEvent;

public enum MouseAction { LeftDown, LeftUp, RightDown, RightUp, MiddleDown, MiddleUp }

public sealed record MouseButton(MouseAction Action) : InputEvent;

/// <summary>Where injected input goes: the real desktop, or a recorder (tests, dry-run).</summary>
public interface IInputSink
{
    void Send(IReadOnlyList<InputEvent> events);
}

/// <summary>Keeps what would have been injected. Used by tests and by dry-run mode.</summary>
public sealed class RecordingSink : IInputSink
{
    public List<InputEvent> Events { get; } = [];

    /// <summary>Raised for each batch (dry-run shows these in the log).</summary>
    public event Action<IReadOnlyList<InputEvent>>? Sent;

    public void Send(IReadOnlyList<InputEvent> events)
    {
        Events.AddRange(events);
        Sent?.Invoke(events);
    }
}

/// <summary>The host's Caps Lock state (reported to the board as LEDS so typed case stays right).</summary>
public interface IKeyboardState
{
    bool CapsLock { get; }
}

/// <summary>Real injection, or (dry run) a description of each event instead. Switchable live.</summary>
/// <remarks>
/// Tracks what is down on the real desktop, so turning dry run on mid-paste releases it there;
/// otherwise the matching key-up (and Run's final ReleaseAll) would only be logged and the key would stick.
/// </remarks>
public sealed class SwitchableSink(IInputSink real) : IInputSink
{
    private readonly HashSet<(int Scan, bool Extended)> keysDown = [];
    private readonly HashSet<MouseAction> buttonsDown = [];
    private readonly object gate = new();
    private bool dryRun;

    public bool DryRun
    {
        get => dryRun;
        set
        {
            lock (gate)
            {
                if (value && !dryRun) ReleaseReal();
                dryRun = value;
            }
        }
    }

    /// <summary>Dry-run descriptions, e.g. "key down" (never which key).</summary>
    public event Action<string>? DryRunEvent;

    public void Send(IReadOnlyList<InputEvent> events)
    {
        lock (gate)
        {
            if (!dryRun)
            {
                foreach (var e in events) Track(e);
                real.Send(events);
                return;
            }
        }
        foreach (var e in events) DryRunEvent?.Invoke(Describe(e));
    }

    private void Track(InputEvent e)
    {
        switch (e)
        {
            case KeyStroke k when k.Up: keysDown.Remove((k.Scan, k.Extended)); break;
            case KeyStroke k: keysDown.Add((k.Scan, k.Extended)); break;
            case MouseButton { Action: MouseAction.LeftDown or MouseAction.RightDown or MouseAction.MiddleDown } b: buttonsDown.Add(b.Action); break;
            case MouseButton b: buttonsDown.Remove(b.Action - 1); break;   // each Up follows its Down
        }
    }

    private void ReleaseReal()
    {
        var ups = new List<InputEvent>();
        ups.AddRange(keysDown.Select(k => new KeyStroke(k.Scan, k.Extended, Up: true)));
        ups.AddRange(buttonsDown.Select(b => new MouseButton(b + 1)));
        keysDown.Clear();
        buttonsDown.Clear();
        if (ups.Count > 0) real.Send(ups);
    }

    public static string Describe(InputEvent e) => e switch
    {
        KeyStroke k => $"key {(k.Up ? "up" : "down")}",     // never which key: the log must not spell out typed text
        MouseMove m => $"mouse move {m.Dx:+0;-0;0} {m.Dy:+0;-0;0}",
        MouseButton b => $"mouse {b.Action}",
        _ => e.ToString() ?? "",
    };
}
