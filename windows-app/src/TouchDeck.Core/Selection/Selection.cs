using System.Runtime.InteropServices;
using System.Windows.Automation;
using TouchDeck.Core.Session;

namespace TouchDeck.Core.Selection;

/// <summary>One way of reading text: null means "can't tell", "" means "nothing there".</summary>
public interface ITextSource
{
    string? Read();
}

/// <summary>COPY text: the focused control's selection, else the clipboard (as the Python helper).</summary>
public sealed class SelectionProvider(ITextSource selection, ITextSource clipboard) : ISelectionProvider
{
    public static SelectionProvider CreateDefault() =>
        new(new TimeBoxedSource(new UiaSelection(FocusTracker.Shared), TimeSpan.FromMilliseconds(1500)), new Win32Clipboard());

    public (string Text, string Source) Grab()
    {
        var sel = selection.Read();
        if (!string.IsNullOrEmpty(sel)) return (sel, "select");
        return (clipboard.Read() ?? "", "clipbd");
    }
}

/// <summary>
/// Some apps answer UI Automation very slowly or not at all; past the limit we give up
/// (the read keeps running in the background) so COPY still answers from the clipboard.
/// </summary>
public sealed class TimeBoxedSource(ITextSource inner, TimeSpan limit) : ITextSource
{
    public string? Read()
    {
        var read = Task.Run(inner.Read);
        return read.Wait(limit) ? read.Result : null;
    }
}

/// <summary>
/// Selected text of the focused control via UI Automation's TextPattern. When Touch Deck itself is
/// in front (the user clicked the mirror), it reads the control last focused in another app instead.
/// </summary>
public sealed class UiaSelection(FocusTracker? tracker = null, Func<bool>? ownWindowInFront = null) : ITextSource
{
    public string? Read()
    {
        try
        {
            AutomationElement? element;
            if (!(ownWindowInFront ?? FocusTracker.OwnWindowInFront)()) element = AutomationElement.FocusedElement;
            else if (tracker?.LastFocus is { } hwnd && FocusTracker.IsWindow(hwnd)) element = AutomationElement.FromHandle(hwnd);
            else return null;                                    // never Touch Deck's own text
            var walker = TreeWalker.ControlViewWalker;
            for (int level = 0; level < 4 && element is not null; level++)   // focus is sometimes on a child
            {
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out var p))
                    return string.Concat(((TextPattern)p).GetSelection().Select(r => r.GetText(-1)));
                element = walker.GetParent(element);
            }
        }
        catch (Exception e) when (e is ElementNotAvailableException or InvalidOperationException or COMException or ArgumentException)
        {
            // UIA errors are app-specific: treat as unsupported.
        }
        return null;
    }
}

/// <summary>Unicode text on the Windows clipboard, read with Win32 (works from any thread).</summary>
public sealed class Win32Clipboard : ITextSource
{
    private const uint CfUnicodeText = 13;

    public string? Read()
    {
        for (int attempt = 0; !OpenClipboard(IntPtr.Zero); attempt++)
        {
            if (attempt == 4) return null;           // another app holds it
            Thread.Sleep(50);
        }
        try
        {
            var h = GetClipboardData(CfUnicodeText);
            if (h == IntPtr.Zero) return "";
            var p = GlobalLock(h);
            if (p == IntPtr.Zero) return "";
            try { return Marshal.PtrToStringUni(p) ?? ""; }
            finally { GlobalUnlock(h); }
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Puts text on the clipboard (used by the app's "copy clip" action and tests).</summary>
    public static bool Write(string text)
    {
        for (int attempt = 0; !OpenClipboard(IntPtr.Zero); attempt++)
        {
            if (attempt == 4) return false;
            Thread.Sleep(50);
        }
        try
        {
            EmptyClipboard();
            int bytes = (text.Length + 1) * 2;
            var h = GlobalAlloc(0x0002 /* GMEM_MOVEABLE */, (UIntPtr)bytes);
            if (h == IntPtr.Zero) return false;
            var p = GlobalLock(h);
            Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
            Marshal.WriteInt16(p, text.Length * 2, 0);
            GlobalUnlock(h);
            if (SetClipboardData(CfUnicodeText, h) != IntPtr.Zero) return true;
            GlobalFree(h);                           // ownership stays with us on failure
            return false;
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] private static extern IntPtr SetClipboardData(uint format, IntPtr mem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr mem);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr mem);
}
