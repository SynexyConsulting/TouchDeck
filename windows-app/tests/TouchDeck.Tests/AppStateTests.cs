using Microsoft.Win32;
using TouchDeck.Core.App;

namespace TouchDeck.Tests;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "touchdeck-test-" + Guid.NewGuid().ToString("N"));
    private string File_ => Path.Combine(dir, "settings.json");

    [Fact]
    public void Round_trips()
    {
        var s = new AppSettings { DryRun = true, Diagnostics = true, StartWithWindows = true, PreferredPort = "COM6" };
        s.Save(File_);
        Assert.Equal(s, AppSettings.Load(File_));
        Assert.False(File.Exists(File_ + ".tmp"));
    }

    [Fact]
    public void Update_and_startup_options_round_trip_with_sane_defaults()
    {
        var d = new AppSettings();
        Assert.True(d.CheckForUpdates);
        Assert.True(d.StartMinimized);
        Assert.Null(d.LastUpdateCheck);
        var s = d with { CheckForUpdates = false, StartMinimized = false, LastUpdateCheck = new DateTime(2026, 9, 29, 8, 0, 0, DateTimeKind.Utc) };
        s.Save(File_);
        Assert.Equal(s, AppSettings.Load(File_));
    }

    [Fact]
    public void A_1_1_settings_file_loads_with_the_new_defaults()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(File_, """{ "DryRun": true, "Diagnostics": false, "StartWithWindows": true, "PreferredPort": "COM6" }""");
        var s = AppSettings.Load(File_);
        Assert.True(s.DryRun);
        Assert.True(s.CheckForUpdates);
        Assert.True(s.StartMinimized);
    }

    [Fact]
    public void Missing_file_gives_defaults() => Assert.Equal(new AppSettings(), AppSettings.Load(File_));

    [Fact]
    public void Corrupt_file_gives_defaults()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(File_, "{ not json");
        Assert.Equal(new AppSettings(), AppSettings.Load(File_));
    }

    public void Dispose()
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}

public class ClipHistoryTests
{
    [Fact]
    public void Newest_first_repeats_move_up_capacity_holds()
    {
        var h = new ClipHistory(capacity: 3);
        foreach (var t in new[] { "a", "b", "c", "a", "d", "" }) h.Add(t);
        Assert.Equal(["d", "a", "c"], h.Items);
    }

    [Fact]
    public void Changes_are_announced()
    {
        var h = new ClipHistory();
        int n = 0;
        h.Changed += () => n++;
        h.Add("x");
        h.Clear();
        Assert.Equal(2, n);
    }
}

public sealed class AutostartTests : IDisposable
{
    private const string TestKey = @"Software\TouchDeckTests";
    private readonly Autostart autostart = new(TestKey);

    [Fact]
    public void Enables_and_disables_under_the_given_key()
    {
        const string exe = @"C:\Users\x\AppData\Local\Programs\TouchDeck\TouchDeck.exe";
        autostart.Set(true, exe);
        Assert.True(autostart.IsEnabled(exe));
        using (var key = Registry.CurrentUser.OpenSubKey(TestKey))
            Assert.Equal($"\"{exe}\" --minimized", key!.GetValue("TouchDeck"));
        Assert.False(autostart.IsEnabled(@"C:\elsewhere\TouchDeck.exe"));  // moved install: stale entry
        autostart.Set(false, exe);
        autostart.Set(false, exe);                                          // idempotent
        Assert.False(autostart.IsEnabled(exe));
    }

    [Fact]
    public void Start_minimized_is_part_of_the_command()
    {
        const string exe = @"C:	d\TouchDeck.exe";
        autostart.Set(true, exe, minimized: false);
        using (var key = Registry.CurrentUser.OpenSubKey(TestKey))
            Assert.Equal($"\"{exe}\"", key!.GetValue("TouchDeck"));
        Assert.True(autostart.IsEnabled(exe));
        Assert.False(autostart.IsMinimized(exe));
        autostart.Set(true, exe, minimized: true);
        Assert.True(autostart.IsMinimized(exe));
    }

    [Fact]
    public void Remove_drops_an_entry_for_any_path()
    {
        autostart.Set(true, @"C:\old\TouchDeck.exe");
        autostart.Remove();
        autostart.Remove();
        using var key = Registry.CurrentUser.OpenSubKey(TestKey);
        Assert.Null(key?.GetValue("TouchDeck"));
    }

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(TestKey, throwOnMissingSubKey: false);
}
