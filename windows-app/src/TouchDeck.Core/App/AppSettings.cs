using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace TouchDeck.Core.App;

/// <summary>User preferences, stored as JSON in %APPDATA%\TouchDeck\settings.json.</summary>
public sealed record AppSettings
{
    /// <summary>Log PC-mode keys/mouse instead of performing them.</summary>
    public bool DryRun { get; init; }
    /// <summary>Poll DBG every 2 s and show the board's diagnostics.</summary>
    public bool Diagnostics { get; init; }
    public bool StartWithWindows { get; init; }
    /// <summary>Port of the last board used, preferred when several are plugged in.</summary>
    public string? PreferredPort { get; init; }
    /// <summary>With Start with Windows: start hidden in the tray (the Run entry gets --minimized).</summary>
    public bool StartMinimized { get; init; } = true;
    /// <summary>Check the public update feed at start and daily.</summary>
    public bool CheckForUpdates { get; init; } = true;
    /// <summary>When the feed was last checked (UTC).</summary>
    public DateTime? LastUpdateCheck { get; init; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TouchDeck", "settings.json");

    /// <summary>Missing or unreadable file gives defaults: settings must never stop the app starting.</summary>
    public static AppSettings Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    /// <summary>Writes to a temp file first so a crash mid-write can't leave half a file.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>Recently sent clips, newest first. RAM only, like the board's clip: nothing is written to disk.</summary>
public sealed class ClipHistory(int capacity = 10)
{
    private readonly List<string> items = [];

    public IReadOnlyList<string> Items => items;
    public event Action? Changed;

    public void Add(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        items.Remove(text);                        // a repeat moves to the top
        items.Insert(0, text);
        if (items.Count > capacity) items.RemoveRange(capacity, items.Count - capacity);
        Changed?.Invoke();
    }

    public void Clear()
    {
        items.Clear();
        Changed?.Invoke();
    }
}

/// <summary>"Start with Windows": a value under HKCU\...\Run (no admin rights needed).</summary>
public sealed class Autostart(string keyPath = Autostart.RunKey, string valueName = "TouchDeck")
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static string CommandFor(string exePath, bool minimized = true) =>
        minimized ? $"\"{exePath}\" --minimized" : $"\"{exePath}\"";

    private string? Current()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath);
        return key?.GetValue(valueName) as string;
    }

    /// <summary>On, for this exe (either form of the command).</summary>
    public bool IsEnabled(string exePath) => Current() is { } c && (c == CommandFor(exePath, true) || c == CommandFor(exePath, false));

    public bool IsMinimized(string exePath) => Current() == CommandFor(exePath, true);

    /// <summary>Uninstall: drop the entry whatever path it points at.</summary>
    public void Remove()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public void Set(bool enabled, string exePath, bool minimized = true)
    {
        using var key = Registry.CurrentUser.CreateSubKey(keyPath);
        if (enabled) key.SetValue(valueName, CommandFor(exePath, minimized));
        else key.DeleteValue(valueName, throwOnMissingValue: false);
    }
}
