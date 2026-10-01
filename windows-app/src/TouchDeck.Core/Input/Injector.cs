namespace TouchDeck.Core.Input;

/// <summary>
/// Turns the board's HID-style reports (PC output mode) into input events.
/// Keys go in as scancodes, so they behave exactly like a physical keyboard.
/// A port of tools/inject.py: same tables, same ordering rules.
/// </summary>
public sealed class Injector(IInputSink sink)
{
    // HID keyboard usage -> (PS/2 set-1 scancode, extended)
    private static readonly Dictionary<int, (int Scan, bool Ext)> Scancodes = BuildScancodes();

    // HID modifier bit -> (scancode, extended): LCtrl LShift LAlt LGUI RCtrl RShift RAlt RGUI
    private static readonly (int Bit, int Scan, bool Ext)[] Modifiers =
    [
        (0x01, 0x1D, false), (0x02, 0x2A, false), (0x04, 0x38, false), (0x08, 0x5B, true),
        (0x10, 0x1D, true), (0x20, 0x36, false), (0x40, 0x38, true), (0x80, 0x5C, true),
    ];

    private static readonly (int Bit, MouseAction Down, MouseAction Up)[] Buttons =
    [
        (0x01, MouseAction.LeftDown, MouseAction.LeftUp),
        (0x02, MouseAction.RightDown, MouseAction.RightUp),
        (0x04, MouseAction.MiddleDown, MouseAction.MiddleUp),
    ];

    public int HeldKey { get; private set; }
    public int HeldMods { get; private set; }
    public int HeldButtons { get; private set; }

    private static Dictionary<int, (int, bool)> BuildScancodes()
    {
        int[] letters = [0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26, 0x32,
                         0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C];
        var map = new Dictionary<int, (int, bool)>();
        for (int i = 0; i < letters.Length; i++) map[0x04 + i] = (letters[i], false);   // a..z
        for (int i = 0; i < 10; i++) map[0x1E + i] = (0x02 + i, false);                  // 1..9, 0
        map[0x28] = (0x1C, false);   // Enter
        map[0x29] = (0x01, false);   // Esc
        map[0x2A] = (0x0E, false);   // Backspace
        map[0x2B] = (0x0F, false);   // Tab
        map[0x2C] = (0x39, false);   // Space
        map[0x6A] = (0x66, false);   // F15: the jiggler's harmless alternative to ESC
        (int Usage, int Scan)[] punct =
        [
            (0x2D, 0x0C), (0x2E, 0x0D), (0x2F, 0x1A), (0x30, 0x1B), (0x31, 0x2B),   // - = [ ] \
            (0x33, 0x27), (0x34, 0x28), (0x35, 0x29), (0x36, 0x33), (0x37, 0x34), (0x38, 0x35), // ; ' ` , . /
        ];
        foreach (var (usage, scan) in punct) map[usage] = (scan, false);
        return map;
    }

    /// <summary>A keyboard report: modifier byte + one usage (0 = release all).</summary>
    public void Key(int mods, int usage)
    {
        if (usage != 0 && !Scancodes.ContainsKey(usage)) return;   // unknown key: ignore the whole report
        var events = new List<InputEvent>();
        if (HeldKey != 0 && HeldKey != usage)                     // 1. old key up
        {
            var (scan, ext) = Scancodes[HeldKey];
            events.Add(new KeyStroke(scan, ext, Up: true));
            HeldKey = 0;
        }
        foreach (var (bit, scan, ext) in Modifiers)               // 2. dropped modifiers up
            if ((HeldMods & bit) != 0 && (mods & bit) == 0) events.Add(new KeyStroke(scan, ext, Up: true));
        foreach (var (bit, scan, ext) in Modifiers)               // 3. new modifiers down
            if ((mods & bit) != 0 && (HeldMods & bit) == 0) events.Add(new KeyStroke(scan, ext, Up: false));
        HeldMods = mods;
        if (usage != 0 && usage != HeldKey)                       // 4. new key down
        {
            var (scan, ext) = Scancodes[usage];
            events.Add(new KeyStroke(scan, ext, Up: false));
            HeldKey = usage;
        }
        Send(events);
    }

    /// <summary>A mouse report: button byte + relative motion.</summary>
    public void Mouse(int buttons, int dx, int dy)
    {
        var events = new List<InputEvent>();
        if (dx != 0 || dy != 0) events.Add(new MouseMove(dx, dy));
        foreach (var (bit, down, up) in Buttons)
        {
            if ((buttons & bit) != 0 && (HeldButtons & bit) == 0) events.Add(new MouseButton(down));
            else if ((HeldButtons & bit) != 0 && (buttons & bit) == 0) events.Add(new MouseButton(up));
        }
        HeldButtons = buttons;
        Send(events);
    }

    /// <summary>Let go of every key and button (link lost, app closing, mode switch).</summary>
    public void ReleaseAll()
    {
        Key(0, 0);
        Mouse(0, 0, 0);
    }

    private void Send(List<InputEvent> events)
    {
        if (events.Count > 0) sink.Send(events);
    }
}
