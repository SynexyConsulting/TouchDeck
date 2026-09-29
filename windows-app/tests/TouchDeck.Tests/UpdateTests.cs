using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using TouchDeck.Core.Session;
using TouchDeck.Core.Updates;

namespace TouchDeck.Tests;

/// <summary>A loopback HTTP server: path -> (status, body, headers).</summary>
internal sealed class LoopServer : IDisposable
{
    private readonly HttpListener listener = new();
    private readonly Dictionary<string, Func<HttpListenerResponse, byte[]>> routes = new();
    public int Port { get; }
    public string Base => $"http://localhost:{Port}";
    public List<string> Hits { get; } = [];

    public LoopServer()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        listener.Prefixes.Add($"http://localhost:{Port}/");
        listener.Start();
        _ = Task.Run(Loop);
    }

    public void Serve(string path, byte[] body, int status = 200) =>
        routes[path] = r => { r.StatusCode = status; return body; };

    public void Redirect(string path, string location, int status = 302) =>
        routes[path] = r => { r.StatusCode = status; r.RedirectLocation = location; return []; };

    private async Task Loop()
    {
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync(); } catch { return; }
            var path = ctx.Request.Url!.AbsolutePath;
            lock (Hits) Hits.Add(path);
            var body = routes.TryGetValue(path, out var f) ? f(ctx.Response) : [];
            if (!routes.ContainsKey(path)) ctx.Response.StatusCode = 404;
            try
            {
                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
            catch { }
        }
    }

    public void Dispose() { listener.Stop(); listener.Close(); }
}

public sealed class UpdateFeedTests
{
    private static readonly UpdateSource Official = UpdateSource.Official;
    private const string Sha = "3a6eb0790f39ac87c94f3856b2dd2c5d110e6811602261a9a923d3bb23adc8b7";

    private static string Feed(string appUrl = "https://github.com/SynexyConsulting/TouchDeckUpdates/releases/download/app-v1.2.0/TouchDeck-1.2.0.msi",
                               string sha = Sha, long size = 56000000, string fwBoard = "rp2040-169", long fwSize = 350000) => $$"""
        { "schema": 1, "published": "2026-09-29T08:00:00Z",
          "app": { "windows": { "version": "1.2.0", "url": "{{appUrl}}", "sha256": "{{sha}}", "size": {{size}} }, "future": {} },
          "firmware": [ { "board": "{{fwBoard}}", "version": "1.6.0",
                          "url": "https://github.com/SynexyConsulting/TouchDeckUpdates/releases/download/app-v1.2.0/rp2040-169-1.6.0.uf2",
                          "sha256": "{{Sha}}", "size": {{fwSize}} } ],
          "unknown": 1 }
        """;

    [Fact]
    public void Parses_a_valid_feed_and_ignores_unknown_keys()
    {
        var f = UpdateFeed.Parse(Feed(), Official);
        Assert.Equal("1.2.0", f.WindowsApp!.Version.ToString(3));
        Assert.Equal(56000000, f.WindowsApp.Size);
        Assert.Equal("rp2040-169", Assert.Single(f.Firmware).Board);
    }

    [Theory]
    [InlineData("http://github.com/SynexyConsulting/TouchDeckUpdates/releases/download/x/a.msi")]      // not https
    [InlineData("https://evil.example.com/TouchDeck.msi")]                                              // other host
    [InlineData("https://github.com/SomeoneElse/TouchDeckUpdates/releases/download/x/a.msi")]           // other repo
    [InlineData("https://github.com/SynexyConsulting/TouchDeckUpdates/../../evil/releases/download/a")] // path games
    [InlineData("file:///C:/Windows/notepad.exe")]
    public void Refuses_asset_urls_outside_the_update_repo(string url) =>
        Assert.Throws<UpdateFeedException>(() => UpdateFeed.Parse(Feed(appUrl: url), Official));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zz6eb0790f39ac87c94f3856b2dd2c5d110e6811602261a9a923d3bb23adc8b7")]
    public void Requires_a_sha256(string sha) =>
        Assert.Throws<UpdateFeedException>(() => UpdateFeed.Parse(Feed(sha: sha), Official));

    [Theory]
    [InlineData(0L)]
    [InlineData(-5L)]
    [InlineData(300_000_000L)]
    public void App_size_must_be_sane(long size) =>
        Assert.Throws<UpdateFeedException>(() => UpdateFeed.Parse(Feed(size: size), Official));

    [Fact]
    public void Firmware_size_is_capped_at_4_MB() =>
        Assert.Throws<UpdateFeedException>(() => UpdateFeed.Parse(Feed(fwSize: 5_000_000), Official));

    [Theory]
    [InlineData("not json")]
    [InlineData("""{ "schema": 2, "app": {}, "firmware": [] }""")]
    [InlineData("""{ "schema": 1, "app": { "windows": { "version": "x.y" } }, "firmware": [] }""")]
    public void Malformed_or_future_feeds_are_refused(string json) =>
        Assert.Throws<UpdateFeedException>(() => UpdateFeed.Parse(json, Official));

    [Fact]
    public void Test_feeds_must_be_loopback()
    {
        Assert.Throws<ArgumentException>(() => UpdateSource.ForTest(new Uri("http://example.com/updates.json")));
        var t = UpdateSource.ForTest(new Uri("http://localhost:5000/updates.json"));
        Assert.True(t.IsAllowedAsset(new Uri("http://localhost:5000/a.msi")));
        Assert.False(t.IsAllowedAsset(new Uri("http://localhost:5001/a.msi")));      // other port
        Assert.False(Official.IsAllowedAsset(new Uri("http://localhost:5000/a.msi")));
    }

    [Theory]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1", true)]
    [InlineData("https://github.com/x", true)]
    [InlineData("http://objects.githubusercontent.com/x", false)]
    [InlineData("https://githubusercontent.com.evil.io/x", false)]
    [InlineData("https://evil.io/github.com", false)]
    public void Redirects_only_to_github_https(string url, bool ok) =>
        Assert.Equal(ok, Official.IsAllowedRedirect(new Uri(url)));
}

public sealed class UpdateSelectionTests
{
    private static UpdateFeed F(string app, params (string board, string ver)[] fw) => new(
        new UpdatePackage(Version.Parse(app), new Uri("https://x/a.msi"), new string('a', 64), 10),
        fw.Select(f => new FirmwarePackage(f.board, Version.Parse(f.ver), new Uri("https://x/f.uf2"), new string('b', 64), 10)).ToList());

    [Fact]
    public void Offers_only_strictly_newer_app()
    {
        Assert.NotNull(UpdateSelector.Select(F("1.2.1"), new Version(1, 2, 0), null).App);
        Assert.Null(UpdateSelector.Select(F("1.2.0"), new Version(1, 2, 0), null).App);
        Assert.Null(UpdateSelector.Select(F("1.1.9"), new Version(1, 2, 0), null).App);   // never a downgrade
    }

    [Fact]
    public void Offers_firmware_only_for_the_connected_board_and_only_newer()
    {
        var feed = F("1.0.0", ("rp2040-169", "1.7.0"), ("esp32c3-128", "1.9.0"));
        var rp = new FirmwareInfo("rp2040-169", "1.6.0", "");
        Assert.Equal("rp2040-169", UpdateSelector.Select(feed, new Version(1, 2, 0), rp).Firmware!.Board);
        Assert.Null(UpdateSelector.Select(feed, new Version(1, 2, 0), new FirmwareInfo("rp2040-169", "1.7.0", "")).Firmware);
        Assert.Null(UpdateSelector.Select(feed, new Version(1, 2, 0), new FirmwareInfo("other-board", "0.1.0", "")).Firmware);
        Assert.Null(UpdateSelector.Select(feed, new Version(1, 2, 0), null).Firmware);    // nothing connected
    }
}

public sealed class UpdateClientTests : IDisposable
{
    private readonly LoopServer server = new();
    private readonly string dir = Directory.CreateTempSubdirectory("td-upd-").FullName;
    private static readonly byte[] Payload = Encoding.ASCII.GetBytes(new string('M', 5000));
    private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private UpdateClient Client() => new(UpdateSource.ForTest(new Uri($"{server.Base}/updates.json")));

    [Fact]
    public async Task Missing_feed_means_nothing_published()
    {
        Assert.Null(await Client().FetchFeedAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Fetches_and_validates_the_feed()
    {
        server.Serve("/updates.json", Encoding.UTF8.GetBytes($$"""
            { "schema": 1, "app": { "windows": { "version": "9.9.9", "url": "{{server.Base}}/a.msi", "sha256": "{{Hex(Payload)}}", "size": {{Payload.Length}} } }, "firmware": [] }
            """));
        var feed = await Client().FetchFeedAsync(CancellationToken.None);
        Assert.Equal(new Version(9, 9, 9), feed!.WindowsApp!.Version);
    }

    [Fact]
    public async Task Downloads_and_verifies()
    {
        server.Serve("/a.msi", Payload);
        var dest = Path.Combine(dir, "x.msi");
        await Client().DownloadAsync(new Uri($"{server.Base}/a.msi"), Hex(Payload), Payload.Length, dest, null, CancellationToken.None);
        Assert.Equal(Payload, File.ReadAllBytes(dest));
    }

    [Fact]
    public async Task Wrong_hash_leaves_no_file()
    {
        server.Serve("/a.msi", Payload);
        var dest = Path.Combine(dir, "x.msi");
        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            Client().DownloadAsync(new Uri($"{server.Base}/a.msi"), new string('0', 64), Payload.Length, dest, null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task Longer_than_declared_is_refused_and_leaves_no_file()
    {
        server.Serve("/a.msi", Payload);
        var dest = Path.Combine(dir, "x.msi");
        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            Client().DownloadAsync(new Uri($"{server.Base}/a.msi"), Hex(Payload), Payload.Length - 1, dest, null, CancellationToken.None));
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task Redirect_to_a_foreign_host_is_refused_before_fetching()
    {
        server.Redirect("/a.msi", "http://example.com/evil.msi");
        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            Client().DownloadAsync(new Uri($"{server.Base}/a.msi"), Hex(Payload), Payload.Length, Path.Combine(dir, "x.msi"), null, CancellationToken.None));
    }

    [Fact]
    public async Task Same_host_redirect_is_followed()
    {
        server.Redirect("/a.msi", $"{server.Base}/b.msi");
        server.Serve("/b.msi", Payload);
        var dest = Path.Combine(dir, "x.msi");
        await Client().DownloadAsync(new Uri($"{server.Base}/a.msi"), Hex(Payload), Payload.Length, dest, null, CancellationToken.None);
        Assert.True(File.Exists(dest));
    }

    [Fact]
    public async Task Asset_url_outside_the_source_is_refused_without_a_request()
    {
        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            Client().DownloadAsync(new Uri("https://example.com/a.msi"), Hex(Payload), Payload.Length, Path.Combine(dir, "x.msi"), null, CancellationToken.None));
    }

    [Fact]
    public async Task Server_errors_become_UpdateFeedException()
    {
        server.Serve("/updates.json", Encoding.UTF8.GetBytes("oops"), status: 500);
        await Assert.ThrowsAsync<UpdateFeedException>(() => Client().FetchFeedAsync(CancellationToken.None));
    }

    public void Dispose()
    {
        server.Dispose();
        Directory.Delete(dir, true);
    }
}

public sealed class UpdateServiceTests : IDisposable
{
    private readonly LoopServer server = new();
    private readonly string dir = Directory.CreateTempSubdirectory("td-svc-").FullName;
    private static readonly byte[] Msi = Encoding.ASCII.GetBytes(new string('I', 3000));
    private static readonly byte[] Uf2 = Encoding.ASCII.GetBytes(new string('U', 2048));
    private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    private UpdateService Service() =>
        new(new UpdateClient(UpdateSource.ForTest(new Uri($"{server.Base}/updates.json"))), dir);

    private void Publish(string app = "1.3.0", string fw = "1.7.0")
    {
        server.Serve("/app.msi", Msi);
        server.Serve("/fw.uf2", Uf2);
        server.Serve("/updates.json", Encoding.UTF8.GetBytes($$"""
            { "schema": 1,
              "app": { "windows": { "version": "{{app}}", "url": "{{server.Base}}/app.msi", "sha256": "{{Hex(Msi)}}", "size": {{Msi.Length}} } },
              "firmware": [ { "board": "rp2040-169", "version": "{{fw}}", "url": "{{server.Base}}/fw.uf2", "sha256": "{{Hex(Uf2)}}", "size": {{Uf2.Length}} } ] }
            """));
    }

    [Fact]
    public async Task Nothing_published_is_reported_as_such()
    {
        var r = await Service().CheckAsync(new Version(1, 2, 0), null, CancellationToken.None);
        Assert.True(r.NothingPublished);
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task Reports_app_and_firmware_separately()
    {
        Publish();
        var r = await Service().CheckAsync(new Version(1, 2, 0), new FirmwareInfo("rp2040-169", "1.6.0", ""), CancellationToken.None);
        Assert.Equal(new Version(1, 3, 0), r.Choice!.App!.Version);
        Assert.Equal(new Version(1, 7, 0), r.Choice.Firmware!.Version);
    }

    [Fact]
    public async Task Failures_are_text_not_exceptions()
    {
        server.Serve("/updates.json", Encoding.UTF8.GetBytes("{ broken"));
        var r = await Service().CheckAsync(new Version(1, 2, 0), null, CancellationToken.None);
        Assert.NotNull(r.Error);
        Assert.Null(r.Choice);
    }

    [Fact]
    public async Task Downloads_use_fixed_names_in_a_cleared_folder()
    {
        Publish();
        File.WriteAllText(Path.Combine(dir, "leftover.msi"), "old");
        var s = Service();
        var r = await s.CheckAsync(new Version(1, 2, 0), new FirmwareInfo("rp2040-169", "1.6.0", ""), CancellationToken.None);
        var msi = await s.DownloadAppAsync(r.Choice!.App!, null, CancellationToken.None);
        Assert.Equal(Path.Combine(dir, "TouchDeck-update.msi"), msi);
        Assert.False(File.Exists(Path.Combine(dir, "leftover.msi")));
        var uf2 = await s.DownloadFirmwareAsync(r.Choice.Firmware!, null, CancellationToken.None);
        Assert.Equal(Path.Combine(dir, "rp2040-169-update.uf2"), uf2);
        Assert.Equal(Uf2, File.ReadAllBytes(uf2));
    }

    public void Dispose()
    {
        server.Dispose();
        Directory.Delete(dir, true);
    }
}

public class InstallerLaunchTests
{
    [Fact]
    public void Launcher_passes_paths_by_environment_never_in_the_command()
    {
        const string msi = @"C:\Users\A & B %PATH%\AppData\Local\TouchDeck\Updates\TouchDeck-update.msi";
        const string exe = @"C:\Users\A & B %PATH%\AppData\Local\Programs\Touch Deck\TouchDeck.exe";
        var psi = InstallerLaunch.Create(msi, exe);
        Assert.EndsWith(@"WindowsPowerShell\v1.0\powershell.exe", psi.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(msi, psi.Environment["TD_UPDATE_MSI"]);
        Assert.Equal(exe, psi.Environment["TD_UPDATE_EXE"]);
        var args = string.Join(' ', psi.ArgumentList);
        Assert.DoesNotContain("A & B", args);
        Assert.DoesNotContain("%PATH%", args);
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(psi.ArgumentList[psi.ArgumentList.IndexOf("-EncodedCommand") + 1]));
        Assert.Contains("msiexec.exe", script);
        Assert.Contains("/passive", script);
        Assert.Contains("$env:TD_UPDATE_MSI", script);
        Assert.Contains("$env:TD_UPDATE_EXE", script);
    }
}
