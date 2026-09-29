using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TouchDeck.Core.Updates;

namespace TouchDeck.Tests;

/// <summary>Signs feeds like tools/publish_release.py: ECDSA P-256 / SHA-256, raw r||s, base64.</summary>
internal sealed class TestSigner : IDisposable
{
    public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public string PublicKey => Convert.ToBase64String(Key.ExportSubjectPublicKeyInfo());
    public byte[] Sign(byte[] data) =>
        Encoding.ASCII.GetBytes(Convert.ToBase64String(Key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
    public void Dispose() => Key.Dispose();
}

public sealed class FeedSignatureTests : IDisposable
{
    private readonly LoopServer server = new();
    private readonly TestSigner signer = new();
    private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private byte[] FeedBytes() => Encoding.UTF8.GetBytes($$"""
        { "schema": 1, "app": { "windows": { "version": "9.9.9", "url": "{{server.Base}}/a.msi", "sha256": "{{Hex([1])}}", "size": 1 } }, "firmware": [] }
        """);

    private UpdateClient Client(string? key = null) =>
        new(UpdateSource.ForTest(new Uri($"{server.Base}/updates.json"), key ?? signer.PublicKey));

    [Fact]
    public async Task A_correctly_signed_feed_is_accepted()
    {
        var feed = FeedBytes();
        server.Serve("/updates.json", feed);
        server.Serve("/updates.json.sig", signer.Sign(feed));
        Assert.NotNull((await Client().FetchFeedAsync(CancellationToken.None))!.WindowsApp);
    }

    [Fact]
    public async Task An_unsigned_feed_is_refused()
    {
        server.Serve("/updates.json", FeedBytes());
        var e = await Assert.ThrowsAsync<UpdateFeedException>(() => Client().FetchFeedAsync(CancellationToken.None));
        Assert.Contains("signature", e.Message);
    }

    [Fact]
    public async Task A_feed_signed_with_another_key_is_refused()
    {
        var feed = FeedBytes();
        using var other = new TestSigner();
        server.Serve("/updates.json", feed);
        server.Serve("/updates.json.sig", other.Sign(feed));
        await Assert.ThrowsAsync<UpdateFeedException>(() => Client().FetchFeedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_feed_changed_after_signing_is_refused()
    {
        var feed = FeedBytes();
        var sig = signer.Sign(feed);
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(feed).Replace("9.9.9", "9.9.8"));
        server.Serve("/updates.json", tampered);
        server.Serve("/updates.json.sig", sig);
        await Assert.ThrowsAsync<UpdateFeedException>(() => Client().FetchFeedAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 !!")]
    [InlineData("AAAA")]
    public void Malformed_signatures_never_verify(string sig) =>
        Assert.False(FeedSignature.Verify(FeedBytes(), Encoding.ASCII.GetBytes(sig), signer.PublicKey));

    [Fact]
    public void The_official_key_is_a_valid_P256_public_key()
    {
        using var k = ECDsa.Create();
        k.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdateSource.OfficialPublicKey), out _);
        Assert.Equal(256, k.KeySize);
    }

    public void Dispose()
    {
        server.Dispose();
        signer.Dispose();
    }
}

public sealed class UpdateTimeoutTests : IDisposable
{
    private readonly TcpListener raw = new(IPAddress.Loopback, 0);
    private readonly string dir = Directory.CreateTempSubdirectory("td-to-").FullName;
    private int Port => ((IPEndPoint)raw.LocalEndpoint).Port;

    public UpdateTimeoutTests() => raw.Start();

    /// <summary>Answers every request with headers promising `length` bytes, sends `sent`, then stalls.</summary>
    private void Stall(int length, int sent) => _ = Task.Run(async () =>
    {
        while (true)
        {
            TcpClient c;
            try { c = await raw.AcceptTcpClientAsync(); } catch { return; }
            var s = c.GetStream();
            var buf = new byte[4096];
            try { _ = await s.ReadAsync(buf); } catch { }
            var head = $"HTTP/1.1 200 OK\r\nContent-Length: {length}\r\nContent-Type: application/octet-stream\r\n\r\n";
            try
            {
                await s.WriteAsync(Encoding.ASCII.GetBytes(head));
                await s.WriteAsync(new byte[sent]);
                await Task.Delay(30000);
            }
            catch { }
            c.Dispose();
        }
    });

    private UpdateClient Client(TimeSpan idle) =>
        new(UpdateSource.ForTest(new Uri($"http://127.0.0.1:{Port}/updates.json"), "unused"), idleTimeout: idle, feedTimeout: idle);

    [Fact]
    public async Task A_stalled_download_times_out_and_leaves_no_file()
    {
        Stall(length: 5000, sent: 10);
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            Client(TimeSpan.FromMilliseconds(600)).DownloadAsync(new Uri($"http://127.0.0.1:{Port}/a.msi"), new string('0', 64), 5000,
                Path.Combine(dir, "x.msi"), null, CancellationToken.None));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task A_stalled_feed_times_out()
    {
        Stall(length: 5000, sent: 10);
        var sw = Stopwatch.StartNew();
        await Assert.ThrowsAsync<UpdateFeedException>(() => Client(TimeSpan.FromMilliseconds(600)).FetchFeedAsync(CancellationToken.None));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), sw.Elapsed.ToString());
    }

    public void Dispose()
    {
        raw.Stop();
        Directory.Delete(dir, true);
    }
}

public sealed class UpdateFolderTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("td-dir-").FullName;

    [Fact]
    public void Clearing_removes_only_the_updater_s_own_files()
    {
        var d = Path.Combine(root, "Updates");
        Directory.CreateDirectory(d);
        foreach (var n in new[] { "TouchDeck-update.msi", "TouchDeck-update.msi.part", "rp2040-169-update.uf2", "keep.txt", "notes.docx" })
            File.WriteAllText(Path.Combine(d, n), "x");
        UpdateService.ClearDownloads(d);
        Assert.Equal(["keep.txt", "notes.docx"], Directory.GetFiles(d).Select(Path.GetFileName).Order().ToList());
    }

    [Fact]
    public void A_junction_in_place_of_the_folder_is_refused()
    {
        var target = Path.Combine(root, "victim");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "TouchDeck-update.msi"), "precious");
        var link = Path.Combine(root, "Updates");
        var mk = Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/c", "mklink", "/J", link, target }, CreateNoWindow = true, UseShellExecute = false })!;
        mk.WaitForExit();
        Assert.True(Directory.Exists(link));
        Assert.Throws<UpdateVerificationException>(() => UpdateService.ClearDownloads(link));
        Assert.Equal("precious", File.ReadAllText(Path.Combine(target, "TouchDeck-update.msi")));
    }

    public void Dispose()
    {
        foreach (var d in Directory.GetDirectories(root))
            if (new DirectoryInfo(d).LinkTarget is not null) Directory.Delete(d);   // remove the junction itself, not its target
        Directory.Delete(root, true);
    }
}
