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
