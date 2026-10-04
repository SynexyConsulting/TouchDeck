using TouchDeck.Core.App;
using TouchDeck.Core.Devices;
using TouchDeck.Core.Firmware;

namespace TouchDeck.Tests;

public class BoardsTests
{
    [Fact]
    public void Log_lines_name_their_board()
    {
        var t = new DateTime(2026, 10, 3, 4, 5, 6);
        Assert.Equal("04:05:06  COM6: Connected", new LogEntry(t, "COM6", "Connected").ToString());
        Assert.Equal("04:05:06  Touch Deck started", new LogEntry(t, null, "Touch Deck started").ToString());
    }

    [Fact]
    public void Only_selected_board_keeps_its_lines_and_the_app_lines()
    {
        var t = DateTime.Now;
        LogEntry mine = new(t, "COM6", "a"), other = new(t, "COM7", "b"), app = new(t, null, "c");
        Assert.True(mine.Shows("COM6", onlySelected: true));
        Assert.False(other.Shows("COM6", onlySelected: true));
        Assert.True(app.Shows("COM6", onlySelected: true));
        Assert.True(other.Shows("COM6", onlySelected: false));
    }

    [Theory]
    [InlineData(new string[0], null, null, null, null)]                       // nothing attached
    [InlineData(new[] { "COM6" }, null, "COM6", null, "COM6")]                // the first board
    [InlineData(new[] { "COM6", "COM7" }, "COM6", "COM7", null, "COM6")]      // a second one doesn't take the tab
    [InlineData(new[] { "COM6", "COM7" }, "COM6", "COM7", "COM7", "COM7")]    // unless it was used last
    [InlineData(new[] { "COM7", "COM9" }, "COM6", null, null, "COM7")]        // the selected one went away
    [InlineData(new[] { "COM6", "COM7" }, "COM7", null, "COM6", "COM7")]      // other changes keep the tab
    public void The_selected_tab_follows_the_rules(string[] keys, string? current, string? added, string? preferred, string? expected) =>
        Assert.Equal(expected, BoardSelection.Next(keys, current, added, preferred));

    [Fact]
    public void Tabs_show_the_model_and_a_short_port()
    {
        Assert.Equal("RP2040 1.69 · COM6", BoardLabels.Tab(BoardLabels.Model(BoardKind.Rp2040, "rp2040-169"), "COM6"));
        Assert.Equal("RP2350 1.28 · usbmodem1101", BoardLabels.Tab(BoardLabels.Model(BoardKind.Rp2040, "rp2350-128"), "/dev/cu.usbmodem1101"));
        Assert.Equal("ESP32-C3 · COM7", BoardLabels.Tab(BoardLabels.Model(BoardKind.Esp32C3, null), "COM7"));
        Assert.Equal("RP2350 bootloader", BoardLabels.Tab(BoardLabels.Model(new NewBoard(Uf2Chip.Rp2350, NewBoardState.Bootloader, null)), null));
        Assert.Equal("RP2040 (not Touch Deck) · COM9",
            BoardLabels.Tab(BoardLabels.Model(new NewBoard(Uf2Chip.Rp2040, NewBoardState.StockFirmware, "COM9")), "COM9"));
    }
}
