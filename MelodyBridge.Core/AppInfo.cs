namespace MelodyBridge.Core;

/// <summary>
/// The one place the app version lives. The Server layout reads it for the
/// sidebar, the About tab shows it, and the update check compares it to
/// GitHub releases.
/// </summary>
public static class AppInfo
{
    /// <summary>E.g. "1.4.2" - keep in sync with the release tag (without the v).</summary>
    public const string Version = "1.0.0";

    /// <summary>Releases feed the update check on the About tab compares against.</summary>
    public const string ReleasesFeed =
        "https://api.github.com/repos/warreth/melodybridge/releases/latest";
}
