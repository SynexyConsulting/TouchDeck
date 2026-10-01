using System.Runtime.InteropServices;
using System.Text;

namespace TouchDeck.Core.Mirror;

/// <summary>
/// The firmware's ui_state_t (src/ui_state.h), field for field: everything a device page
/// needs to draw itself. Tests pin its size against the native renderer's.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct UiState
{
    public const int ClipView = 1024;

    public int Screen, Sub, TimeS, Helper, LinkOk, Muted, TimerS, BtMode, BtAvail, BtState, BtReady, BtSecsLeft;
    public uint BtPasskey;
    public int ClipLen, ClipState, PastePos, JigOn, JigDemo, JigPaused, JigPhase, JigLetter, JigScale;
    public float JigX, JigY;
    public int JigNextS, JigUpS;
    public uint JigMenus;
    public fixed byte ClipSrc[12];
    public fixed byte Msg[40];
    public fixed byte BtHost[32];
    public fixed byte DownReason[32];
    public fixed byte Clip[ClipView];
    public int JigMenuOn, JigKey, JigOpenS, JigPauseS;   // Jiggler settings page (firmware 1.8.0)

    public static int Size => sizeof(UiState);

    /// <summary>Writes an ASCII string into a fixed field, NUL-terminated and cut to fit.</summary>
    internal static void Put(byte* field, int size, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        int n = Math.Min(bytes.Length, size - 1);
        for (int i = 0; i < size; i++) field[i] = i < n ? bytes[i] : (byte)0;
    }

    internal static string Get(byte* field, int size)
    {
        int n = 0;
        while (n < size && field[n] != 0) n++;
        return Encoding.ASCII.GetString(field, n);
    }
}
