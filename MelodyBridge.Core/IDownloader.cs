namespace MelodyBridge.Core;

/// <summary>
/// Result of a track search performed by a downloader plugin.
/// <paramref name="Alternatives"/> carries the runner-up candidates in
/// rank order: when the first choice turns out to be undownloadable
/// (deleted video, geo-block, dead mirror), the manager can fall back to
/// the next candidate from the same plugin instead of abandoning the
/// plugin entirely.
/// </summary>
public record DownloaderSearchHit(
    string Title,
    string? Artist,
    string? SourceUrl,
    TimeSpan? Duration,
    int? BitrateKbps = null,
    MatchConfidence MatchConfidence = MatchConfidence.Low,
    IReadOnlyList<DownloaderSearchHit>? Alternatives = null);

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
/// One editable setting a plugin wants on its config panel (Plugins page).
/// Key is the storage key; Value is loaded/saved through the registry.
/// </summary>
public record PluginConfigField(
    string Key,
    string Label,
    string? Placeholder = null,
    string? Description = null);

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

    /// <summary>
    /// Settings this plugin exposes on its config panel. Empty by default:
    /// plugins without knobs render no panel.
    /// </summary>
    IReadOnlyList<PluginConfigField> ConfigFields => Array.Empty<PluginConfigField>();

    /// <summary>
    /// What this plugin can honestly produce (formats + bitrate band).
    /// Required with no default: the quality router cannot route on
    /// unstated capabilities.
    /// </summary>
    PluginCapabilities Capabilities { get; }

    /// <summary>True when the plugin is operational (binary reachable, service up).</summary>
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    /// Find a downloadable source URL for the track query, honoring the
    /// requested quality where the source exposes it. Plugins rank their
    /// candidates with <c>SearchRanker</c>: title/artist similarity plus
    /// duration proximity, with gross mismatches and unrequested
    /// modifiers (karaoke, cover, live cut) dropped. Plugins whose
    /// catalog indexes ISRCs search by <c>query.Isrc</c> first when it is
    /// present, and fall back to the text query when that yields nothing.
    /// </summary>
    Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default);

    /// <summary>
    /// Download the track at sourceUrl into outputDirectory, honoring the
    /// requested quality (format/bitrate cap) where the source allows it.
    /// </summary>
    Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl,
        string outputDirectory,
        string? melodyId,
        DownloadQuality? quality = null,
        CancellationToken ct = default);
}
