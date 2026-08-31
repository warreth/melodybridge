namespace MelodyBridge.Core;

/// <summary>
/// Result of a track search performed by a downloader plugin.
/// </summary>
public record DownloaderSearchHit(
    string Title,
    string? Artist,
    string? SourceUrl,
    TimeSpan? Duration
);

/// <summary>
/// Result of a download attempt.
/// </summary>
public record DownloaderDownloadResult(
    bool Success,
    string? FilePath,
    string? ErrorMessage
);

/// <summary>
/// Progress of one track's download, as reported to the UI.
/// </summary>
public record DownloadProgress(
    string MelodyId,
    string Title,
    string Status, // searching | downloading | done | failed
    string? Plugin,
    string? FilePath);

/// <summary>
/// One download plugin. Implementations search for a track by metadata
/// (artist/title), download it to a directory, and return the local path.
/// The DownloadManager runs plugins in priority order (the waterfall).
/// </summary>
public interface IDownloader
{
    /// <summary>Unique identifier (stable across restarts, stored in DB).</summary>
    string Id { get; }

    /// <summary>Human-readable name shown in the UI.</summary>
    string Name { get; }

    /// <summary>Short description of what/where this plugin downloads from.</summary>
    string Description => string.Empty;

    /// <summary>True when the plugin is operational (binary reachable, service up).</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>Find a downloadable source URL for the track metadata.</summary>
    Task<DownloaderSearchHit?> SearchAsync(string artist, string title, CancellationToken ct = default);

    /// <summary>Download the track at sourceUrl into outputDirectory.</summary>
    Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl,
        string outputDirectory,
        string? melodyId,
        CancellationToken ct = default);
}
