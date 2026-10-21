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
    /// Maps a raw category name to one of <see cref="All"/>. The exact
    /// names used by the Dev panel's own logger win first, then matching
    /// is substring-based on the short class name, so namespace renames
    /// do not break the mapping.
    /// </summary>
    public static string FromCategory(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return "System";

        if (Exact.TryGetValue(category, out var exact)) return exact;

        foreach (var (keyword, area) in Keywords)
        {
            if (category.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return area;
        }
        return "System";
    }

    // The Dev panel logs under short names ("Download", "Queue",
    // "Search") instead of a type name. Mapping them here is what keeps
    // those rows out of "System".
    private static readonly Dictionary<string, string> Exact =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Download"] = "Downloads",
            ["Queue"] = "Downloads",
            ["Search"] = "Sources",
            ["Sync"] = "Sync",
            ["Scanner"] = "Library",
            ["Database"] = "Database",
        };

    // One row per logger the app actually creates, ordered so the most
    // specific keyword wins (substring matching walks top to bottom).
    private static readonly (string Keyword, string Area)[] Keywords =
    {
        // The M3U writer sits in the Playlists namespace but belongs to
        // publishing, the page where its output is scheduled.
        ("M3u", "Sync"),

        // Playlists: the store, queries, transfers and the playlist pages.
        ("Playlist", "Playlists"),

        // Sources: playlist import providers (public and account), the
        // token stores and the ISRC lookup that feeds them.
        ("SourceProvider", "Sources"),
        ("Spotify", "Sources"),
        ("YouTube", "Sources"),
        ("AccountToken", "Sources"),
        ("Account", "Sources"),
        ("AuthCallback", "Sources"),
        ("IsrcResolver", "Sources"),
        ("ListenBrainz", "Sources"),

        // Downloads: the manager, coordinator, registry, every plugin,
        // the quality probes and the Cloudflare solver they share.
        ("Download", "Downloads"),
        ("Queue", "Downloads"),
        ("Downloader", "Downloads"),
        ("Qobuz", "Downloads"),
        ("Tidal", "Downloads"),
        ("Deezer", "Downloads"),
        ("Jumo", "Downloads"),
        ("Slskd", "Downloads"),
        ("Monochrome", "Downloads"),
        ("Lucida", "Downloads"),
        ("SoundCloud", "Downloads"),
        ("ArchiveOrg", "Downloads"),
        ("YtDlp", "Downloads"),
        ("Bitrate", "Downloads"),
        ("Spectrum", "Downloads"),
        ("FlareSolverr", "Downloads"),
        ("PythonRunner", "Downloads"),
        ("PluginAuth", "Downloads"),

        // Library: scanning the disk, watching folders, the metadata
        // passes and the file links a playlist folder holds.
        ("Library", "Library"),
        ("Scanner", "Library"),
        ("FileSystemMonitor", "Library"),
        ("FileSystemMonitoring", "Library"),
        ("ScanScheduling", "Library"),
        ("TrackLinker", "Library"),
        ("CoverArt", "Library"),
        ("Lyrics", "Library"),

        // Sync: jobs, the engine, the background services, outputs.
        ("SyncJob", "Sync"),
        ("SyncEngine", "Sync"),
        ("SyncController", "Sync"),
        ("AutoSync", "Sync"),
        ("Jellyfin", "Sync"),
        ("Plex", "Sync"),
        ("Subsonic", "Sync"),
        ("MediaServerStatus", "Sync"),
        ("M3u", "Sync"),

        // Database: EF Core internals, our context and backups.
        ("MelodyBridgeDbContext", "Database"),
        ("DbContext", "Database"),
        ("Database", "Database"),
        ("Command", "Database"),

        // System: startup, the Dev panel, toasts and notifications.
        ("Program", "System"),
        ("DevPanel", "System"),
        ("Toast", "System"),
        ("Notification", "System"),
        ("Ntfy", "System"),
        ("BackgroundFailure", "System"),
        ("UpdateCheck", "System"),
    };
}
