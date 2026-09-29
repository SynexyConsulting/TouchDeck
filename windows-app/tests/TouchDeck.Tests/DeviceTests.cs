using System.Text;
using TouchDeck.Core.Devices;

namespace TouchDeck.Tests;

public class UsbIdTests
{
    [Theory]
    [InlineData(@"USB\VID_CAFE&PID_4011&MI_00\9&1618F6D4&0&0000", 0xCAFE, 0x4011)]
    [InlineData(@"USB\VID_303A&PID_1001&MI_00\9&1CE80327&0&0000", 0x303A, 0x1001)]
    [InlineData(@"usb\vid_2e8a&pid_000a\E46358B1074D1335", 0x2E8A, 0x000A)]
    public void Parses_vid_and_pid(string pnp, int vid, int pid)
    {
        Assert.True(UsbId.TryParse(pnp, out var id));
        Assert.Equal((vid, pid), (id.Vid, id.Pid));
    }

    [Theory]
    [InlineData(@"ACPI\PNP0501\0")]
    [InlineData(@"BTHENUM\{00001101-0000-1000-8000-00805F9B34FB}")]
    [InlineData("")]
    public void Rejects_non_usb_ids(string pnp) => Assert.False(UsbId.TryParse(pnp, out _));
}

public class DeviceScannerTests
{
    [Fact]
    public void Finds_touch_decks_and_orders_rp2040_first()
    {
        var found = DeviceScanner.FromEntities(
        [
            ("USB Serial Device (COM7)", @"USB\VID_303A&PID_1001&MI_00\9&1"),
            ("Communications Port (COM1)", @"ACPI\PNP0501\0"),
            ("USB Serial Device (COM6)", @"USB\VID_CAFE&PID_4011&MI_00\9&2"),
            ("HID Keyboard Device", @"HID\VID_CAFE&PID_4011&MI_02\A&1"),   // no COM port
            ("USB Serial Device (COM9)", @"USB\VID_1A86&PID_7523\5&1"),     // unrelated CH340
        ]);
        Assert.Equal([("COM6", BoardKind.Rp2040), ("COM7", BoardKind.Esp32C3)],
                     found.Select(d => (d.Port, d.Kind)).ToList());
    }

    [Fact]
    public void Serial_control_lines_follow_the_board()
    {
        Assert.True(BoardKinds.DtrHigh(BoardKind.Rp2040));      // TinyUSB only sends with DTR set
        Assert.False(BoardKinds.DtrHigh(BoardKind.Esp32C3));    // DTR/RTS are its reset lines
    }
}

public class LineSplitterTests
{
    [Fact]
    public void Splits_lines_across_partial_reads_and_drops_carriage_returns()
    {
        var split = new LineSplitter();
        var lines = new List<string>();
        lines.AddRange(split.Feed(Encoding.ASCII.GetBytes("PON")));
        lines.AddRange(split.Feed(Encoding.ASCII.GetBytes("G\r\nLOG a b\nK 02")));
        lines.AddRange(split.Feed(Encoding.ASCII.GetBytes(" 04\n")));
        Assert.Equal(["PONG", "LOG a b", "K 02 04"], lines);
    }
}

/// <summary>Real hardware: skipped unless a Touch Deck is plugged in.</summary>
[Collection("Board")]   // one test at a time on the real serial port
public class DeviceSmokeTests
{
    [SkippableFact]
    public void A_connected_board_answers_hello_with_pong()
    {
        var device = DeviceScanner.Scan().FirstOrDefault();
        Skip.If(device is null, "no Touch Deck connected");
        using var t = new SerialPortTransport(device!);
        try { t.Open(); }
        catch (UnauthorizedAccessException) { Skip.If(true, $"{device!.Port} is busy (helper running?)"); }
        t.WriteLine("HELLO");
        var deadline = DateTime.UtcNow.AddSeconds(2);
        string? line = null;
        while (DateTime.UtcNow < deadline && line != "PONG") line = t.ReadLine(TimeSpan.FromMilliseconds(100));
        Assert.Equal("PONG", line);
    }
}
