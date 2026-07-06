namespace MelodyBridge.Core;

public interface ISourceProvider
{
    string Name { get; }
    Platform Platform { get; }
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
    Task<string?> DownloadWithQualityFallbackAsync(string sourceUrl, string outputDirectory, string melodyId, TrackQuality maxQuality, TrackQuality? minQuality = null, CancellationToken ct = default);
}

public interface IMusicSourceManager
{
    Task<MusicSource> AddSourceAsync(MusicSource source);
    Task RemoveSourceAsync(string sourceId);
    Task<IReadOnlyList<MusicSource>> GetAllSourcesAsync();
    Task<MusicSource?> GetSourceAsync(string sourceId);
    Task UpdateSourceAsync(MusicSource source);
    Task AutoSyncAllAsync(CancellationToken ct = default);
}

public record PlaylistOutputOptions(
    string OutputPath,
    bool UseRelativePaths,
    Dictionary<string, string>? PathRemap
);
