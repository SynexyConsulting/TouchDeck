using TouchDeck.Core.Devices;

namespace TouchDeck.Core.Mirror;

/// <summary>
/// Which page code and screen a board has. Not the same as <see cref="BoardKind"/> (the USB
/// link): both RP boards are CAFE:4011, and only VER tells the 1.69 from the round RP2350.
/// </summary>
public enum UiModel
{
    /// <summary>RP2040-Touch-LCD-1.69: 240x280, Watch / Clipboard / Jiggler (tdui_rp2040).</summary>
    Rp2040Rect,
    /// <summary>ESP32-2424S012C: 240x240 round, Clipboard / Jiggler / Settings (tdui_esp32c3).</summary>
    Esp32Round,
    /// <summary>RP2350-Touch-LCD-1.28: 240x240 round, Clipboard / Jiggler, USB (tdui_rp2350).</summary>
    Rp2350Round,
}

public static class UiModels
{
    /// <summary>The model for a link and the board name VER reported (null or "?" before VER existed).</summary>
    public static UiModel For(BoardKind kind, string? board) =>
        kind == BoardKind.Esp32C3 ? UiModel.Esp32Round
        : board == "rp2350-128" ? UiModel.Rp2350Round
        : UiModel.Rp2040Rect;

    public static bool IsRound(this UiModel m) => m != UiModel.Rp2040Rect;

    /// <summary>Index of the Clipboard page (the RP2040 1.69 starts on its watch).</summary>
    public static int ClipPage(this UiModel m) => m == UiModel.Rp2040Rect ? 1 : 0;

    public static int PageCount(this UiModel m) => m == UiModel.Rp2350Round ? 2 : 3;
}
