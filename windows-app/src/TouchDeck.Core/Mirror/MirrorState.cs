using TouchDeck.Core.Devices;
using TouchDeck.Core.Protocol;

namespace TouchDeck.Core.Mirror;

/// <summary>
/// The board's UI state as the device mirror knows it, built from the board's STATE, TEXT and
/// CLIPTEXT lines (firmware 1.7.0+; the protocol is at the top of src/usb_io.c).
/// </summary>
public sealed class MirrorState(UiModel model)
{
    // STATE key -> field. tools/tests/tdui_host.py has the same table.
    private static readonly (string Key, Setter Set)[] Numbers =
    [
        ("page", (ref UiState s, long v) => s.Screen = (int)v),
        ("sub", (ref UiState s, long v) => s.Sub = (int)v),
        ("t", (ref UiState s, long v) => s.TimeS = (int)v),
        ("pc", (ref UiState s, long v) => s.Helper = (int)v),
        ("link", (ref UiState s, long v) => s.LinkOk = (int)v),
        ("mute", (ref UiState s, long v) => s.Muted = (int)v),
        ("timer", (ref UiState s, long v) => s.TimerS = (int)v),
        ("mode", (ref UiState s, long v) => s.BtMode = (int)v),
        ("bta", (ref UiState s, long v) => s.BtAvail = (int)v),
        ("bts", (ref UiState s, long v) => s.BtState = (int)v),
        ("btr", (ref UiState s, long v) => s.BtReady = (int)v),
        ("left", (ref UiState s, long v) => s.BtSecsLeft = (int)v),
        ("pk", (ref UiState s, long v) => s.BtPasskey = (uint)v),
        ("clip", (ref UiState s, long v) => s.ClipLen = (int)v),
        ("cst", (ref UiState s, long v) => s.ClipState = (int)v),
        ("ppos", (ref UiState s, long v) => s.PastePos = (int)v),
        ("jig", (ref UiState s, long v) => s.JigOn = (int)v),
        ("demo", (ref UiState s, long v) => s.JigDemo = (int)v),
        ("paused", (ref UiState s, long v) => s.JigPaused = (int)v),
        ("phase", (ref UiState s, long v) => s.JigPhase = (int)v),
        ("scale", (ref UiState s, long v) => s.JigScale = (int)v),
        ("x", (ref UiState s, long v) => s.JigX = v),
        ("y", (ref UiState s, long v) => s.JigY = v),
        ("next", (ref UiState s, long v) => s.JigNextS = (int)v),
        ("up", (ref UiState s, long v) => s.JigUpS = (int)v),
        ("menus", (ref UiState s, long v) => s.JigMenus = (uint)v),
        ("jmenu", (ref UiState s, long v) => s.JigMenuOn = (int)v),
        ("jkey", (ref UiState s, long v) => s.JigKey = (int)v),
        ("jopen", (ref UiState s, long v) => s.JigOpenS = (int)v),
        ("jpause", (ref UiState s, long v) => s.JigPauseS = (int)v),
    ];

    private delegate void Setter(ref UiState s, long value);

    private UiState state = Initial();

    public UiModel Model { get; } = model;

    /// <summary>A STATE with the full mirror fields has arrived (firmware 1.7.0+).</summary>
    public bool Complete { get; private set; }

    public UiState State => state;

    private static unsafe UiState Initial()
    {
        var s = new UiState();
        UiState.Put(s.ClipSrc, 12, "-");
        return s;
    }

    /// <summary>Applies one board line; true when it changed what the mirror shows.</summary>
    public unsafe bool Apply(BoardMessage message)
    {
        switch (message)
        {
            case StateReport st:
                foreach (var (key, set) in Numbers)
                    if (st.Fields.Contains(key)) set(ref state, st.Fields.Get(key));
                int li = NativeUi.LetterIndex(Model, st.Letter);
                state.JigLetter = li < 0 ? 0 : li;
                Complete |= st.IsFullMirror;
                return true;
            case TextField t:
                fixed (UiState* s = &state)
                {
                    switch (t.Key)
                    {
                        case "msg": UiState.Put(s->Msg, 40, t.Value); break;
                        case "src": UiState.Put(s->ClipSrc, 12, t.Value); break;
                        case "host": UiState.Put(s->BtHost, 32, t.Value); break;
                        case "down": UiState.Put(s->DownReason, 32, t.Value); break;
                        default: return false;
                    }
                }
                return true;
            case ClipText c:
                fixed (UiState* s = &state)
                {
                    int n = Math.Min(c.Bytes.Length, UiState.ClipView);
                    for (int i = 0; i < UiState.ClipView; i++) s->Clip[i] = i < n ? c.Bytes[i] : (byte)0;
                }
                return true;
            default:
                return false;
        }
    }

    public unsafe string Message
    {
        get { fixed (UiState* s = &state) return UiState.Get(s->Msg, 40); }
    }
}
