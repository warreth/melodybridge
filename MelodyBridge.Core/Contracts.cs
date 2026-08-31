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
}

public interface ISyncJobRunner
{
    Task<SyncJobRunLog> RunJobAsync(SyncJob job, CancellationToken ct = default);
}

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

public record PlaylistOutputOptions(
    string OutputPath,
    bool UseRelativePaths,
    Dictionary<string, string>? PathRemap
);
