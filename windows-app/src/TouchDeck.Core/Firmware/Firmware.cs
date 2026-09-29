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

public static class Uf2
{
    private const uint Magic0 = 0x0A324655, Magic1 = 0x9E5D5157, MagicEnd = 0x0AB16F30;
    private const uint FlagFamilyId = 0x00002000;
    public const uint Rp2040Family = 0xE48BFF56;

    /// <summary>
    /// True if every 512-byte block is a UF2 block for the RP2040. Checked before copying:
    /// the bootloader silently ignores anything else and the board would just sit there.
    /// </summary>
    public static bool IsRp2040Image(ReadOnlySpan<byte> image)
    {
        if (image.Length == 0 || image.Length % 512 != 0) return false;
        for (int off = 0; off < image.Length; off += 512)
        {
            var b = image.Slice(off, 512);
            if (U32(b, 0) != Magic0 || U32(b, 4) != Magic1 || U32(b, 508) != MagicEnd) return false;
            if ((U32(b, 8) & FlagFamilyId) == 0 || U32(b, 28) != Rp2040Family) return false;
        }
        return true;
    }

    private static uint U32(ReadOnlySpan<byte> b, int at) => BitConverter.ToUInt32(b.Slice(at, 4));

    /// <summary>The RP2040 bootloader's drive: a root holding INFO_UF2.TXT that names RPI-RP2.</summary>
    public static string? FindBootDrive(IEnumerable<string> roots)
    {
        foreach (var root in roots)
        {
            try
            {
                var info = Path.Combine(root, "INFO_UF2.TXT");
                if (File.Exists(info) && File.ReadAllText(info).Contains("RPI-RP2", StringComparison.Ordinal))
                    return root;
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
