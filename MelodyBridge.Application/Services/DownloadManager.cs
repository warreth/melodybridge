using System.Collections.Concurrent;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Application.Services;

/// <summary>
/// Coordinates downloader plugins (the waterfall): given track metadata,
/// search and download through enabled plugins in priority order.
///
/// Every plugin call is individually guarded: a plugin that throws on
/// availability, search or download never stops the waterfall, it just
/// stops being the answer for this track. Search hits are duration-checked
/// before any bytes are spent, and every produced file is sniffed for real
/// audio bytes before it can enter the library.
/// </summary>
public class DownloadManager : IDownloadManager
{
    private readonly IDownloaderRegistry _registry;
    private readonly ILogger<DownloadManager> _logger;

    public DownloadManager(IDownloaderRegistry registry, ILogger<DownloadManager> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>Live progress reporting per melodyId, for the UI.</summary>
    private readonly ConcurrentDictionary<string, DownloadProgress> _progress = new();

    /// <summary>Why the last waterfall run for a melodyId produced nothing, for honest error messages.</summary>
    private readonly ConcurrentDictionary<string, string> _lastFailure = new();

    public IReadOnlyList<DownloadProgress> SnapshotProgress()
        => _progress.Values.ToList();

    /// <inheritdoc />
    public async Task<string?> DownloadAsync(string sourceUrl, string outputDirectory, string melodyId, CancellationToken ct = default)
    {
        // Direct URL case: let plugins download the URL as-is.
        foreach (var downloader in _registry.GetEnabled())
        {
            if (!await AvailableAsync(downloader, ct))
                continue;

            try
            {
                var result = await downloader.DownloadAsync(sourceUrl, outputDirectory, melodyId, null, ct);
                if (result.Success && result.FilePath is not null)
                {
                    if (RejectNonAudio(result.FilePath, downloader.Name))
                        continue;
                    return result.FilePath;
                }
                _logger.LogWarning("{Name} failed for {Url}: {Error}", downloader.Name, sourceUrl, result.ErrorMessage);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "{Name} threw for {Url}", downloader.Name, sourceUrl);
            }
        }
        return null;
    }

    /// <summary>
    /// Why the last DownloadTrackAsync call for this melodyId failed to
    /// produce a file: outside-the-filters skips or no hit at all. Null
    /// when the last call succeeded (or never ran).
    /// </summary>
    public string? LastFailure(string melodyId)
        => _lastFailure.TryGetValue(melodyId, out var reason) ? reason : null;

    /// <summary>
    /// The main entry for playlist tracks: search through the plugin
    /// waterfall and download the first hit. The query carries the
    /// expected duration and the ISRC when known.
    /// </summary>
    public async Task<string?> DownloadTrackAsync(
        TrackQuery query, string outputDirectory, string melodyId, CancellationToken ct = default)
    {
        var quality = query.Quality;
        _progress[melodyId] = new DownloadProgress(melodyId, query.Title, "searching", null, null);
        _lastFailure.TryRemove(melodyId, out _);
        // Why nothing landed: hits the filters rejected, versus no hit at all.
        var skippedByBand = 0;
        var skippedByDuration = 0;
        var skippedByInvalid = 0;
        try
        {
            // Quality-aware routing: plugins that cannot serve this request
            // are excluded before any network call, survivors ranked so the
            // most promising source runs first.
            var plan = QualityRouter.Route(quality, _registry.GetEnabled());
            foreach (var (downloader, reason) in plan.Excluded)
                _logger.LogDebug("Router excluded {Name}: {Reason}", downloader.Name, reason);

            foreach (var downloader in plan.Plugins)
            {
                ct.ThrowIfCancellationRequested();
                if (!await AvailableAsync(downloader, ct))
                    continue;

                var hit = await SearchAsync(downloader, query, ct);
                if (hit is null)
                {
                    _logger.LogDebug("{Name}: no search hit for '{Artist} - {Title}'",
                        downloader.Name, query.Artist, query.Title);
                    continue;
                }

                // The primary hit plus the runner-ups the plugin attached:
                // a dead first choice (deleted video, geo-block) gets one
                // more chance from the same plugin instead of losing it.
                foreach (var candidate in Candidates(hit))
                {
                    ct.ThrowIfCancellationRequested();

                    // Gross duration mismatch: a preview or an unrelated
                    // edit is the wrong track, reject before spending bytes.
                    if (!FuzzyMatcher.DurationPlausible(query.ExpectedDuration, candidate.Duration))
                    {
                        skippedByDuration++;
                        _logger.LogInformation(
                            "{Name} candidate '{Hit}' is {HitLength:0}s long, expected ~{Expected:0}s; skipping",
                            downloader.Name, candidate.Title,
                            candidate.Duration?.TotalSeconds ?? 0, query.ExpectedDuration?.TotalSeconds ?? 0);
                        continue;
                    }

                    // Quality gate: reject reported bitrates outside the requested band.
                    if (!quality.IsWithinBand(candidate.BitrateKbps))
                    {
                        skippedByBand++;
                        _logger.LogInformation(
                            "{Name} hit for '{Title}' is {Hit} kbps, outside the requested {Min}-{Max} kbps band; skipping",
                            downloader.Name, query.Title, candidate.BitrateKbps,
                            quality.MinKbps?.ToString() ?? "any", quality.MaxKbps?.ToString() ?? "any");
                        continue;
                    }

                    _progress[melodyId] = new DownloadProgress(
                        melodyId, query.Title, "downloading", downloader.Name, null, candidate.MatchConfidence);
                    var result = await DownloadAsync(downloader, candidate.SourceUrl!, outputDirectory, melodyId, quality, ct);
                    if (result is null || !result.Success || result.FilePath is null)
                    {
                        _logger.LogWarning("{Name} download failed for '{Artist} - {Title}': {Error}",
                            downloader.Name, query.Artist, query.Title, result?.ErrorMessage ?? "no result");
                        continue;
                    }

                    // Real bytes only: an error page saved as audio must
                    // never pass. The check reads the file's leading bytes,
                    // never its extension.
                    if (RejectNonAudio(result.FilePath, downloader.Name))
                    {
                        skippedByInvalid++;
                        continue;
                    }

                    // Enforce the band on the real file: plugins can over-promise,
                    // the measurement never lies.
                    var measured = BitrateProbe.MeasureKbps(result.FilePath);
                    if (!quality.IsWithinBand(measured))
                    {
                        skippedByBand++;
                        _logger.LogInformation(
                            "{Name} produced {Measured} kbps for '{Title}', outside the requested {Min}-{Max} kbps band; rejecting",
                            downloader.Name, measured, query.Title,
                            quality.MinKbps?.ToString() ?? "any", quality.MaxKbps?.ToString() ?? "any");
                        FileCleanup.DeleteQuietly(result.FilePath);
                        continue;
                    }

                    // One tagging point for every plugin: the MELODY_ID in
                    // the file is what survives a database wipe, so it is
                    // written here, once, after the file passed the band
                    // gate. Plugins never tag it themselves.
                    TaglibHelper.WriteMelodyId(result.FilePath, melodyId);

                    _progress[melodyId] = new DownloadProgress(
                        melodyId, query.Title, "done", downloader.Name, result.FilePath, candidate.MatchConfidence,
                        Warning: measured is null ? "bitrate could not be verified" : null);
                    return result.FilePath;
                }
            }

            _progress[melodyId] = new DownloadProgress(
                melodyId, query.Title, "failed", null, null,
                Warning: "No plugin could download this track");
            // Distinguish "nothing matched your filters" from "nobody has
            // the track": the fix for the first is relaxing the filters.
            _lastFailure[melodyId] =
                plan.Plugins.Count == 0 && plan.Excluded.Count > 0
                    ? "your quality filters excluded every plugin"
                    : skippedByBand > 0
                        ? $"sources had {skippedByBand} file(s) outside your quality filters"
                        : skippedByInvalid > 0
                            ? "sources returned files that are not audio"
                            : skippedByDuration > 0
                                ? "sources only had versions with the wrong length"
                                : "no source had this track";
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _progress[melodyId] = new DownloadProgress(melodyId, query.Title, "failed", null, null, Warning: ex.Message);
            throw;
        }
    }

    /// <summary>The primary hit followed by its runner-up alternatives.</summary>
    private static IEnumerable<DownloaderSearchHit> Candidates(DownloaderSearchHit hit)
    {
        yield return hit;
        if (hit.Alternatives is null) yield break;
        foreach (var alternative in hit.Alternatives)
            yield return alternative;
    }

    /// <summary>
    /// Availability with its own guard: a plugin that throws while
    /// probing is unavailable for this track, nothing more.
    /// </summary>
    private async Task<bool> AvailableAsync(IDownloader downloader, CancellationToken ct)
    {
        try
        {
            if (await downloader.IsAvailableAsync(ct)) return true;
            _logger.LogDebug("Skipping {Name}: unavailable", downloader.Name);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Skipping {Name}: availability probe threw", downloader.Name);
            return false;
        }
    }

    /// <summary>Search with its own guard; a throw is a miss for this plugin.</summary>
    private async Task<DownloaderSearchHit?> SearchAsync(
        IDownloader downloader, TrackQuery query, CancellationToken ct)
    {
        try
        {
            return await downloader.SearchAsync(query, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Name} search threw for '{Artist} - {Title}'",
                downloader.Name, query.Artist, query.Title);
            return null;
        }
    }

    /// <summary>Download with its own guard; a throw is a miss for this candidate.</summary>
    private async Task<DownloaderDownloadResult?> DownloadAsync(
        IDownloader downloader, string sourceUrl, string outputDirectory,
        string melodyId, DownloadQuality quality, CancellationToken ct)
    {
        try
        {
            return await downloader.DownloadAsync(sourceUrl, outputDirectory, melodyId, quality, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Name} download threw for {Url}", downloader.Name, sourceUrl);
            return null;
        }
    }

    /// <summary>
    /// Deletes a file whose bytes are not audio and reports the rejection.
    /// Returns true when the file was rejected (caller moves on).
    /// </summary>
    private bool RejectNonAudio(string filePath, string pluginName)
    {
        if (AudioSignature.LooksLikeAudio(filePath)) return false;
        _logger.LogWarning(
            "{Name} produced {File}, which is not audio (no recognizable container signature); deleting",
            pluginName, filePath);
        FileCleanup.DeleteQuietly(filePath);
        return true;
    }
}
