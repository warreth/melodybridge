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
/// What one candidate's audio really is, read before any bytes are
/// downloaded: the source stream's codec, bitrate, sample rate and bit
/// depth where the plugin can know them. A null fact means the plugin
/// cannot tell; the UI must show that instead of guessing. Lossless is
/// only true when the container uniquely is (FLAC, ALAC, PCM).
/// </summary>
public record CandidateAudioQuality(
    string? Codec,
    int? BitrateKbps,
    int? SampleRateHz,
    int? BitDepth,
    bool Lossless,
    string? Note = null);

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

/// <summary>One choice of a select-style plugin setting.</summary>
public record PluginConfigChoice(string Value, string Label);

/// <summary>
/// One editable setting a plugin wants on its config panel (Plugins page).
/// Key is the storage key; Value is loaded/saved through the registry.
/// <paramref name="Advanced"/> fields hide behind a small toggle so the
/// panel shows the essentials first; <paramref name="Secret"/> fields
/// render as password inputs; <paramref name="Options"/> turns the field
/// into a select.
/// </summary>
public record PluginConfigField(
    string Key,
    string Label,
    string? Placeholder = null,
    string? Description = null,
    bool Advanced = false,
    bool Secret = false,
    IReadOnlyList<PluginConfigChoice>? Options = null);

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
    /// Why the last availability check said no, when the plugin can tell
    /// (a missing token, a rejected key, an unreachable service). The
    /// Plugins page shows this instead of the generic hint. Null when the
    /// plugin is ready or cannot explain itself.
    /// </summary>
    string? LastUnavailableReason => null;

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
    /// Every candidate the plugin found for the query, best first, before
    /// its own ranking gate: the "choose a source by hand" dialog shows
    /// them so a human can pick a result the automatic match refused. The
    /// default returns the ranked hit plus its alternatives; plugins that
    /// can offer more (the yt-dlp backed ones) override it.
    /// </summary>
    async Task<IReadOnlyList<DownloaderSearchHit>> SearchAllAsync(
        TrackQuery query, CancellationToken ct = default)
    {
        var hit = await SearchAsync(query, ct);
        if (hit is null) return Array.Empty<DownloaderSearchHit>();
        var all = new List<DownloaderSearchHit>(1 + (hit.Alternatives?.Count ?? 0)) { hit };
        if (hit.Alternatives is { Count: > 0 } alternatives) all.AddRange(alternatives);
        return all;
    }

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

    /// <summary>
    /// Reads the exact audio facts of one search hit without downloading
    /// it: the yt-dlp-backed plugins simulate the extraction and parse the
    /// selected format, Qobuz reports its configured tier. Null when this
    /// plugin cannot know: the picker then shows only what the manifest
    /// promises, never a guess. Best effort by contract; never throws.
    /// </summary>
    Task<CandidateAudioQuality?> ProbeQualityAsync(
        string sourceUrl, CancellationToken ct = default)
        => Task.FromResult<CandidateAudioQuality?>(null);
}
