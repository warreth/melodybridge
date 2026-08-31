using MelodyBridge.Core.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Maps raw ILogger category names (full type names like
/// "MelodyBridge.Infrastructure.Services.PlaylistStore") to friendly,
/// stable areas the Logs page can filter on.
/// </summary>
public static class LogAreas
{
    /// <summary>Display order for the Logs page filter chips.</summary>
    public static readonly string[] All =
    {
        "Playlists", "Downloads", "Sources", "Library", "Sync", "Database", "System",
    };

    /// <summary>
    /// Maps a raw category name to one of <see cref="All"/>. Matching is
    /// substring-based on the short class name, so namespace renames do
    /// not break the mapping.
    /// </summary>
    public static string FromCategory(string category)
    {
        foreach (var (keyword, area) in Keywords)
        {
            if (category.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return area;
        }
        return "System";
    }

    private static readonly (string Keyword, string Area)[] Keywords =
    {
        ("PlaylistStore", "Playlists"),
        ("SpotifySourceProvider", "Sources"),
        ("YouTubeSourceProvider", "Sources"),
        ("SourceProvider", "Sources"),
        ("DownloadManager", "Downloads"),
        ("Downloader", "Downloads"),
        ("SoundCloud", "Downloads"),
        ("ArchiveOrg", "Downloads"),
        ("YtDlp", "Downloads"),
        ("LibraryScanner", "Library"),
        ("FileSystemMonitor", "Library"),
        ("FileSystemMonitoring", "Library"),
        ("ScanScheduling", "Library"),
        ("SyncJobRunner", "Sync"),
        ("SyncEngine", "Sync"),
        ("AutoSync", "Sync"),
        ("Jellyfin", "Sync"),
        ("DbContext", "Database"),
        ("Database", "Database"),
        ("MelodyBridgeDbContext", "Database"),
        ("Command", "Database"),
    };
}
