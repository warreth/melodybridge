using System.Reflection;

namespace MelodyBridge.Core;

/// <summary>
/// The one place the app version lives. The release workflow stamps the
/// assembly informational version from the release tag; source runs and
/// unstamped builds fall back to <see cref="FallbackVersion"/>. The Server
/// layout reads it for the sidebar, the About tab shows it, and the update
/// check compares it to GitHub releases.
/// </summary>
public static class AppInfo
{
    /// <summary>Used when no assembly informational version is stamped.</summary>
    public const string FallbackVersion = "1.0.0";

    /// <summary>
    /// The running version, e.g. "1.4.2": never with a "v" prefix and
    /// never with SemVer build metadata (the part after "+").
    /// </summary>
    public static string Version { get; } = ReadVersion();

    private static string ReadVersion()
    {
        var informational = typeof(AppInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational)) return FallbackVersion;
        var version = informational.Split('+')[0].Trim();
        return version.Length == 0 ? FallbackVersion : version;
    }

    /// <summary>Releases feed the update check on the About tab compares against.</summary>
    public const string ReleasesFeed =
        "https://api.github.com/repos/warreth/melodybridge/releases/latest";

    /// <summary>Newest releases, newest first: the fallback while no stable release exists.</summary>
    public const string ReleasesListFeed =
        "https://api.github.com/repos/warreth/melodybridge/releases?per_page=20";

    /// <summary>The public release page, for the offline what's-new fallback.</summary>
    public const string ReleasesPage =
        "https://github.com/warreth/melodybridge/releases";
}
