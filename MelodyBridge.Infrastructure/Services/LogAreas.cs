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

    // One row per logger the app actually creates, ordered so the most
    // specific keyword wins (substring matching walks top to bottom).
    private static readonly (string Keyword, string Area)[] Keywords =
    {
        // Playlists: the store that fetches and downloads playlist tracks.
        ("PlaylistStore", "Playlists"),

        // Sources: playlist import providers (public and account).
        ("SpotifySourceProvider", "Sources"),
        ("YouTubeSourceProvider", "Sources"),
        ("SpotifyAccountProvider", "Sources"),
        ("YouTubeAccountProvider", "Sources"),
        ("AccountTokenStore", "Sources"),
        ("SourceProvider", "Sources"),

        // Downloads: the manager, coordinator, registry and every plugin.
        ("DownloadManager", "Downloads"),
        ("DownloadCoordinator", "Downloads"),
        ("DownloaderRegistry", "Downloads"),
        ("Lucida", "Downloads"),
        ("SoundCloud", "Downloads"),
        ("ArchiveOrg", "Downloads"),
        ("YtDlp", "Downloads"),
        ("Downloader", "Downloads"),
        ("BitrateProbe", "Downloads"),
        ("Spectrum", "Downloads"),

        // Library: scanning the disk and watching folders.
        ("LibraryScanner", "Library"),
        ("LibraryReconciler", "Library"),
        ("FileSystemMonitor", "Library"),
        ("FileSystemMonitoring", "Library"),
        ("ScanScheduling", "Library"),

        // Sync: jobs, the engine, the background services, outputs.
        ("SyncJobRunner", "Sync"),
        ("SyncEngine", "Sync"),
        ("SyncController", "Sync"),
        ("AutoSync", "Sync"),
        ("JellyfinSync", "Sync"),
        ("Jellyfin", "Sync"),
        ("M3uGenerator", "Sync"),

        // Database: EF Core internals and our context.
        ("MelodyBridgeDbContext", "Database"),
        ("DbContext", "Database"),
        ("Database", "Database"),
        ("Command", "Database"),
    };
}
