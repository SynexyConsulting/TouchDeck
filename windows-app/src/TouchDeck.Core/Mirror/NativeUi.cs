using System.Runtime.InteropServices;
using System.Text;
using TouchDeck.Core.Devices;

namespace TouchDeck.Core.Mirror;

/// <summary>
/// The device renderers: the firmware's own page code compiled for the PC (hostui/, built by
/// this project into tdui_rp2040.dll and tdui_esp32c3.dll). Output is RGB565, row by row.
/// </summary>
public static partial class NativeUi
{
    public static (int Width, int Height) Size(BoardKind kind) => kind == BoardKind.Esp32C3 ? (240, 240) : (240, 280);

    /// <summary>True when the board's renderer loads and agrees with <see cref="UiState"/>'s layout.</summary>
    public static bool Available(BoardKind kind, out string? error)
    {
        try
        {
            var (w, h) = kind == BoardKind.Esp32C3 ? (Esp.tdui_width(), Esp.tdui_height()) : (Rp.tdui_width(), Rp.tdui_height());
            int size = kind == BoardKind.Esp32C3 ? Esp.tdui_state_size() : Rp.tdui_state_size();
            error = size != UiState.Size ? $"renderer state size {size} != {UiState.Size}"
                  : (w, h) != Size(kind) ? $"renderer size {w}x{h}" : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            error = e.Message;
        }
        return error is null;
    }

    public static ushort[] Render(BoardKind kind, in UiState state)
    {
        var (w, h) = Size(kind);
        var pixels = new ushort[w * h];
        Render(kind, state, pixels);
        return pixels;
    }

    public static void Render(BoardKind kind, in UiState state, ushort[] pixels)
    {
        var (w, h) = Size(kind);
        if (pixels.Length < w * h) throw new ArgumentException("buffer too small", nameof(pixels));
        if (kind == BoardKind.Esp32C3) Esp.tdui_render(in state, pixels);
        else Rp.tdui_render(in state, pixels);
    }

    /// <summary>STATE letter= name to the renderer's letter index, or -1.</summary>
    public static int LetterIndex(BoardKind kind, char name) =>
        kind == BoardKind.Esp32C3 ? Esp.tdui_letter_index((byte)name) : Rp.tdui_letter_index((byte)name);

    /// <summary>The STATE line the firmware would send for this state (ui_sync.c; tests).</summary>
    public static string StateLine(BoardKind kind, in UiState state)
    {
        var buf = new byte[512];
        int n = kind == BoardKind.Esp32C3 ? Esp.tdui_state_line(in state, buf, buf.Length) : Rp.tdui_state_line(in state, buf, buf.Length);
        return Encoding.ASCII.GetString(buf, 0, Math.Min(n, buf.Length - 1));
    }

    private static partial class Rp
    {
        private const string Lib = "tdui_rp2040";
        [LibraryImport(Lib)] internal static partial int tdui_width();
        [LibraryImport(Lib)] internal static partial int tdui_height();
        [LibraryImport(Lib)] internal static partial int tdui_state_size();
        [LibraryImport(Lib)] internal static partial int tdui_letter_index(byte name);
        [LibraryImport(Lib)] internal static partial void tdui_render(in UiState s, [Out] ushort[] output);
        [LibraryImport(Lib)] internal static partial int tdui_state_line(in UiState s, [Out] byte[] output, int n);
    }

    private static partial class Esp
    {
        private const string Lib = "tdui_esp32c3";
        [LibraryImport(Lib)] internal static partial int tdui_width();
        [LibraryImport(Lib)] internal static partial int tdui_height();
        [LibraryImport(Lib)] internal static partial int tdui_state_size();
        [LibraryImport(Lib)] internal static partial int tdui_letter_index(byte name);
        [LibraryImport(Lib)] internal static partial void tdui_render(in UiState s, [Out] ushort[] output);
        [LibraryImport(Lib)] internal static partial int tdui_state_line(in UiState s, [Out] byte[] output, int n);
    }
}
