using System.Management;
using System.Text.RegularExpressions;

namespace TouchDeck.Core.Devices;

public enum BoardKind
{
    /// <summary>RP2040 Touch Deck firmware (USB CAFE:4011): native USB keyboard/mouse.</summary>
    Rp2040,
    /// <summary>ESP32-C3 Touch Deck (USB 303A:1001): Bluetooth or PC-mode output.</summary>
    Esp32C3,
}

public static class BoardKinds
{
    public static BoardKind? FromUsb(int vid, int pid) => (vid, pid) switch
    {
        (0xCAFE, 0x4011) => BoardKind.Rp2040,
        (0x303A, 0x1001) => BoardKind.Esp32C3,
        _ => null,
    };

    /// <summary>
    /// RP2040 (TinyUSB) only transmits while DTR is asserted. On the ESP32-C3's
    /// USB-Serial-JTAG, DTR/RTS are the reset and boot-mode lines: both stay low
    /// or opening the port reboots the chip.
    /// </summary>
    public static bool DtrHigh(BoardKind kind) => kind == BoardKind.Rp2040;

    public static string DisplayName(BoardKind kind) => kind switch
    {
        BoardKind.Rp2040 => "RP2040 Touch Deck",
        BoardKind.Esp32C3 => "ESP32-C3 Touch Deck",
        _ => kind.ToString(),
    };
}

public readonly record struct UsbId(int Vid, int Pid)
{
    private static readonly Regex Pattern = new(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase);

    /// <summary>Pulls VID/PID out of a PnP device id like USB\VID_CAFE&amp;PID_4011&amp;MI_00\...</summary>
    public static bool TryParse(string pnpDeviceId, out UsbId id)
    {
        id = default;
        if (!pnpDeviceId.StartsWith("USB", StringComparison.OrdinalIgnoreCase)) return false;
        var m = Pattern.Match(pnpDeviceId);
        if (!m.Success) return false;
        id = new UsbId(Convert.ToInt32(m.Groups[1].Value, 16), Convert.ToInt32(m.Groups[2].Value, 16));
        return true;
    }
}

public sealed record DeviceCandidate(string Port, BoardKind Kind, UsbId Usb);

public static class DeviceScanner
{
    private static readonly Regex ComPort = new(@"\((COM\d+)\)");

    /// <summary>Touch Deck serial ports currently present, RP2040 boards first.</summary>
    public static IReadOnlyList<DeviceCandidate> Scan()
    {
        var entities = new List<(string Name, string Pnp)>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
        foreach (var o in searcher.Get())
            using (o)
                entities.Add(((o["Name"] as string) ?? "", (o["PNPDeviceID"] as string) ?? ""));
        return FromEntities(entities);
    }

    /// <summary>The pure part of <see cref="Scan"/>: classify (name, PnP id) pairs.</summary>
    public static IReadOnlyList<DeviceCandidate> FromEntities(IEnumerable<(string Name, string Pnp)> entities)
    {
        var found = new List<DeviceCandidate>();
        foreach (var (name, pnp) in entities)
        {
            var com = ComPort.Match(name);
            if (!com.Success || !UsbId.TryParse(pnp, out var usb)) continue;
            if (BoardKinds.FromUsb(usb.Vid, usb.Pid) is { } kind) found.Add(new DeviceCandidate(com.Groups[1].Value, kind, usb));
        }
        return found
            .OrderBy(d => d.Kind)
            .ThenBy(d => int.Parse(d.Port.AsSpan(3)))
            .ToList();
    }
}
