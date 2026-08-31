namespace MelodyBridge.Core;

/// <summary>
/// Result of a track search performed by a downloader plugin.
/// </summary>
public record DownloaderSearchHit(
    string Title,
    string? Artist,
    string? SourceUrl,
    TimeSpan? Duration,
    int? BitrateKbps = null,
    MatchConfidence MatchConfidence = MatchConfidence.Low);

/// <summary>
/// How sure a plugin is that its search result is the requested track.
/// Low confidence is downloaded anyway but shown as a warning next to the
/// track, so the user can re-check instead of silently getting a wrong song.
/// </summary>
public enum MatchConfidence
{
    High,
    Low,
}

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
    string? FilePath,
    MatchConfidence MatchConfidence = MatchConfidence.High,
    string? Warning = null);

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

    /// <summary>
    /// Find a downloadable source URL for the track metadata, honoring the
    /// requested quality where the source exposes it.
    /// </summary>
    Task<DownloaderSearchHit?> SearchAsync(string artist, string title, DownloadQuality quality, CancellationToken ct = default);

    /// <summary>Download the track at sourceUrl into outputDirectory.</summary>
    Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl,
        string outputDirectory,
        string? melodyId,
        CancellationToken ct = default);
}
