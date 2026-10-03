using TouchDeck.Core.Devices;
using TouchDeck.Core.Firmware;

namespace TouchDeck.Core.App;

/// <summary>One activity-log line. Board lines carry their port, so the log can show one board's lines.</summary>
public sealed record LogEntry(DateTime Time, string? Port, string Text)
{
    public override string ToString() => Port is null ? $"{Time:HH:mm:ss}  {Text}" : $"{Time:HH:mm:ss}  {Port}: {Text}";

    /// <summary>With "only the selected board", app lines (no port) stay; other boards' lines go.</summary>
    public bool Shows(string? selectedPort, bool onlySelected) => !onlySelected || Port is null || Port == selectedPort;
}

/// <summary>Which board's tab is selected when boards come and go.</summary>
public static class BoardSelection
{
    /// <param name="keys">The boards now attached, in tab order.</param>
    /// <param name="current">The selected board before the change.</param>
    /// <param name="added">The board that just appeared, if that was the change.</param>
    /// <param name="preferred">The board used last time (its port), selected when it comes back.</param>
    public static string? Next(IReadOnlyList<string> keys, string? current, string? added, string? preferred)
    {
        if (keys.Count == 0) return null;
        // A board plugged in doesn't take the tab you're looking at, unless it's the only one or the last used.
        if (added is not null && keys.Contains(added) && (keys.Count == 1 || added == preferred)) return added;
        if (current is not null && keys.Contains(current)) return current;
        return keys[0];
    }
}

/// <summary>Short names for the tab strip and the log: the model, then where it is.</summary>
public static class BoardLabels
{
    public static string Model(BoardKind kind, string? board) => board switch
    {
        "rp2040-169" => "RP2040 1.69",
        "rp2350-128" => "RP2350 1.28",
        "esp32c3-128" => "ESP32-C3 1.28",
        _ => kind == BoardKind.Esp32C3 ? "ESP32-C3" : "RP2040",
    };

    public static string Model(NewBoard b) =>
        (b.Chip == Uf2Chip.Rp2350 ? "RP2350" : "RP2040") + (b.State == NewBoardState.Bootloader ? " bootloader" : " (not Touch Deck)");

    /// <summary>The tab text: "RP2040 1.69 · COM6"; a board with no port (a bootloader drive) has none.</summary>
    public static string Tab(string model, string? port) => port is null ? model : $"{model} · {ShortPort(port)}";

    /// <summary>macOS ports are long (/dev/cu.usbmodem1101): the last part is enough.</summary>
    public static string ShortPort(string port) => port.StartsWith("/dev/cu.", StringComparison.Ordinal) ? port[8..] : port;
}
