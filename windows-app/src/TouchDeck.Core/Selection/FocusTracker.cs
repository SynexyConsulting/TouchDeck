using System.Runtime.InteropServices;

namespace TouchDeck.Core.Selection;

/// <summary>
/// Remembers the window that last had keyboard focus in another app. Windows clears a background
/// app's focus window, so when the user clicks Touch Deck (the mirror's COPY), the control they
/// were working in can only be found by having watched it get focus.
/// An out-of-context WinEvent hook only passes window handles: it never asks the app anything,
/// unlike a UI Automation focus handler, which keeps Chromium's accessibility tree switched on.
/// </summary>
public sealed class FocusTracker
{
    private static readonly Lazy<FocusTracker> shared = new(() => new FocusTracker());
    public static FocusTracker Shared => shared.Value;

    private long lastFocus;
    private readonly WinEventProc proc;                  // kept alive: the hook calls it from native code

    /// <summary>The window last focused outside Touch Deck, or null before any focus change.</summary>
    public IntPtr? LastFocus => Interlocked.Read(ref lastFocus) is var h and not 0 ? (IntPtr)h : null;

    private FocusTracker()
    {
        proc = OnEvent;
        // Out-of-context hooks are delivered through the installing thread's message queue.
        var thread = new Thread(() =>
        {
            if (SetWinEventHook(EventObjectFocus, EventObjectFocus, IntPtr.Zero, proc, 0, 0,
                                WinEventOutOfContext | WinEventSkipOwnProcess) == IntPtr.Zero) return;
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) DispatchMessage(ref msg);
        }) { IsBackground = true, Name = "FocusTracker" };
        thread.Start();
    }

    private void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd != IntPtr.Zero) Interlocked.Exchange(ref lastFocus, (long)hwnd);
    }

    /// <summary>True when the foreground window belongs to this process.</summary>
    public static bool OwnWindowInFront()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    public static bool IsWindow(IntPtr hwnd) => IsWindowNative(hwnd);

    private const uint EventObjectFocus = 0x8005, WinEventOutOfContext = 0x0000, WinEventSkipOwnProcess = 0x0002;

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Msg msg);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "IsWindow")] private static extern bool IsWindowNative(IntPtr hwnd);
}
