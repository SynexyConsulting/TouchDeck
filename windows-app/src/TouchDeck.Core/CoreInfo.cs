namespace TouchDeck.Core;

/// <summary>Build facts shared by the app and tests.</summary>
public static class CoreInfo
{
    /// <summary>The app/MSI version from Directory.Build.props.</summary>
    public static string Version =>
        typeof(CoreInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
