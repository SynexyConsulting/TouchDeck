using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using TouchDeck.Core.Session;

namespace TouchDeck.Core.Updates;

/// <summary>A feed or download failed validation or couldn't be read (shown as text, never trusted).</summary>
public sealed class UpdateFeedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A download didn't match what the feed promised (host, size or SHA-256). Nothing is kept.</summary>
public sealed class UpdateVerificationException(string message) : Exception(message);

public sealed record UpdatePackage(Version Version, Uri Url, string Sha256, long Size);
public sealed record FirmwarePackage(string Board, Version Version, Uri Url, string Sha256, long Size);

/// <summary>Where updates come from, and which URLs that source may point at.</summary>
public sealed class UpdateSource
{
    public const string OfficialAssetPrefix = "https://github.com/SynexyConsulting/TouchDeckUpdates/releases/download/";
    public static readonly Uri OfficialFeed = new("https://github.com/SynexyConsulting/TouchDeckUpdates/releases/latest/download/updates.json");

    /// <summary>
    /// The feed-signing public key (ECDSA P-256, SubjectPublicKeyInfo). Only a feed signed with its
    /// private key is trusted; that key never lives in this repo (see the README's release section).
    /// </summary>
    public const string OfficialPublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEme/lCuDrapxNW0d+7Lk9/qQTKbXAPhv1RjJLIDbe8Go9JWZFXQOyQ41g/o2OlU5LJHjA01RYYgCOG7+Irdtwhw==";

    public static UpdateSource Official { get; } = new(OfficialFeed, OfficialPublicKey, test: false);

    public Uri FeedUrl { get; }
    public Uri SignatureUrl => new(FeedUrl.AbsoluteUri + ".sig");
    public string PublicKey { get; }
    public bool IsTest { get; }

    private UpdateSource(Uri feed, string publicKey, bool test)
    {
        FeedUrl = feed;
        PublicKey = publicKey;
        IsTest = test;
    }

    /// <summary>A loopback feed and its test key, for end-to-end tests only. Anything but loopback is refused.</summary>
    public static UpdateSource ForTest(Uri feed, string publicKey)
    {
        if (!feed.IsAbsoluteUri || !feed.IsLoopback || feed.Scheme is not ("http" or "https"))
            throw new ArgumentException("A test update feed must be an http(s) loopback URL.", nameof(feed));
        return new UpdateSource(feed, publicKey, test: true);
    }

    /// <summary>Asset URLs a feed may name: the update repo's release downloads (or, in test mode, the feed's own server).</summary>
    public bool IsAllowedAsset(Uri u)
    {
        if (!u.IsAbsoluteUri) return false;
        if (IsTest) return SameServer(u, FeedUrl);
        return u.Scheme == Uri.UriSchemeHttps && u.AbsoluteUri.StartsWith(OfficialAssetPrefix, StringComparison.Ordinal);
    }

    /// <summary>Redirect targets: GitHub's own https hosts (release downloads land on githubusercontent.com).</summary>
    public bool IsAllowedRedirect(Uri u)
    {
        if (!u.IsAbsoluteUri) return false;
        if (IsTest) return SameServer(u, FeedUrl);
        if (u.Scheme != Uri.UriSchemeHttps) return false;
        var h = u.Host.ToLowerInvariant();
        return h is "github.com" || h.EndsWith(".github.com", StringComparison.Ordinal)
            || h is "githubusercontent.com" || h.EndsWith(".githubusercontent.com", StringComparison.Ordinal);
    }

    private static bool SameServer(Uri a, Uri b) =>
        a.Scheme == b.Scheme && a.IdnHost == b.IdnHost && a.Port == b.Port;
}

/// <summary>updates.json.sig: base64 of the raw ECDSA P-256 signature (r||s, 64 bytes) over the feed's bytes.</summary>
public static class FeedSignature
{
    public static bool Verify(byte[] feed, byte[] signatureFile, string publicKeySpki)
    {
        try
        {
            var sig = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(signatureFile).Trim());
            if (sig.Length != 64) return false;
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpki), out _);
            if (key.KeySize != 256) return false;
            return key.VerifyData(feed, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>The published updates.json: newest app per OS and newest firmware per board.</summary>
public sealed record UpdateFeed(UpdatePackage? WindowsApp, IReadOnlyList<FirmwarePackage> Firmware)
{
    public const long MaxAppBytes = 200L * 1024 * 1024;
    public const long MaxFirmwareBytes = 4L * 1024 * 1024;
    private static readonly Regex Sha = new("^[0-9a-fA-F]{64}$");
    private static readonly Regex Board = new("^[a-z0-9][a-z0-9-]{0,31}$");

    /// <summary>Parses and validates; any bad entry rejects the whole feed (a tampered feed is not partly trusted).</summary>
    public static UpdateFeed Parse(string json, UpdateSource source)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("schema", out var schema) || schema.ValueKind != JsonValueKind.Number || schema.GetInt32() != 1)
                throw new UpdateFeedException("Unsupported update feed (schema).");

            UpdatePackage? app = null;
            if (root.TryGetProperty("app", out var apps) && apps.ValueKind == JsonValueKind.Object &&
                apps.TryGetProperty("windows", out var win) && win.ValueKind == JsonValueKind.Object)
            {
                var (v, u, sha, size) = Entry(win, source, MaxAppBytes);
                app = new UpdatePackage(v, u, sha, size);
            }

            var fw = new List<FirmwarePackage>();
            if (root.TryGetProperty("firmware", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in list.EnumerateArray())
                {
                    var board = Str(e, "board");
                    if (!Board.IsMatch(board)) throw new UpdateFeedException("Invalid board id in the update feed.");
                    var (v, u, sha, size) = Entry(e, source, MaxFirmwareBytes);
                    fw.Add(new FirmwarePackage(board, v, u, sha, size));
                }
            }
            return new UpdateFeed(app, fw);
        }
        catch (JsonException e)
        {
            throw new UpdateFeedException("The update feed is not valid JSON.", e);
        }
        catch (InvalidOperationException e)
        {
            throw new UpdateFeedException("The update feed has an unexpected shape.", e);
        }
    }

    private static (Version, Uri, string, long) Entry(JsonElement e, UpdateSource source, long max)
    {
        if (!Version.TryParse(Str(e, "version"), out var v) || v.Build < 0)
            throw new UpdateFeedException("Invalid version in the update feed.");
        if (!Uri.TryCreate(Str(e, "url"), UriKind.Absolute, out var url) || !source.IsAllowedAsset(url))
            throw new UpdateFeedException("The update feed points outside the Touch Deck update repository.");
        var sha = Str(e, "sha256");
        if (!Sha.IsMatch(sha)) throw new UpdateFeedException("The update feed has no valid SHA-256.");
        if (!e.TryGetProperty("size", out var s) || s.ValueKind != JsonValueKind.Number || !s.TryGetInt64(out var size) ||
            size <= 0 || size > max)
            throw new UpdateFeedException("The update feed has an invalid size.");
        return (new Version(v.Major, v.Minor, v.Build), url, sha.ToLowerInvariant(), size);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
}

public sealed record UpdateChoice(UpdatePackage? App, FirmwarePackage? Firmware);

public static class UpdateSelector
{
    /// <summary>Strictly newer only: never a downgrade. Firmware only for the connected board.</summary>
    public static UpdateChoice Select(UpdateFeed feed, Version currentApp, FirmwareInfo? device)
    {
        var app = feed.WindowsApp is { } a && a.Version > Normalize(currentApp) ? a : null;
        FirmwarePackage? fw = null;
        if (device is { Known: true } d)
        {
            var mine = feed.Firmware.FirstOrDefault(f => f.Board == d.Board);
            if (mine is not null && (d.SemVer is not { } running || mine.Version > Normalize(running))) fw = mine;
        }
        return new UpdateChoice(app, fw);
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}

/// <summary>
/// Reads the feed and downloads packages. Redirects are followed by hand (at most 5) and each hop
/// is checked against the source; downloads are size-capped and SHA-256 verified while streaming.
/// </summary>
public sealed class UpdateClient
{
    private const int MaxFeedBytes = 1_000_000, MaxSignatureBytes = 1024;
    private readonly UpdateSource source;
    private readonly HttpClient http;
    private readonly TimeSpan feedTimeout, idleTimeout;

    /// <param name="feedTimeout">Whole feed check (feed + signature), default 30 s.</param>
    /// <param name="idleTimeout">Longest wait for the next bytes of a download, default 30 s.</param>
    public UpdateClient(UpdateSource source, HttpMessageHandler? handler = null, TimeSpan? feedTimeout = null, TimeSpan? idleTimeout = null)
    {
        this.source = source;
        this.feedTimeout = feedTimeout ?? TimeSpan.FromSeconds(30);
        this.idleTimeout = idleTimeout ?? TimeSpan.FromSeconds(30);
        http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"TouchDeck/{CoreInfo.Version}");
    }

    /// <summary>
    /// The feed, or null when nothing has been published yet (404). The feed must carry a valid
    /// signature (updates.json.sig) from the pinned key, or it is refused before it is parsed.
    /// </summary>
    public async Task<UpdateFeed?> FetchFeedAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(feedTimeout);
        try
        {
            var feed = await ReadSmallAsync(source.FeedUrl, MaxFeedBytes, deadline.Token);
            if (feed is null) return null;
            var sig = await ReadSmallAsync(source.SignatureUrl, MaxSignatureBytes, deadline.Token)
                ?? throw new UpdateFeedException("The update feed has no signature; it is not trusted.");
            if (!FeedSignature.Verify(feed, sig, source.PublicKey))
                throw new UpdateFeedException("The update feed's signature doesn't match; it is not trusted.");
            return UpdateFeed.Parse(System.Text.Encoding.UTF8.GetString(feed), source);
        }
        catch (HttpRequestException e) { throw new UpdateFeedException($"Couldn't reach the update server: {e.Message}", e); }
        catch (OperationCanceledException e) when (!ct.IsCancellationRequested) { throw new UpdateFeedException("The update server timed out.", e); }
        catch (UpdateVerificationException e) { throw new UpdateFeedException(e.Message); }
    }

    /// <summary>A small resource (feed, signature), read with a hard size cap; null on 404.</summary>
    private async Task<byte[]?> ReadSmallAsync(Uri url, int max, CancellationToken ct)
    {
        using var resp = await SendFollowingAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode) throw new UpdateFeedException($"The update server answered {(int)resp.StatusCode}.");
        if (resp.Content.Headers.ContentLength > max) throw new UpdateFeedException("The update feed is too large.");
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buf = new byte[16384];
        int n;
        while ((n = await stream.ReadAsync(buf, ct)) > 0)
        {
            if (ms.Length + n > max) throw new UpdateFeedException("The update feed is too large.");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    /// <summary>Downloads <paramref name="url"/> to <paramref name="dest"/>; any mismatch deletes it and throws.</summary>
    public async Task DownloadAsync(Uri url, string sha256, long size, string dest, IProgress<double>? progress, CancellationToken ct)
    {
        if (!source.IsAllowedAsset(url)) throw new UpdateVerificationException("Download refused: not from the Touch Deck update repository.");
        var tmp = dest + ".part";
        try
        {
            using (var resp = await SendFollowingAsync(url, ct))
            {
                if (!resp.IsSuccessStatusCode) throw new UpdateVerificationException($"Download failed ({(int)resp.StatusCode}).");
                if (resp.Content.Headers.ContentLength is { } cl && cl != size)
                    throw new UpdateVerificationException("Download refused: its size doesn't match the update feed.");
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buf = new byte[81920];
                long total = 0;
                int n;
                while ((n = await ReadWithIdleTimeoutAsync(src, buf, ct)) > 0)
                {
                    total += n;
                    if (total > size) throw new UpdateVerificationException("Download refused: longer than the update feed says.");
                    hash.AppendData(buf, 0, n);
                    await file.WriteAsync(buf.AsMemory(0, n), ct);
                    progress?.Report((double)total / size);
                }
                if (total != size) throw new UpdateVerificationException("Download incomplete.");
                var got = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(got, sha256, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateVerificationException("Download refused: its SHA-256 doesn't match the update feed.");
            }
            File.Move(tmp, dest, overwrite: true);
        }
        catch (HttpRequestException e)
        {
            throw new UpdateVerificationException($"Download failed: {e.Message}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdateVerificationException("Download stalled; try again.");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private async Task<int> ReadWithIdleTimeoutAsync(Stream src, byte[] buf, CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(idleTimeout);
        return await src.ReadAsync(buf, idle.Token);
    }

    private async Task<HttpResponseMessage> SendFollowingAsync(Uri url, CancellationToken ct)
    {
        var current = url;
        for (int hop = 0; hop < 6; hop++)
        {
            var resp = await http.GetAsync(current, HttpCompletionOption.ResponseHeadersRead, ct);
            int code = (int)resp.StatusCode;
            if (code is not (301 or 302 or 303 or 307 or 308)) return resp;
            var loc = resp.Headers.Location;
            resp.Dispose();
            if (loc is null) throw new UpdateVerificationException("Redirect without a location.");
            var next = loc.IsAbsoluteUri ? loc : new Uri(current, loc);
            if (!source.IsAllowedRedirect(next)) throw new UpdateVerificationException("Download refused: redirected outside GitHub.");
            current = next;
        }
        throw new UpdateVerificationException("Too many redirects.");
    }
}
