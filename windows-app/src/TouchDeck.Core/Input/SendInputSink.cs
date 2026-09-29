using System.Runtime.InteropServices;

namespace TouchDeck.Core.Input;

/// <summary>Performs input on the real desktop with Win32 SendInput.</summary>
/// <remarks>Windows blocks injection into apps running elevated when we are not (UIPI).</remarks>
public sealed class SendInputSink : IInputSink
{
    private const uint InputMouse = 0, InputKeyboard = 1;
    private const uint KeyeventfExtendedKey = 0x0001, KeyeventfKeyUp = 0x0002, KeyeventfScanCode = 0x0008;
    private const uint MouseeventfMove = 0x0001;

    internal static int InputStructSize => Marshal.SizeOf<INPUT>();

    public void Send(IReadOnlyList<InputEvent> events)
    {
        var inputs = new INPUT[events.Count];
        for (int i = 0; i < events.Count; i++) inputs[i] = ToInput(events[i]);
        SendInput((uint)inputs.Length, inputs, InputStructSize);
    }

    private static INPUT ToInput(InputEvent e) => e switch
    {
        KeyStroke k => new INPUT
        {
            type = InputKeyboard,
            u = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wScan = (ushort)k.Scan,
                    dwFlags = KeyeventfScanCode | (k.Extended ? KeyeventfExtendedKey : 0) | (k.Up ? KeyeventfKeyUp : 0),
                },
            },
        },
        MouseMove m => new INPUT
        {
            type = InputMouse,
            u = new InputUnion { mi = new MOUSEINPUT { dx = m.Dx, dy = m.Dy, dwFlags = MouseeventfMove } },
        },
        MouseButton b => new INPUT
        {
            type = InputMouse,
            u = new InputUnion { mi = new MOUSEINPUT { dwFlags = ButtonFlag(b.Action) } },
        },
        _ => throw new ArgumentOutOfRangeException(nameof(e)),
    };

    private static uint ButtonFlag(MouseAction a) => a switch
    {
        MouseAction.LeftDown => 0x02, MouseAction.LeftUp => 0x04,
        MouseAction.RightDown => 0x08, MouseAction.RightUp => 0x10,
        MouseAction.MiddleDown => 0x20, MouseAction.MiddleUp => 0x40,
        _ => 0,
    };

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL, wParamH;
    }
}

/// <summary>Reads Caps Lock from the Windows keyboard state.</summary>
public sealed class WindowsKeyboardState : IKeyboardState
{
    public bool CapsLock => (GetKeyState(0x14) & 1) != 0;

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);
}
