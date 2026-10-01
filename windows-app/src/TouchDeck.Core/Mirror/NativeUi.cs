using System.Runtime.InteropServices;
using System.Text;

namespace TouchDeck.Core.Mirror;

/// <summary>
/// The device renderers: the firmware's own page code compiled for the PC (hostui/, built by
/// this project into tdui_rp2040.dll, tdui_esp32c3.dll and tdui_rp2350.dll). Output is RGB565, row by row.
/// </summary>
public static partial class NativeUi
{
    public static (int Width, int Height) Size(UiModel model) => model == UiModel.Rp2040Rect ? (240, 280) : (240, 240);

    /// <summary>True when the board's renderer loads and agrees with <see cref="UiState"/>'s layout.</summary>
    public static bool Available(UiModel model, out string? error)
    {
        try
        {
            var (w, h, size) = model switch
            {
                UiModel.Esp32Round => (Esp.tdui_width(), Esp.tdui_height(), Esp.tdui_state_size()),
                UiModel.Rp2350Round => (Rp2350.tdui_width(), Rp2350.tdui_height(), Rp2350.tdui_state_size()),
                _ => (Rp.tdui_width(), Rp.tdui_height(), Rp.tdui_state_size()),
            };
            error = size != UiState.Size ? $"renderer state size {size} != {UiState.Size}"
                  : (w, h) != Size(model) ? $"renderer size {w}x{h}" : null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            error = e.Message;
        }
        return error is null;
    }

    public static ushort[] Render(UiModel model, in UiState state)
    {
        var (w, h) = Size(model);
        var pixels = new ushort[w * h];
        Render(model, state, pixels);
        return pixels;
    }

    public static void Render(UiModel model, in UiState state, ushort[] pixels)
    {
        var (w, h) = Size(model);
        if (pixels.Length < w * h) throw new ArgumentException("buffer too small", nameof(pixels));
        switch (model)
        {
            case UiModel.Esp32Round: Esp.tdui_render(in state, pixels); break;
            case UiModel.Rp2350Round: Rp2350.tdui_render(in state, pixels); break;
            default: Rp.tdui_render(in state, pixels); break;
        }
    }

    /// <summary>STATE letter= name to the renderer's letter index, or -1.</summary>
    public static int LetterIndex(UiModel model, char name) => model switch
    {
        UiModel.Esp32Round => Esp.tdui_letter_index((byte)name),
        UiModel.Rp2350Round => Rp2350.tdui_letter_index((byte)name),
        _ => Rp.tdui_letter_index((byte)name),
    };

    /// <summary>The STATE line the firmware would send for this state (ui_sync.c; tests).</summary>
    public static string StateLine(UiModel model, in UiState state)
    {
        var buf = new byte[512];
        int n = model switch
        {
            UiModel.Esp32Round => Esp.tdui_state_line(in state, buf, buf.Length),
            UiModel.Rp2350Round => Rp2350.tdui_state_line(in state, buf, buf.Length),
            _ => Rp.tdui_state_line(in state, buf, buf.Length),
        };
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

    private static partial class Rp2350
    {
        private const string Lib = "tdui_rp2350";
        [LibraryImport(Lib)] internal static partial int tdui_width();
        [LibraryImport(Lib)] internal static partial int tdui_height();
        [LibraryImport(Lib)] internal static partial int tdui_state_size();
        [LibraryImport(Lib)] internal static partial int tdui_letter_index(byte name);
        [LibraryImport(Lib)] internal static partial void tdui_render(in UiState s, [Out] ushort[] output);
        [LibraryImport(Lib)] internal static partial int tdui_state_line(in UiState s, [Out] byte[] output, int n);
    }
}
