using System.Diagnostics;
using System.IO;
using System.Text;
using TouchDeck.Core.Session;

namespace TouchDeck.Core.Updates;

/// <summary>Outcome of a check: a choice (possibly empty), "nothing published yet", or an error text.</summary>
public sealed record UpdateCheckOutcome(UpdateChoice? Choice, bool NothingPublished, string? Error);

/// <summary>Checks the feed and downloads packages into one per-user folder under fixed names.</summary>
public sealed class UpdateService(UpdateClient client, string downloadDir)
{
    public static string DefaultDownloadDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TouchDeck", "Updates");

    public async Task<UpdateCheckOutcome> CheckAsync(Version currentApp, FirmwareInfo? device, CancellationToken ct)
    {
        try
        {
            var feed = await client.FetchFeedAsync(ct);
            if (feed is null) return new UpdateCheckOutcome(null, NothingPublished: true, null);
            return new UpdateCheckOutcome(UpdateSelector.Select(feed, currentApp, device), false, null);
        }
        catch (UpdateFeedException e)
        {
            return new UpdateCheckOutcome(null, false, e.Message);
        }
    }

    public Task<string> DownloadAppAsync(UpdatePackage p, IProgress<double>? progress, CancellationToken ct) =>
        DownloadAsync(p.Url, p.Sha256, p.Size, "TouchDeck-update.msi", progress, ct);

    /// <summary>The board id was validated by the feed parser ([a-z0-9-]), so it is safe in a file name.</summary>
    public Task<string> DownloadFirmwareAsync(FirmwarePackage p, IProgress<double>? progress, CancellationToken ct) =>
        DownloadAsync(p.Url, p.Sha256, p.Size, $"{p.Board}-update.uf2", progress, ct);

    private static readonly System.Text.RegularExpressions.Regex OwnFile =
        new(@"^(TouchDeck-update\.msi|[a-z0-9][a-z0-9-]*-update\.uf2)(\.part)?$");

    /// <summary>
    /// Removes the updater's own files (never anything else), after checking the folder is a real
    /// folder: a junction planted in its place would redirect the deletes and the downloads.
    /// </summary>
    public static void ClearDownloads(string dir)
    {
        if (!Directory.Exists(dir)) return;
        if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new UpdateVerificationException("The update folder is a link; refusing to use it.");
        foreach (var f in Directory.GetFiles(dir))
            if (OwnFile.IsMatch(Path.GetFileName(f))) File.Delete(f);
    }

    private async Task<string> DownloadAsync(Uri url, string sha, long size, string name, IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(downloadDir);
        ClearDownloads(downloadDir);                                           // nothing stale is ever reused
        var dest = Path.Combine(downloadDir, name);
        await client.DownloadAsync(url, sha, size, dest, progress, ct);
        return dest;
    }
}

/// <summary>
/// Starts the MSI and relaunches the app when it is done. The script is fixed and passed
/// base64-encoded; the two paths reach it only through environment variables, so no path or
/// feed text is ever parsed as a command.
/// </summary>
public static class InstallerLaunch
{
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        $msiexec = Join-Path $env:SystemRoot 'System32\msiexec.exe'
        $p = Start-Process -FilePath $msiexec -ArgumentList @('/i', ('"' + $env:TD_UPDATE_MSI + '"'), '/passive', '/norestart') -Wait -PassThru
        if ($p.ExitCode -eq 0 -or $p.ExitCode -eq 3010) { Start-Process -FilePath $env:TD_UPDATE_EXE }
        """;

    public static ProcessStartInfo Create(string msiPath, string exePath)
    {
        var ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        var psi = new ProcessStartInfo(ps)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden",
                                  "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) })
            psi.ArgumentList.Add(a);
        psi.Environment["TD_UPDATE_MSI"] = msiPath;
        psi.Environment["TD_UPDATE_EXE"] = exePath;
        return psi;
    }
}
