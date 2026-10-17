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
    private readonly ProviderHealth _health;
    private readonly SettingsStore? _settings;

    public DownloadManager(
        IDownloaderRegistry registry,
        ILogger<DownloadManager> logger,
        ProviderHealth? health = null,
        SettingsStore? settings = null)
    {
        _registry = registry;
        _logger = logger;
        _health = health ?? new ProviderHealth();
        _settings = settings;
    }

    /// <summary>Plugin budget when the setting row is missing.</summary>
    internal static readonly TimeSpan DefaultPluginBudget = TimeSpan.FromMinutes(4);

    /// <summary>Whole-track budget when the setting row is missing.</summary>
    internal static readonly TimeSpan DefaultTrackBudget = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Hard wall-clock caps for one track, read from the settings table on
    /// every run (no restart). The plugin budget bounds one plugin's whole
    /// attempt (probe, search, candidates); the track budget bounds every
    /// plugin and ladder step together. Values are clamped so a typo cannot
    /// disable downloading.
    /// </summary>
    internal async Task<(TimeSpan Plugin, TimeSpan Track)> BudgetsAsync(CancellationToken ct)
    {
        if (_settings is null) return (DefaultPluginBudget, DefaultTrackBudget);
        try
        {
            var plugin = await _settings.GetAsync("download_plugin_budget_seconds", "240", ct);
            var track = await _settings.GetAsync("download_track_budget_seconds", "1200", ct);
            return (
                TimeSpan.FromSeconds(Math.Clamp(ParseSeconds(plugin, 240), 1, 3600)),
                TimeSpan.FromSeconds(Math.Clamp(ParseSeconds(track, 1200), 1, 14400)));
        }
        catch
        {
            // Settings unavailable (tests without the store): sane defaults.
            return (DefaultPluginBudget, DefaultTrackBudget);
        }
    }

    private static int ParseSeconds(string raw, int fallback)
        => int.TryParse(raw, out var value) ? value : fallback;

    /// <summary>"4 minute" / "45 second": a budget reads naturally in the failure text.</summary>
    private static string DescribeBudget(TimeSpan budget)
        => budget.TotalMinutes >= 1
            ? $"{budget.TotalMinutes:0.#} minute"
            : $"{budget.TotalSeconds:0} second";

    /// <summary>
    /// Live progress plus the last failure per melodyId, for the UI and for
    /// honest error messages. Bounded inside: the manager is a singleton.
    /// </summary>
    private readonly DownloadState _state = new();

    public IReadOnlyList<DownloadProgress> SnapshotProgress()
        => _state.Snapshot();

    /// <inheritdoc />
    public async Task<string?> DownloadAsync(string sourceUrl, string outputDirectory, string melodyId, CancellationToken ct = default)
    {
        // Direct URL case: let plugins download the URL as-is.
        foreach (var downloader in _registry.GetEnabled())
        {
            if (_health.ShouldSkip(downloader.Id))
                continue;
            if (!await AvailableAsync(downloader, ct))
                continue;

            try
            {
                var result = await downloader.DownloadAsync(sourceUrl, outputDirectory, melodyId, null, ct);
                if (result.Success && result.FilePath is not null)
                {
                    if (RejectNonAudio(result.FilePath, downloader.Name))
                    {
                        _health.RecordFailure(downloader.Id);
                        continue;
                    }
                    _health.RecordSuccess(downloader.Id);
                    return result.FilePath;
                }
                _health.RecordFailure(downloader.Id);
                _logger.LogWarning("{Name} failed for {Url}: {Error}", downloader.Name, sourceUrl, result.ErrorMessage);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _health.RecordFailure(downloader.Id);
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
        => _state.LastFailure(melodyId);

    /// <summary>
    /// The main entry for playlist tracks: search through the plugin
    /// waterfall and download the first hit. The query carries the
    /// expected duration and the ISRC when known.
    ///
    /// Strict (no fallback policy): exactly the requested quality, or
    /// nothing. Non-strict: walk the ladder the enabled plugins declare
    /// (QualityLadder), stepping down until a source delivers. The reason
    /// that is reported when nothing works is always the requested
    /// quality's own reason.
    /// </summary>
    public async Task<string?> DownloadTrackAsync(
        TrackQuery query, string outputDirectory, string melodyId,
        DownloadFallback? fallback = null, CancellationToken ct = default)
    {
        var (pluginBudget, trackBudget) = await BudgetsAsync(ct);

        // One wall-clock cap for the whole track, every plugin and ladder
        // step together: a chain of dead sources must not hold a sync run.
        using var trackBudgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        trackBudgetCts.CancelAfter(trackBudget);
        try
        {
            return await DownloadTrackCoreAsync(
                query, outputDirectory, melodyId, fallback, pluginBudget, trackBudgetCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _state.SetProgress(new DownloadProgress(melodyId, query.Title, "failed", null, null,
                Warning: "the track budget expired"));
            _state.SetFailure(melodyId,
                $"the track took longer than its {DescribeBudget(trackBudget)} budget");
            _logger.LogWarning("Track '{Title}' exceeded its {Budget} budget; giving up",
                query.Title, DescribeBudget(trackBudget));
            return null;
        }
    }

    /// <summary>The per-quality waterfall passes, under the track budget.</summary>
    private async Task<string?> DownloadTrackCoreAsync(
        TrackQuery query, string outputDirectory, string melodyId,
        DownloadFallback? fallback, TimeSpan pluginBudget, CancellationToken ct)
    {
        var targets = fallback is null
            ? new List<DownloadQuality> { query.Quality }
            : QualityLadder.Build(query.Quality, fallback.AllowContainerChange, _registry.GetEnabled()).ToList();
        if (targets.Count == 0) targets.Add(query.Quality);

        string? requestedFailure = null;
        for (var i = 0; i < targets.Count; i++)
        {
            var path = await TryDownloadQualityAsync(
                query with { Quality = targets[i] }, outputDirectory, melodyId, pluginBudget, ct);
            if (path is not null) return path;

            requestedFailure ??= _state.LastFailure(melodyId);
            if (i + 1 < targets.Count)
                _logger.LogInformation(
                    "No download for '{Title}' at {Quality}; stepping down to {Next}",
                    query.Title, DescribeQuality(targets[i]), DescribeQuality(targets[i + 1]));
        }

        // The headline failure is the quality the user asked for, not the
        // last step of the ladder.
        if (requestedFailure is not null) _state.SetFailure(melodyId, requestedFailure);
        return null;
    }

    /// <summary>One waterfall pass for one concrete quality target.</summary>
    private async Task<string?> TryDownloadQualityAsync(
        TrackQuery query, string outputDirectory, string melodyId,
        TimeSpan pluginBudget, CancellationToken ct)
    {
        var quality = query.Quality;
        _state.SetProgress(new DownloadProgress(melodyId, query.Title, "searching", null, null));
        _state.ClearFailure(melodyId);
        // Why nothing landed: hits the filters rejected, versus no hit at all.
        var skippedByBand = 0;
        var skippedByDuration = 0;
        var skippedByInvalid = 0;
        var skippedByFormat = 0;
        try
        {
            // Quality-aware routing: plugins that cannot serve this request
            // are excluded before any network call, survivors ranked so the
            // most promising source runs first.
            var plan = QualityRouter.Route(quality, _registry.GetEnabled());
            foreach (var (downloader, reason) in plan.Excluded)
                _logger.LogDebug("Router excluded {Name}: {Reason}", downloader.Name, reason);

            // The router's controlled-first rule stays; inside those groups
            // a provider whose recent attempts mostly failed runs after the
            // healthy ones. The user's order survives otherwise.
            var ordered = plan.Plugins
                .OrderBy(p => p.Capabilities.ServesControlled(quality) ? 0 : 1)
                .ThenBy(p => _health.IsDegraded(p.Id) ? 1 : 0)
                .ToList();

            foreach (var downloader in ordered)
            {
                ct.ThrowIfCancellationRequested();

                // Open circuit: skip without a call until the cooldown passed.
                if (_health.ShouldSkip(downloader.Id))
                {
                    _logger.LogDebug("Skipping {Name}: recent attempts kept failing (circuit open)",
                        downloader.Name);
                    continue;
                }

                // One budget for this plugin's whole attempt: a provider
                // that hangs must not hold the track, whatever it is doing.
                using var pluginBudgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                pluginBudgetCts.CancelAfter(pluginBudget);
                var pluginCt = pluginBudgetCts.Token;
                try
                {
                    if (!await AvailableAsync(downloader, pluginCt))
                        continue;

                    var hit = await SearchAsync(downloader, query, pluginCt);
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
                        pluginCt.ThrowIfCancellationRequested();

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

                        _state.SetProgress(new DownloadProgress(
                            melodyId, query.Title, "downloading", downloader.Name, null, candidate.MatchConfidence));
                        var result = await DownloadAsync(downloader, candidate.SourceUrl!, outputDirectory, melodyId, quality, pluginCt);
                        if (result is null || !result.Success || result.FilePath is null)
                        {
                            _health.RecordFailure(downloader.Id);
                            _logger.LogWarning("{Name} download failed for '{Artist} - {Title}': {Error}",
                                downloader.Name, query.Artist, query.Title, result?.ErrorMessage ?? "no result");
                            continue;
                        }

                        // Real bytes only: an error page saved as audio must
                        // never pass. The check reads the file's leading bytes,
                        // never its extension.
                        if (RejectNonAudio(result.FilePath, downloader.Name))
                        {
                            _health.RecordFailure(downloader.Id);
                            skippedByInvalid++;
                            continue;
                        }

                        // An explicit format request must be honoured by the
                        // actual bytes: a "lossless" request that silently
                        // accepted an MP3 would break the preset's promise and
                        // skip the documented lossy fallback.
                        var requestedContainer = DedupQuality.RequestedContainer(quality);
                        if (requestedContainer is not null)
                        {
                            var actual = AudioSignature.DetectContainer(result.FilePath);
                            if (actual is null || !DedupQuality.FormatEquals(actual, requestedContainer))
                            {
                                _health.RecordFailure(downloader.Id);
                                skippedByFormat++;
                                _logger.LogInformation(
                                    "{Name} produced {Actual} for the requested {Requested} for '{Title}'; rejecting",
                                    downloader.Name, actual ?? "unknown", requestedContainer, query.Title);
                                FileCleanup.DeleteQuietly(result.FilePath);
                                continue;
                            }
                        }

                        // Enforce the band on the real file: plugins can over-promise,
                        // the measurement never lies.
                        var measured = BitrateProbe.MeasureKbps(result.FilePath);
                        if (!quality.IsWithinBand(measured))
                        {
                            _health.RecordFailure(downloader.Id);
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

                        _health.RecordSuccess(downloader.Id);
                        _state.SetProgress(new DownloadProgress(
                            melodyId, query.Title, "done", downloader.Name, result.FilePath, candidate.MatchConfidence,
                            Warning: measured is null ? "bitrate could not be verified" : null));
                        return result.FilePath;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // The plugin's budget expired mid-call (or the plugin
                    // cancelled its own call): an unhealthy signal, and the
                    // track moves on to the next plugin.
                    _health.RecordFailure(downloader.Id);
                    if (pluginBudgetCts.IsCancellationRequested)
                        _logger.LogWarning(
                            "{Name} exceeded its {Seconds:0} second budget for '{Title}'; moving on",
                            downloader.Name, pluginBudget.TotalSeconds, query.Title);
                    else
                        _logger.LogWarning(
                            "{Name} cancelled its own call for '{Title}'; moving on",
                            downloader.Name, query.Title);
                    continue;
                }
            }

            _state.SetProgress(new DownloadProgress(
                melodyId, query.Title, "failed", null, null,
                Warning: "No plugin could download this track"));
            // Distinguish "nothing matched your filters" from "nobody has
            // the track": the fix for the first is relaxing the filters.
            _state.SetFailure(melodyId,
                plan.Plugins.Count == 0 && plan.Excluded.Count > 0
                    ? "your quality filters excluded every plugin"
                    : skippedByBand > 0
                        ? $"sources had {skippedByBand} file(s) outside your quality filters"
                        : skippedByFormat > 0
                            ? $"sources only returned files in another format than the requested {DedupQuality.RequestedContainer(quality)}"
                            : skippedByInvalid > 0
                                ? "sources returned files that are not audio"
                                : skippedByDuration > 0
                                    ? "sources only had versions with the wrong length"
                                    : "no source had this track");
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _state.SetProgress(new DownloadProgress(melodyId, query.Title, "failed", null, null, Warning: ex.Message));
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
            _health.RecordFailure(downloader.Id);
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
            _health.RecordFailure(downloader.Id);
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

    /// <summary>Short form of a quality target for the stepping log.</summary>
    private static string DescribeQuality(DownloadQuality q)
    {
        var container = q.Format == AudioFormat.Auto ? "any container" : q.Format.ToString();
        var band = (q.MinKbps, q.MaxKbps) switch
        {
            (null, null) => "any bitrate",
            (null, { } max) => $"up to {max} kbps",
            ({ } min, null) => $"{min}+ kbps",
            ({ } min, { } max) => $"{min}-{max} kbps",
        };
        return $"{container}, {band}";
    }
}
