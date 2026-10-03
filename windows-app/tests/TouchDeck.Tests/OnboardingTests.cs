using TouchDeck.Core.Firmware;
using TouchDeck.Core.Session;
using static TouchDeck.Tests.Uf2InspectTests;

namespace TouchDeck.Tests;

/// <summary>Finding a board without Touch Deck firmware, and installing the right firmware on it.</summary>
public sealed class OnboardingTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("touchdeck-onb-").FullName;

    private string Uf2(string board, uint family, bool absolute = false, bool marker = true)
    {
        var path = Path.Combine(dir, $"{board}.uf2");
        File.WriteAllBytes(path, Image(marker ? Payload(board, 100) : new byte[512], family, absolute));
        return path;
    }

    private static UpdateSteps Steps(List<string> log, Func<string?> drive, Func<FirmwareInfo?> running) => new()
    {
        EnterBootloader = () => log.Add("BOOT"),
        FindBootDrive = drive,
        CopyImage = (src, dst) => log.Add($"copy {Path.GetFileName(src)} -> {dst}"),
        ReadRunningFirmware = running,
        PollEvery = TimeSpan.FromMilliseconds(1),
        BootloaderTimeout = TimeSpan.FromMilliseconds(100),
        RebootTimeout = TimeSpan.FromMilliseconds(100),
    };

    private static readonly BoardModel Round = BoardModels.Find("rp2350-128")!;
    private static readonly BoardModel Rect = BoardModels.Find("rp2040-169")!;

    // ---------- which boards and models

    [Fact]
    public void Models_per_chip()
    {
        Assert.Equal(["rp2040-169"], BoardModels.For(Uf2Chip.Rp2040).Select(m => m.Board));
        Assert.Equal(["rp2350-128"], BoardModels.For(Uf2Chip.Rp2350).Select(m => m.Board));
        Assert.Equal(Uf2Chip.Rp2350, Round.Chip);
        Assert.Contains("round", Round.Name);
        Assert.Null(BoardModels.Find("esp32c3-128"));   // flashed with PlatformIO, not by the app
    }

    [Fact]
    public void Raspberry_Pi_usb_ids_are_new_boards()
    {
        var found = NewBoards.FromEntities(
        [
            ("USB Serial Device (COM10)", @"USB\VID_2E8A&PID_0009&MI_00\9&27293212&0&0000"),   // RP2350 factory demo
            ("Reset", @"USB\VID_2E8A&PID_0009&MI_02\9&27293212&0&0002"),
            ("USB Composite Device", @"USB\VID_2E8A&PID_0009\73F290A468AA3C8B"),
            ("USB Mass Storage Device", @"USB\VID_2E8A&PID_000F&MI_00\9&2125BB4D&0&0000"),     // RP2350 bootloader
            ("RP2350 Boot", @"USB\VID_2E8A&PID_000F&MI_01\9&2125BB4D&0&0001"),
            ("USB Serial Device (COM4)", @"USB\VID_2E8A&PID_000A&MI_00\7&1&0&0000"),           // RP2040 stock SDK program
            ("RP2 Boot", @"USB\VID_2E8A&PID_0003&MI_01\1&2&3"),                                 // RP2040 bootloader
            ("USB Serial Device (COM11)", @"USB\VID_CAFE&PID_4011&MI_00\9&28BA58E2&0&0000"),   // already Touch Deck
            ("Logitech mouse", @"USB\VID_046D&PID_C08B\1770395F3530"),
        ]);
        Assert.Equal(
        [
            new NewBoard(Uf2Chip.Rp2040, NewBoardState.StockFirmware, "COM4"),
            new NewBoard(Uf2Chip.Rp2040, NewBoardState.Bootloader, null),
            new NewBoard(Uf2Chip.Rp2350, NewBoardState.StockFirmware, "COM10"),
            new NewBoard(Uf2Chip.Rp2350, NewBoardState.Bootloader, null),
        ], found);
    }

    // ---------- installing

    [Fact]
    public async Task A_stock_board_is_rebooted_flashed_and_confirmed()
    {
        var log = new List<string>();
        int probes = 0;
        var result = await FirmwareUpdater.InstallAsync(Uf2("rp2350-128", Rp2350ArmS, absolute: true), Round, Steps(log,
            () => log.Contains("BOOT") && ++probes > 2 ? @"J:\" : null,
            () => new FirmwareInfo("rp2350-128", "1.7.0", "x")));
        Assert.True(result.Ok, result.Message);
        Assert.Equal(["BOOT", @"copy rp2350-128.uf2 -> J:\"], log);
    }

    [Fact]
    public async Task A_board_that_never_reaches_its_bootloader_asks_for_BOOT()
    {
        var log = new List<string>();
        var result = await FirmwareUpdater.InstallAsync(Uf2("rp2350-128", Rp2350ArmS), Round, Steps(log, () => null, () => null));
        Assert.False(result.Ok);
        Assert.Contains("Hold BOOT", result.Message);
        Assert.Contains("RP2350", result.Message);        // the drive this chip shows
        Assert.Equal(["BOOT"], log);
    }

    [Theory]
    [InlineData("rp2040-169", Rp2040)]      // another model, other chip
    [InlineData("rp2350-999", Rp2350ArmS)]  // right chip, another model
    public async Task Another_boards_firmware_is_refused_before_touching_the_board(string board, uint family)
    {
        var log = new List<string>();
        var result = await FirmwareUpdater.InstallAsync(Uf2(board, family), Round, Steps(log, () => @"J:\", () => null));
        Assert.False(result.Ok);
        Assert.Empty(log);
    }

    [Fact]
    public async Task An_rp2350_image_without_a_marker_is_refused()
    {
        var log = new List<string>();
        var result = await FirmwareUpdater.InstallAsync(Uf2("x", Rp2350ArmS, marker: false), Round, Steps(log, () => @"J:\", () => null));
        Assert.False(result.Ok);
        Assert.Empty(log);
    }

    [Fact]
    public async Task Pre_marker_rp2040_firmware_still_installs_on_the_169()
    {
        var log = new List<string>();
        var result = await FirmwareUpdater.InstallAsync(Uf2("old", Rp2040, marker: false), Rect,
            Steps(log, () => @"J:\", () => new FirmwareInfo("rp2040-169", "1.6.0", "x")));
        Assert.True(result.Ok, result.Message);
    }

    [Fact]
    public async Task A_board_coming_back_as_another_model_is_a_failure()
    {
        var result = await FirmwareUpdater.InstallAsync(Uf2("rp2350-128", Rp2350ArmS), Round,
            Steps([], () => @"J:\", () => new FirmwareInfo("rp2040-169", "1.7.0", "x")));
        Assert.False(result.Ok);
        Assert.Contains("rp2040-169", result.Message);
    }

    [Fact]
    public async Task Another_board_answering_first_is_waited_past()
    {
        // Two RP boards plugged in: the other one may answer before the flashed one is back.
        int probes = 0;
        var result = await FirmwareUpdater.InstallAsync(Uf2("rp2350-128", Rp2350ArmS), Round, Steps([], () => @"J:\",
            () => ++probes < 5 ? new FirmwareInfo("rp2040-169", "1.7.0", "x") : new FirmwareInfo("rp2350-128", "1.7.0", "x")));
        Assert.True(result.Ok, result.Message);
        Assert.Equal("rp2350-128", result.Running!.Board);
    }

    [Fact]
    public async Task A_board_of_the_other_chip_is_named_not_told_to_hold_BOOT()
    {
        // VER missed its window on a round RP2350, so the app offered the 1.69's firmware. BOOT puts the
        // board in its RP2350 bootloader, which the RP2040 install rightly never uses: say what it is.
        var log = new List<string>();
        var steps = Steps(log, () => null, () => null);
        steps = new UpdateSteps
        {
            EnterBootloader = steps.EnterBootloader, FindBootDrive = steps.FindBootDrive, CopyImage = steps.CopyImage,
            ReadRunningFirmware = steps.ReadRunningFirmware, PollEvery = steps.PollEvery,
            BootloaderTimeout = steps.BootloaderTimeout, RebootTimeout = steps.RebootTimeout,
            BootloaderChip = () => Uf2Chip.Rp2350,
        };
        var result = await FirmwareUpdater.InstallAsync(Uf2("rp2040-169", Rp2040), Rect, steps);
        Assert.False(result.Ok);
        Assert.Contains("RP2350", result.Message);
        Assert.DoesNotContain("Hold BOOT", result.Message);
        Assert.DoesNotContain(log, l => l.StartsWith("copy"));
    }

    public void Dispose() => Directory.Delete(dir, true);

    [Fact]
    public void Two_stock_boards_are_two_new_boards()
    {
        var found = NewBoards.FromEntities(
        [
            ("USB Serial Device (COM12)", @"USB\VID_2E8A&PID_000A&MI_00&1&0&0000"),
            ("Reset", @"USB\VID_2E8A&PID_000A&MI_02&1&0&0002"),
            ("USB Serial Device (COM9)", @"USB\VID_2E8A&PID_000A&MI_00&2&0&0000"),
        ]);
        Assert.Equal(
        [
            new NewBoard(Uf2Chip.Rp2040, NewBoardState.StockFirmware, "COM9"),
            new NewBoard(Uf2Chip.Rp2040, NewBoardState.StockFirmware, "COM12"),
        ], found);
    }
}
