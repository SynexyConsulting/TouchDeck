using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TouchDeck.Core.Session;

namespace TouchDeck.Core.Firmware;

/// <summary>One firmware image shipped with the app (firmware/manifest.json).</summary>
public sealed record BundledFirmware(
    [property: JsonPropertyName("board")] string Board,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("file")] string File)
{
    public static IReadOnlyList<BundledFirmware> LoadManifest(string dir)
    {
        var path = Path.Combine(dir, "manifest.json");
        if (!System.IO.File.Exists(path)) return [];
        return JsonSerializer.Deserialize<List<BundledFirmware>>(System.IO.File.ReadAllText(path)) ?? [];
    }

    public static BundledFirmware? For(IEnumerable<BundledFirmware> bundle, string board) =>
        bundle.FirstOrDefault(f => f.Board == board);

    /// <summary>Offer the update when the board is older, or too old to report a version at all.</summary>
    public bool IsNewerThan(FirmwareInfo? running)
    {
        if (running is null || !running.Known || running.SemVer is null) return true;
        return System.Version.TryParse(Version, out var mine) && mine > running.SemVer;
    }
}

public enum Uf2Chip { Unknown, Rp2040, Rp2350 }

/// <summary>What a firmware file is for: its chip (UF2 family IDs) and board (the TDBOARD marker, or null).</summary>
public sealed record Uf2Info(bool Valid, Uf2Chip Chip, string? Board);

public static class Uf2
{
    private const uint Magic0 = 0x0A324655, Magic1 = 0x9E5D5157, MagicEnd = 0x0AB16F30;
    private const uint FlagFamilyId = 0x00002000;
    public const uint Rp2040Family = 0xE48BFF56;
    public const uint Rp2350ArmSFamily = 0xE48BFF59;
    /// <summary>SDK 2.x adds one block of this family to RP2350 images (a bootrom erratum workaround).</summary>
    public const uint Rp2350AbsoluteFamily = 0xE48BFF57;
    private static readonly byte[] Marker = "TDBOARD:"u8.ToArray();

    /// <summary>
    /// Checks every 512-byte block and names the chip and board. Checked before copying: a bootloader
    /// silently ignores blocks for another chip, and another board's firmware would show nothing useful.
    /// Valid only if every block is well formed and all blocks are for one chip: RP2040, or RP2350
    /// (ARM-S, plus the absolute block). The board comes from the firmware's "TDBOARD:&lt;model&gt;;"
    /// marker, found in the payload reassembled by address, so a marker split across blocks counts.
    /// </summary>
    public static Uf2Info Inspect(ReadOnlySpan<byte> image)
    {
        var invalid = new Uf2Info(false, Uf2Chip.Unknown, null);
        if (image.Length == 0 || image.Length % 512 != 0) return invalid;
        int rp2040 = 0, rp2350 = 0, absolute = 0;
        var payload = new SortedDictionary<uint, byte[]>();
        for (int off = 0; off < image.Length; off += 512)
        {
            var b = image.Slice(off, 512);
            if (U32(b, 0) != Magic0 || U32(b, 4) != Magic1 || U32(b, 508) != MagicEnd) return invalid;
            if ((U32(b, 8) & FlagFamilyId) == 0) return invalid;
            uint family = U32(b, 28), addr = U32(b, 12), size = U32(b, 16);
            if (family == Rp2040Family) rp2040++;
            else if (family == Rp2350ArmSFamily) rp2350++;
            else if (family == Rp2350AbsoluteFamily) absolute++;
            else return invalid;
            if (size > 476) return invalid;
            payload[addr] = b.Slice(32, (int)size).ToArray();
        }
        if ((rp2040 > 0) == (rp2350 > 0)) return invalid;               // one chip, and some program
        if (rp2040 > 0 && absolute > 0) return invalid;                 // no RP2350 blocks in an RP2040 image
        return new Uf2Info(true, rp2040 > 0 ? Uf2Chip.Rp2040 : Uf2Chip.Rp2350, FindBoard(payload));
    }

    /// <summary>True if the image is a valid RP2040 image (any board).</summary>
    public static bool IsRp2040Image(ReadOnlySpan<byte> image) => Inspect(image) is { Valid: true, Chip: Uf2Chip.Rp2040 };

    // Joins address-contiguous blocks and looks for TDBOARD:<model>; in each run.
    private static string? FindBoard(SortedDictionary<uint, byte[]> blocks)
    {
        var run = new List<byte>();
        uint next = 0;
        foreach (var (addr, data) in blocks)
        {
            if (run.Count > 0 && addr != next)
            {
                if (MarkerIn(run) is { } found) return found;
                run.Clear();
            }
            run.AddRange(data);
            next = addr + (uint)data.Length;
        }
        return MarkerIn(run);
    }

    private static string? MarkerIn(List<byte> run)
    {
        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(run);
        int at = span.IndexOf(Marker);
        if (at < 0) return null;
        var rest = span[(at + Marker.Length)..];
        int end = rest.IndexOf((byte)';');
        if (end <= 0 || end > 32) return null;
        var name = System.Text.Encoding.ASCII.GetString(rest[..end]);
        return name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? name : null;
    }

    private static uint U32(ReadOnlySpan<byte> b, int at) => BitConverter.ToUInt32(b.Slice(at, 4));

    /// <summary>Board-ID in each chip's bootloader drive INFO_UF2.TXT.</summary>
    private static string BootId(Uf2Chip chip) => chip == Uf2Chip.Rp2350 ? "RP2350" : "RPI-RP2";

    /// <summary>An RP bootloader's drive: a root whose INFO_UF2.TXT names Board-ID RPI-RP2 (RP2040)
    /// or RP2350; with <paramref name="chip"/>, only that chip's.</summary>
    public static string? FindBootDrive(IEnumerable<string> roots, Uf2Chip chip = Uf2Chip.Unknown)
    {
        string[] ids = chip == Uf2Chip.Unknown ? [BootId(Uf2Chip.Rp2040), BootId(Uf2Chip.Rp2350)] : [BootId(chip)];
        foreach (var root in roots)
        {
            try
            {
                var info = Path.Combine(root, "INFO_UF2.TXT");
                if (!File.Exists(info)) continue;
                var id = File.ReadAllLines(info).Where(l => l.StartsWith("Board-ID:", StringComparison.Ordinal))
                    .Select(l => l["Board-ID:".Length..].Trim()).FirstOrDefault();
                if (id is not null && ids.Contains(id)) return root;
            }
            catch (IOException) { }                    // drive vanished mid-check
            catch (UnauthorizedAccessException) { }
        }
        return null;
    }

    public static IEnumerable<string> RemovableRoots() =>
        DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Removable && d.IsReady).Select(d => d.RootDirectory.FullName);
}

/// <summary>The steps of an RP2040 update, injectable so the flow can be tested without a board.</summary>
public sealed class UpdateSteps
{
    public required Action EnterBootloader { get; init; }
    public required Func<string?> FindBootDrive { get; init; }
    public required Action<string, string> CopyImage { get; init; }
    /// <summary>The firmware running after the copy, or null while the board hasn't come back.</summary>
    public required Func<FirmwareInfo?> ReadRunningFirmware { get; init; }
    public TimeSpan PollEvery { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeSpan BootloaderTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan RebootTimeout { get; init; } = TimeSpan.FromSeconds(20);
}

public sealed record UpdateResult(bool Ok, string Message, FirmwareInfo? Running = null);

public static class FirmwareUpdater
{
    /// <summary>BOOT (unless already in the bootloader), copy the UF2, wait for the board to answer VER.</summary>
    public static async Task<UpdateResult> UpdateRp2040Async(
        string uf2Path, UpdateSteps steps, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        byte[] image;
        try { image = await File.ReadAllBytesAsync(uf2Path, ct); }
        catch (IOException e) { return new(false, $"Can't read {Path.GetFileName(uf2Path)}: {e.Message}"); }
        if (!Uf2.IsRp2040Image(image)) return new(false, $"{Path.GetFileName(uf2Path)} is not an RP2040 UF2 image.");

        var drive = steps.FindBootDrive();
        if (drive is null)
        {
            progress?.Report("Rebooting the board into its bootloader...");
            steps.EnterBootloader();
            drive = await PollAsync(steps.FindBootDrive, steps.BootloaderTimeout, steps.PollEvery, ct);
            if (drive is null)
                return new(false, "The bootloader drive (RPI-RP2) did not appear. Hold BOOT while plugging the board in, then try again.");
        }

        progress?.Report($"Copying firmware to {drive}...");
        steps.CopyImage(uf2Path, drive);

        progress?.Report("Waiting for the board to restart...");
        var running = await PollAsync(steps.ReadRunningFirmware, steps.RebootTimeout, steps.PollEvery, ct);
        return running is null
            ? new(false, "Firmware copied, but the board did not come back as a Touch Deck.")
            : new(true, $"Updated: {running.Board} {running.Version}", running);
    }

    private static async Task<T?> PollAsync<T>(Func<T?> probe, TimeSpan timeout, TimeSpan every, CancellationToken ct) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (probe() is { } found) return found;
            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(every, ct);
        }
    }
}
