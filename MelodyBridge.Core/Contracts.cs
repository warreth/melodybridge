namespace MelodyBridge.Core;

public interface ISourceProvider
{
    string Name { get; }
    Platform Platform { get; }
    /// <summary>True when this provider can parse and fetch the given identifier/URL.</summary>
    bool CanHandle(string sourceIdentifier);
    Task<Playlist> GetPlaylistAsync(string sourceIdentifier);
    Task<string?> ResolveTrackUrlAsync(string query);
}

public interface ILibraryScanner
{
    Task ScanAsync(IEnumerable<ScanLocation> paths, CancellationToken ct = default);
}

public interface IPlaylistComposer
{
    Task ComposeAsync(Playlist playlist, IEnumerable<ScanLocation> searchLocations, PlaylistOutputOptions options, CancellationToken ct = default);
}

public interface IMediaServerSync
{
    string Name { get; }
    Task SyncPlaylistAsync(Playlist playlist, PlaylistOutputOptions options, CancellationToken ct = default);
    /// <summary>Report of the last SyncPlaylistAsync call; null before the first one.</summary>
    MediaServerSyncReport? LastReport { get; }
}

public interface ISyncJobRunner
{
    Task<SyncJobRunLog> RunJobAsync(SyncJob job, CancellationToken ct = default);
}

/// <summary>
/// Reads users and reachability of an arbitrary media server (Jellyfin,
/// Plex, Navidrome). The connection values travel per call, so nothing
/// mutable is shared with the sync clients.
/// </summary>
public interface IMediaServerDirectory
{
    /// <summary>Server kind this directory speaks ("Jellyfin", "Plex", "Navidrome").</summary>
    string Kind { get; }
    /// <summary>All users the given server reports; empty when the server has no user list.</summary>
    Task<List<MediaServerUserOption>> GetUsersAsync(MediaServerConnection connection, CancellationToken ct = default);
    /// <summary>True when the server answers an authenticated lightweight request.</summary>
    Task<bool> TestConnectionAsync(MediaServerConnection connection, CancellationToken ct = default);
}

/// <summary>One user row of a media server, as the picker shows it.</summary>
public record MediaServerUserOption(string Id, string? Name);

public interface IDownloadManager
{
    Task<string?> DownloadAsync(string sourceUrl, string outputDirectory, string melodyId, CancellationToken ct = default);
    /// <summary>
    /// Search for the track by metadata through the plugin waterfall,
    /// download it into outputDirectory, tag the MELODY_ID, return the path.
    /// </summary>
    Task<string?> DownloadTrackAsync(string artist, string title, string outputDirectory, string melodyId, DownloadQuality? quality = null, CancellationToken ct = default);
    /// <summary>Current in-flight download states (for UI polling).</summary>
    IReadOnlyList<DownloadProgress> SnapshotProgress();
}

/// <summary>
/// Per-call media-server connection override (Jellyfin, Plex, Navidrome).
/// When set, these values win over the global settings (the sync-job
/// wizard stores them per job).
/// </summary>
/// <summary>UserId carries the Jellyfin user id or the Navidrome username;
/// null for servers that need none (Plex) or the server default.</summary>
public record MediaServerConnection(string BaseUrl, string ApiKey, string? UserId = null);

public record PlaylistOutputOptions(
    string OutputPath,
    bool UseRelativePaths,
    Dictionary<string, string>? PathRemap,
    MediaServerConnection? MediaServerConnection = null
);
