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

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(TestKey, throwOnMissingSubKey: false);
}
