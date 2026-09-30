using System.Text;
using TouchDeck.Core.Firmware;

namespace TouchDeck.Tests;

/// <summary>
/// A firmware file names its chip (UF2 family IDs) and its board (the TDBOARD:&lt;model&gt;; marker
/// the firmware embeds). The app installs a file only when both match the board it flashes.
/// </summary>
public class Uf2InspectTests
{
    internal const uint Rp2040 = 0xE48BFF56, Rp2350ArmS = 0xE48BFF59, Rp2350Absolute = 0xE48BFF57, Esp32 = 0x1C5F21B0;

    /// <summary>A UF2 image of <paramref name="payload"/> at 0x10000000 in 256-byte blocks.</summary>
    internal static byte[] Image(byte[] payload, uint family, bool absoluteBlock = false)
    {
        var blocks = new List<byte[]>();
        int n = (payload.Length + 255) / 256;
        for (int i = 0; i < n; i++)
            blocks.Add(Block(0x10000000u + (uint)(i * 256), payload.AsSpan(i * 256, Math.Min(256, payload.Length - i * 256)), family));
        if (absoluteBlock) blocks.Add(Block(0x10FFFF00u, new byte[256], Rp2350Absolute));   // what SDK 2.x adds for RP2350
        return blocks.SelectMany(b => b).ToArray();
    }

    private static byte[] Block(uint addr, ReadOnlySpan<byte> data, uint family)
    {
        var b = new byte[512];
        BitConverter.GetBytes(0x0A324655u).CopyTo(b, 0);
        BitConverter.GetBytes(0x9E5D5157u).CopyTo(b, 4);
        BitConverter.GetBytes(0x00002000u).CopyTo(b, 8);
        BitConverter.GetBytes(addr).CopyTo(b, 12);
        BitConverter.GetBytes(256u).CopyTo(b, 16);
        BitConverter.GetBytes(family).CopyTo(b, 28);
        data.CopyTo(b.AsSpan(32));
        BitConverter.GetBytes(0x0AB16F30u).CopyTo(b, 508);
        return b;
    }

    /// <summary>Code-ish filler with the marker at <paramref name="at"/>.</summary>
    internal static byte[] Payload(string board, int at, int size = 1024)
    {
        var p = Enumerable.Range(0, size).Select(i => (byte)(i * 7)).ToArray();
        Encoding.ASCII.GetBytes($"TDBOARD:{board};").CopyTo(p, at);
        return p;
    }

    [Fact]
    public void An_rp2040_image_names_its_chip_and_board()
    {
        var info = Uf2.Inspect(Image(Payload("rp2040-169", 300), Rp2040));
        Assert.Equal(new Uf2Info(true, Uf2Chip.Rp2040, "rp2040-169"), info);
    }

    [Fact]
    public void An_rp2350_image_with_the_absolute_block_is_an_rp2350()
    {
        var info = Uf2.Inspect(Image(Payload("rp2350-128", 40), Rp2350ArmS, absoluteBlock: true));
        Assert.Equal(new Uf2Info(true, Uf2Chip.Rp2350, "rp2350-128"), info);
    }

    [Fact]
    public void The_marker_is_found_across_a_block_boundary()
    {
        var info = Uf2.Inspect(Image(Payload("rp2350-128", 250), Rp2350ArmS));   // "TDBOARD:rp2350-128;" spans 250..268
        Assert.Equal("rp2350-128", info.Board);
    }

    [Fact]
    public void Blocks_out_of_order_are_reassembled_by_address()
    {
        var img = Image(Payload("rp2040-169", 250), Rp2040);
        var swapped = img.AsSpan(512, 512).ToArray().Concat(img.AsSpan(0, 512).ToArray()).Concat(img.AsSpan(1024).ToArray()).ToArray();
        Assert.Equal("rp2040-169", Uf2.Inspect(swapped).Board);
    }

    [Fact]
    public void An_image_without_a_marker_has_no_board()
    {
        var info = Uf2.Inspect(Image(new byte[512], Rp2040));
        Assert.True(info.Valid);
        Assert.Equal(Uf2Chip.Rp2040, info.Chip);
        Assert.Null(info.Board);
    }

    [Fact]
    public void Mixed_chips_or_foreign_families_are_invalid()
    {
        byte[] mixed = [.. Image(Payload("rp2040-169", 0, 256), Rp2040), .. Image(new byte[256], Rp2350ArmS)];
        Assert.False(Uf2.Inspect(mixed).Valid);
        Assert.False(Uf2.Inspect(Image(new byte[256], Esp32)).Valid);
        Assert.False(Uf2.Inspect(Image(new byte[256], Rp2350Absolute)).Valid);   // only the absolute block: no program
        Assert.False(Uf2.Inspect([]).Valid);
        Assert.False(Uf2.Inspect(new byte[300]).Valid);
    }

    [Fact]
    public void The_old_rp2040_check_still_means_a_valid_rp2040_image()
    {
        Assert.True(Uf2.IsRp2040Image(Image(new byte[256], Rp2040)));
        Assert.False(Uf2.IsRp2040Image(Image(new byte[256], Rp2350ArmS)));
    }

    [Theory]
    [InlineData("Fixtures/INFO_UF2-rp2350.txt", Uf2Chip.Rp2350)]
    public void The_rp2350_boot_drive_is_found_by_its_real_info_file(string fixture, Uf2Chip chip)
    {
        var root = Directory.CreateTempSubdirectory("touchdeck-drive-").FullName;
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, fixture), Path.Combine(root, "INFO_UF2.TXT"));
            Assert.Equal(root, Uf2.FindBootDrive([root], chip));
            Assert.Equal(root, Uf2.FindBootDrive([root]));                      // any RP bootloader
            Assert.Null(Uf2.FindBootDrive([root], Uf2Chip.Rp2040));             // not an RP2040's drive
        }
        finally { Directory.Delete(root, true); }
    }

    [SkippableTheory]
    [InlineData(@"build\watch.uf2", Uf2Chip.Rp2040, "rp2040-169")]
    [InlineData(@"build-rp2350\deck128.uf2", Uf2Chip.Rp2350, "rp2350-128")]
    public void The_real_builds_name_their_board(string file, Uf2Chip chip, string board)
    {
        var uf2 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\..\..\..\..\..\", file));
        Skip.IfNot(File.Exists(uf2), "firmware not built");
        Assert.Equal(new Uf2Info(true, chip, board), Uf2.Inspect(File.ReadAllBytes(uf2)));
    }
}
