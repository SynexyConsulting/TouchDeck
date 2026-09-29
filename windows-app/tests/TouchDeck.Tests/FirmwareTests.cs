using TouchDeck.Core.Firmware;
using TouchDeck.Core.Session;

namespace TouchDeck.Tests;

public sealed class FirmwareTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("touchdeck-fw-").FullName;

    private static byte[] Block(uint family = Uf2.Rp2040Family, uint endMagic = 0x0AB16F30)
    {
        var b = new byte[512];
        BitConverter.GetBytes(0x0A324655u).CopyTo(b, 0);
        BitConverter.GetBytes(0x9E5D5157u).CopyTo(b, 4);
        BitConverter.GetBytes(0x00002000u).CopyTo(b, 8);
        BitConverter.GetBytes(family).CopyTo(b, 28);
        BitConverter.GetBytes(endMagic).CopyTo(b, 508);
        return b;
    }

    private string WriteImage(params byte[][] blocks)
    {
        var path = Path.Combine(dir, "fw.uf2");
        File.WriteAllBytes(path, blocks.SelectMany(b => b).ToArray());
        return path;
    }

    [Fact]
    public void Accepts_rp2040_uf2_and_rejects_other_images()
    {
        Assert.True(Uf2.IsRp2040Image([.. Block(), .. Block()]));
        Assert.False(Uf2.IsRp2040Image(Block(family: 0x1C5F21B0)));          // ESP32 family
        Assert.False(Uf2.IsRp2040Image(Block(endMagic: 0)));
        Assert.False(Uf2.IsRp2040Image(Block().AsSpan(0, 300)));
        Assert.False(Uf2.IsRp2040Image([]));
    }

    [SkippableFact]
    public void The_real_build_is_a_valid_image()
    {
        var uf2 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\build\watch.uf2"));
        Skip.IfNot(File.Exists(uf2), "firmware not built");
        Assert.True(Uf2.IsRp2040Image(File.ReadAllBytes(uf2)));
    }

    [Fact]
    public void Finds_the_boot_drive_by_its_info_file()
    {
        var other = Directory.CreateDirectory(Path.Combine(dir, "usbstick")).FullName;
        var boot = Directory.CreateDirectory(Path.Combine(dir, "boot")).FullName;
        File.WriteAllText(Path.Combine(other, "INFO_UF2.TXT"), "UF2 Bootloader v1.0\nBoard-ID: SAMD21\n");
        File.WriteAllText(Path.Combine(boot, "INFO_UF2.TXT"), "UF2 Bootloader v3.0\nModel: Raspberry Pi RP2\nBoard-ID: RPI-RP2\n");
        Assert.Equal(boot, Uf2.FindBootDrive([Path.Combine(dir, "missing"), other, boot]));
        Assert.Null(Uf2.FindBootDrive([other]));
    }

    [Theory]
    [InlineData("1.5.0", "1.4.9", true)]
    [InlineData("1.5.0", "1.5.0", false)]
    [InlineData("1.5.0", "1.10.0", false)]   // numeric, not string, comparison
    [InlineData("1.5.0", null, true)]        // pre-VER firmware
    public void Offers_update_only_to_older_firmware(string bundled, string? running, bool offer)
    {
        var fw = new BundledFirmware("rp2040-169", bundled, "rp2040-169.uf2");
        var info = running is null ? FirmwareInfo.Unknown : new FirmwareInfo("rp2040-169", running, "");
        Assert.Equal(offer, fw.IsNewerThan(info));
    }

    [Fact]
    public void Manifest_is_read_by_board()
    {
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            """[{"board":"rp2040-169","version":"1.5.0","file":"rp2040-169.uf2"}]""");
        var bundle = BundledFirmware.LoadManifest(dir);
        Assert.Equal("1.5.0", BundledFirmware.For(bundle, "rp2040-169")!.Version);
        Assert.Null(BundledFirmware.For(bundle, "esp32c3-128"));
        Assert.Empty(BundledFirmware.LoadManifest(Path.Combine(dir, "nope")));
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

    [Fact]
    public async Task Full_update_boots_copies_and_confirms_the_version()
    {
        var uf2 = WriteImage(Block());
        var log = new List<string>();
        int probes = 0;
        var result = await FirmwareUpdater.UpdateRp2040Async(uf2, Steps(log,
            () => log.Contains("BOOT") && ++probes > 2 ? @"J:\" : null,
            () => new FirmwareInfo("rp2040-169", "1.5.0", "x")));
        Assert.True(result.Ok, result.Message);
        Assert.Equal(["BOOT", @"copy fw.uf2 -> J:\"], log);
    }

    [Fact]
    public async Task Board_already_in_bootloader_skips_boot()
    {
        var log = new List<string>();
        var result = await FirmwareUpdater.UpdateRp2040Async(WriteImage(Block()),
            Steps(log, () => @"J:\", () => new FirmwareInfo("rp2040-169", "1.5.0", "x")));
        Assert.True(result.Ok);
        Assert.DoesNotContain("BOOT", log);
    }

    [Fact]
    public async Task No_drive_fails_without_copying()
    {
        var log = new List<string>();
        var result = await FirmwareUpdater.UpdateRp2040Async(WriteImage(Block()), Steps(log, () => null, () => null));
        Assert.False(result.Ok);
        Assert.Contains("RPI-RP2", result.Message);
        Assert.Equal(["BOOT"], log);
    }

    [Fact]
    public async Task Bad_image_is_refused_before_touching_the_board()
    {
        var log = new List<string>();
        var result = await FirmwareUpdater.UpdateRp2040Async(WriteImage(Block(family: 1)), Steps(log, () => @"J:\", () => null));
        Assert.False(result.Ok);
        Assert.Empty(log);
    }

    [Fact]
    public async Task Board_not_returning_is_reported()
    {
        var result = await FirmwareUpdater.UpdateRp2040Async(WriteImage(Block()), Steps([], () => @"J:\", () => null));
        Assert.False(result.Ok);
        Assert.Contains("did not come back", result.Message);
    }

    public void Dispose() => Directory.Delete(dir, true);
}
