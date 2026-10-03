using System.IO;
using System.IO.Ports;
using System.Management;
using System.Text.RegularExpressions;
using TouchDeck.Core.Devices;

namespace TouchDeck.Core.Firmware;

/// <summary>A Touch Deck model the app can install firmware on (a UF2 board).</summary>
public sealed record BoardModel(string Board, string Name, Uf2Chip Chip);

public static class BoardModels
{
    public static IReadOnlyList<BoardModel> All { get; } =
    [
        new("rp2040-169", "Touch LCD 1.69 (rectangle)", Uf2Chip.Rp2040),
        new("rp2350-128", "Touch LCD 1.28 (round)", Uf2Chip.Rp2350),
    ];

    /// <summary>The models built for a chip. The chip is known from USB; the screen isn't.</summary>
    public static IReadOnlyList<BoardModel> For(Uf2Chip chip) => All.Where(m => m.Chip == chip).ToList();

    public static BoardModel? Find(string board) => All.FirstOrDefault(m => m.Board == board);
}

public enum NewBoardState
{
    /// <summary>Running a stock Pico SDK program with USB serial (e.g. the factory demo).</summary>
    StockFirmware,
    /// <summary>Sitting in its UF2 bootloader (a drive named RPI-RP2 or RP2350).</summary>
    Bootloader,
}

/// <summary>A Raspberry Pi board that isn't running Touch Deck yet.</summary>
public sealed record NewBoard(Uf2Chip Chip, NewBoardState State, string? Port)
{
    public string Describe() =>
        $"{(Chip == Uf2Chip.Rp2350 ? "RP2350" : "RP2040")} board " +
        (State == NewBoardState.Bootloader ? "in its bootloader" : $"with other firmware ({Port})");
}

public static class NewBoards
{
    private static readonly Regex ComPort = new(@"\((COM\d+)\)");

    /// <summary>Raspberry Pi USB IDs (VID 2E8A): stock SDK programs with USB serial, and the bootloaders.</summary>
    private static (Uf2Chip Chip, NewBoardState State)? FromUsb(int vid, int pid) => (vid, pid) switch
    {
        (0x2E8A, 0x000A) => (Uf2Chip.Rp2040, NewBoardState.StockFirmware),
        (0x2E8A, 0x0003) => (Uf2Chip.Rp2040, NewBoardState.Bootloader),
        (0x2E8A, 0x0009) => (Uf2Chip.Rp2350, NewBoardState.StockFirmware),
        (0x2E8A, 0x000F) => (Uf2Chip.Rp2350, NewBoardState.Bootloader),
        _ => null,
    };

    public static IReadOnlyList<NewBoard> Scan()
    {
        var entities = new List<(string Name, string Pnp)>();
        using var searcher = new ManagementObjectSearcher(
            @"SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE PNPDeviceID LIKE 'USB\\VID_2E8A%'");
        foreach (var o in searcher.Get())
            using (o)
                entities.Add(((o["Name"] as string) ?? "", (o["PNPDeviceID"] as string) ?? ""));
        return FromEntities(entities);
    }

    /// <summary>
    /// The pure part of <see cref="Scan"/>: one entry per board. A USB device has several interfaces;
    /// only its serial interface names a COM port, so two stock boards are told apart by their ports.
    /// A bootloader has no port, so one entry per chip.
    /// </summary>
    public static IReadOnlyList<NewBoard> FromEntities(IEnumerable<(string Name, string Pnp)> entities)
    {
        var withPort = new HashSet<NewBoard>();
        var seen = new HashSet<(Uf2Chip, NewBoardState)>();
        foreach (var (name, pnp) in entities)
        {
            if (!UsbId.TryParse(pnp, out var usb) || FromUsb(usb.Vid, usb.Pid) is not { } key) continue;
            seen.Add(key);
            var com = ComPort.Match(name);
            if (com.Success) withPort.Add(new NewBoard(key.Chip, key.State, com.Groups[1].Value));
        }
        var portless = seen.Where(k => !withPort.Any(b => b.Chip == k.Item1 && b.State == k.Item2))
                           .Select(k => new NewBoard(k.Item1, k.Item2, null));
        return withPort.Concat(portless)
            .OrderBy(b => b.Chip).ThenBy(b => b.State).ThenBy(b => b.Port?.Length).ThenBy(b => b.Port, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Reboots a stock Pico SDK program into its bootloader: opening its USB serial port at 1200 baud
    /// is the SDK's reset signal (on by default in stdio USB). False if the port couldn't be opened.
    /// </summary>
    public static bool RebootToBootloader(string port)
    {
        try
        {
            using var sp = new SerialPort(port, 1200) { DtrEnable = true };
            sp.Open();
            Thread.Sleep(100);
            sp.Close();
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }
}
