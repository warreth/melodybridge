using System.Collections.Concurrent;
using MelodyBridge.Core;
using MelodyBridge.Core.Logging;
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
    private readonly PluginAuthMonitor? _auth;

    public DownloadManager(
        IDownloaderRegistry registry,
        ILogger<DownloadManager> logger,
        ProviderHealth? health = null,
        SettingsStore? settings = null,
        PluginAuthMonitor? auth = null)
    {
        _registry = registry;
        _logger = logger;
        _health = health ?? new ProviderHealth();
        _settings = settings;
        _auth = auth;
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

    /// <summary>Which plugin produced the last successful file per melodyId.</summary>
    private readonly ConcurrentDictionary<string, string> _lastPlugin = new();

    /// <summary>Which plugin last searched for the track, successful or not.</summary>
    private readonly ConcurrentDictionary<string, string> _lastAttemptedPlugin = new();

    /// <summary>
    /// Every candidate the enabled plugins offer for a track, best first,
    /// for the manual source picker. Deliberately no acceptance gate and
    /// no quality routing: the list exists to show a human what the
    /// automatic match refused or skipped. Guarded like the waterfall: an
    /// unavailable or throwing plugin contributes nothing.
    /// </summary>
    public Task<IReadOnlyList<DownloadCandidate>> SearchCandidatesAsync(
        TrackQuery query, CancellationToken ct = default)
        => SearchCandidatesAsync(query, onCandidate: null, ct);

    /// <summary>
    /// Same list, but every enabled plugin searches at the same time: the
    /// wait is the slowest plugin instead of the sum of all of them, and
    /// each plugin still makes exactly one search request, so nothing
    /// costs extra rate-limit budget. <paramref name="onCandidate"/>
    /// receives each hit the moment its plugin answers, so a picker can
    /// fill in while the rest is still running.
    /// </summary>
    public async Task<IReadOnlyList<DownloadCandidate>> SearchCandidatesAsync(
        TrackQuery query, Action<DownloadCandidate>? onCandidate,
        CancellationToken ct = default)
    {
        var (pluginBudget, _) = await BudgetsAsync(ct);
        var results = new System.Collections.Concurrent.ConcurrentBag<DownloadCandidate>();
        var seenUrls = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        var tasks = _registry.GetEnabled().Select(async downloader =>
        {
            if (_health.ShouldSkip(downloader.Id)) return;

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(pluginBudget);
            try
            {
                if (!await AvailableAsync(downloader, budget.Token)) return;
                var hits = await downloader.SearchAllAsync(query, budget.Token);
                _auth?.Observe(downloader);
                var canServe = downloader.Capabilities.CanServe(query.Quality);
                var declared = DescribeCapabilities(downloader.Capabilities);
                foreach (var hit in hits)
                {
                    if (hit.SourceUrl is not { Length: > 0 } url || !seenUrls.TryAdd(url, 0)) continue;
                    var candidate = new DownloadCandidate(
                        downloader.Id, downloader.Name, hit.Title, hit.Artist,
                        hit.Duration, hit.BitrateKbps, url, hit.MatchConfidence,
                        CanServeRequested: canServe, DeclaredQuality: declared,
                        BitDepth: hit.BitDepth, SampleRateHz: hit.SampleRateHz);
                    results.Add(candidate);
                    onCandidate?.Invoke(candidate);
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The plugin's own search budget expired: a miss for the list.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "{Name} candidate search threw for '{Artist} - {Title}'",
                    downloader.Name, query.Artist, query.Title);
            }
        }).ToList();

        await Task.WhenAll(tasks);

        // Best first: the plugin's own confidence, then the shared score
        // (title/artist similarity with the duration penalty). The user
        // sees the same ordering the waterfall would have used, and can
        // still pick anything below it.
        return results
            .OrderByDescending(c => c.Confidence == MatchConfidence.High)
            .ThenByDescending(c => FuzzyMatcher.ScoreWithDuration(
                query.Artist, query.Title, query.ExpectedDuration, c.Artist, c.Title, c.Duration))
            .ToList();
    }

    /// <summary>
    /// Downloads the candidate a human picked. The pick overrides the
    /// ranking and the playlist's quality gates (the user saw the hit and
    /// chose it), not the sanity: bytes must sniff as audio and the
    /// MELODY_ID tag always goes on. A plugin that cannot serve the
    /// requested quality is asked for its source quality instead of
    /// transcoding: asking SoundCloud for FLAC only produces a lossy
    /// source boxed as lossless, and the picker already said so.
    /// </summary>
    public async Task<string?> DownloadCandidateAsync(
        DownloadCandidate candidate, TrackQuery query, string outputDirectory,
        string melodyId, CancellationToken ct = default)
    {
        using var correlation = LogCorrelation.Begin($"Track: {query.Artist} - {query.Title}");
        var downloader = _registry.GetEnabled().FirstOrDefault(d => d.Id == candidate.PluginId);
        if (downloader is null)
        {
            _state.SetFailure(melodyId, $"plugin '{candidate.PluginId}' is not enabled");
            return null;
        }

        _lastAttemptedPlugin[melodyId] = downloader.Id;
        _state.SetProgress(new DownloadProgress(
            melodyId, query.Title, "downloading", downloader.Name, null, candidate.Confidence));
        try
        {
            if (!await AvailableAsync(downloader, ct))
            {
                _state.SetFailure(melodyId, $"{downloader.Name} is not available");
                return null;
            }

            var canServe = downloader.Capabilities.CanServe(query.Quality);
            if (!canServe)
            {
                _logger.LogInformation(
                    "{Name} cannot serve {Quality} for '{Title}'; the pick is fetched at its source quality instead of re-encoding",
                    downloader.Name, DescribeQuality(query.Quality), query.Title);
            }
            var quality = canServe ? query.Quality : DownloadQuality.Any;
            var result = await DownloadAsync(
                downloader, candidate.SourceUrl, outputDirectory, melodyId, quality, ct);
            _auth?.Observe(downloader);
            if (result is null || !result.Success || result.FilePath is null)
            {
                _health.RecordFailure(downloader.Id);
                _state.SetFailure(melodyId,
                    result?.ErrorMessage ?? $"{downloader.Name} produced nothing");
                return null;
            }
            if (RejectNonAudio(result.FilePath, downloader.Name))
            {
                _health.RecordFailure(downloader.Id);
                _state.SetFailure(melodyId, $"{downloader.Name} returned bytes that are not audio");
                return null;
            }

            TaglibHelper.WriteMelodyId(result.FilePath, melodyId);
            _health.RecordSuccess(downloader.Id);
            _lastPlugin[melodyId] = downloader.Id;
            _state.ClearFailure(melodyId);
            _state.SetProgress(new DownloadProgress(
                melodyId, query.Title, "done", downloader.Name, result.FilePath, candidate.Confidence));
            return result.FilePath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _health.RecordFailure(downloader.Id);
            _state.SetFailure(melodyId, ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message);
            throw;
        }
    }

    /// <summary>Current in-flight download states (for UI polling).</summary>
    public IReadOnlyList<DownloadProgress> SnapshotProgress()
        => _state.Snapshot();

    /// <summary>
    /// The exact audio facts of one candidate, read by its plugin without
    /// downloading (a simulate-only yt-dlp extraction, a tier lookup).
    /// Best effort: an unknown plugin, an unavailable one or any throw
    /// yields null, and the picker falls back to the manifest.
    /// </summary>
    public async Task<CandidateAudioQuality?> ProbeQualityAsync(
        string pluginId, string sourceUrl, CancellationToken ct = default)
    {
        var downloader = _registry.GetEnabled().FirstOrDefault(d => d.Id == pluginId);
        if (downloader is null
            || string.IsNullOrWhiteSpace(sourceUrl)
            || !await AvailableAsync(downloader, ct))
        {
            return null;
        }

        try
        {
            return await downloader.ProbeQualityAsync(sourceUrl, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "{Name} quality probe failed for {Url}", downloader.Name, sourceUrl);
            return null;
        }
    }

    /// <inheritdoc />
    public string? LastPluginId(string melodyId)
        => _lastPlugin.TryGetValue(melodyId, out var id) ? id : null;

    /// <inheritdoc />
    public string? LastAttemptedPluginId(string melodyId)
        => _lastAttemptedPlugin.TryGetValue(melodyId, out var id) ? id : null;

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
                _auth?.Observe(downloader);
                if (result.Success && result.FilePath is not null)
                {
                    if (RejectNonAudio(result.FilePath, downloader.Name))
                    {
                        _health.RecordFailure(downloader.Id);
                        continue;
                    }
                    _health.RecordSuccess(downloader.Id);
                    _lastPlugin[melodyId] = downloader.Id;
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
    ///
    /// <paramref name="avoidPluginId"/> drops one plugin from this whole
    /// call, ladder steps included: the retry path of a flagged file uses
    /// it so the provider that delivered the flagged file gets no second
    /// chance at the same track.
    /// </summary>
    public async Task<string?> DownloadTrackAsync(
        TrackQuery query, string outputDirectory, string melodyId,
        DownloadFallback? fallback = null, string? avoidPluginId = null, CancellationToken ct = default)
    {
        using var correlation = LogCorrelation.Begin($"Track: {query.Artist} - {query.Title}");
        var (pluginBudget, trackBudget) = await BudgetsAsync(ct);

        // One wall-clock cap for the whole track, every plugin and ladder
        // step together: a chain of dead sources must not hold a sync run.
        using var trackBudgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        trackBudgetCts.CancelAfter(trackBudget);
        _lastPlugin.TryRemove(melodyId, out _);
        try
        {
            return await DownloadTrackCoreAsync(
                query, outputDirectory, melodyId, fallback, avoidPluginId, pluginBudget, trackBudgetCts.Token);
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
        DownloadFallback? fallback, string? avoidPluginId, TimeSpan pluginBudget, CancellationToken ct)
    {
        // The retry hint filters the plugin list before routing and before
        // the ladder is built: a step that only the avoided plugin could
        // serve drops out, and the routing reasons stay honest.
        var enabled = _registry.GetEnabled();
        if (avoidPluginId is { Length: > 0 })
        {
            var avoided = enabled.Where(p => string.Equals(p.Id, avoidPluginId, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (avoided.Count > 0)
            {
                enabled = enabled
                    .Where(p => !string.Equals(p.Id, avoidPluginId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                _logger.LogInformation(
                    "Avoiding {Name} for '{Title}': it delivered a flagged file; retrying elsewhere",
                    avoided[0].Name, query.Title);
            }
        }

        var targets = fallback is null
            ? new List<DownloadQuality> { query.Quality }
            : QualityLadder.Build(query.Quality, fallback.AllowContainerChange, enabled).ToList();
        if (targets.Count == 0) targets.Add(query.Quality);

        string? requestedFailure = null;
        for (var i = 0; i < targets.Count; i++)
        {
            var path = await TryDownloadQualityAsync(
                query with { Quality = targets[i] }, outputDirectory, melodyId, enabled, pluginBudget, ct);
            if (path is not null) return path;

            requestedFailure ??= _state.LastFailure(melodyId);
            if (i + 1 < targets.Count)
                _logger.LogInformation(
                    "No download for '{Title}' at {Quality}; stepping down to {Next}",
                    query.Title, DescribeQuality(targets[i]), DescribeQuality(targets[i + 1]));
        }

        // Nothing worked with the avoided plugin out. Say exactly that
        // instead of pretending no source has the track: the user can
        // settle the review with Keep, or take the flagged provider back.
        if (enabled.Count == 0 && avoidPluginId is { Length: > 0 })
        {
            var name = _registry.Get(avoidPluginId)?.Name ?? avoidPluginId;
            _state.SetFailure(melodyId,
                $"no other provider is available: {name} delivered the flagged file and was skipped");
            _state.SetProgress(new DownloadProgress(melodyId, query.Title, "failed", null, null,
                Warning: "No other provider left to retry"));
            return null;
        }

        // The headline failure is the quality the user asked for, not the
        // last step of the ladder.
        if (requestedFailure is not null) _state.SetFailure(melodyId, requestedFailure);
        return null;
    }

    /// <summary>One waterfall pass for one concrete quality target.</summary>
    private async Task<string?> TryDownloadQualityAsync(
        TrackQuery query, string outputDirectory, string melodyId,
        IReadOnlyList<IDownloader> enabled, TimeSpan pluginBudget, CancellationToken ct)
    {
        var quality = query.Quality;
        _state.SetProgress(new DownloadProgress(melodyId, query.Title, "searching", null, null));
        _state.ClearFailure(melodyId);
        // Why nothing landed: hits the filters rejected, versus no hit at all.
        var skippedByBand = 0;
        var skippedByDuration = 0;
        var skippedByInvalid = 0;
        var skippedByFormat = 0;
        string? authFailure = null;
        try
        {
            // Quality-aware routing: plugins that cannot serve this request
            // are excluded before any network call, survivors ranked so the
            // most promising source runs first.
            var plan = QualityRouter.Route(quality, enabled);
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
                    {
                        authFailure ??= AuthProblem(downloader) is { } reason
                            ? $"{downloader.Name}: {reason}"
                            : null;
                        continue;
                    }

                    _lastAttemptedPlugin[melodyId] = downloader.Id;
                    var hit = await SearchAsync(downloader, query, pluginCt);
                    _auth?.Observe(downloader);
                    if (hit is null)
                    {
                        authFailure ??= AuthProblem(downloader) is { } reason
                            ? $"{downloader.Name}: {reason}"
                            : null;
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
                            _logger.LogDebug(
                                "{Name} candidate '{Hit}' is {HitLength:0}s long, expected ~{Expected:0}s; skipping",
                                downloader.Name, candidate.Title,
                                candidate.Duration?.TotalSeconds ?? 0, query.ExpectedDuration?.TotalSeconds ?? 0);
                            continue;
                        }

                        // Quality gate: reject reported bitrates outside the requested band.
                        if (!quality.IsWithinBand(candidate.BitrateKbps))
                        {
                            skippedByBand++;
                            _logger.LogDebug(
                                "{Name} hit for '{Title}' is {Hit} kbps, outside the requested {Min}-{Max} kbps band; skipping",
                                downloader.Name, query.Title, candidate.BitrateKbps,
                                quality.MinKbps?.ToString() ?? "any", quality.MaxKbps?.ToString() ?? "any");
                            continue;
                        }

                        _state.SetProgress(new DownloadProgress(
                            melodyId, query.Title, "downloading", downloader.Name, null, candidate.MatchConfidence));
                        var result = await DownloadAsync(downloader, candidate.SourceUrl!, outputDirectory, melodyId, quality, pluginCt);
                        _auth?.Observe(downloader);
                        if (result is null || !result.Success || result.FilePath is null)
                        {
                            _health.RecordFailure(downloader.Id);
                            authFailure ??= AuthProblem(downloader) is { } reason
                                ? $"{downloader.Name}: {reason}"
                                : null;
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

                        _lastPlugin[melodyId] = downloader.Id;
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
                authFailure
                    ?? (plan.Plugins.Count == 0 && plan.Excluded.Count > 0
                    ? "your quality filters excluded every plugin"
                    : skippedByBand > 0
                        ? $"sources had {skippedByBand} file(s) outside your quality filters"
                        : skippedByFormat > 0
                            ? $"sources only returned files in another format than the requested {DedupQuality.RequestedContainer(quality)}"
                            : skippedByInvalid > 0
                                ? "sources returned files that are not audio"
                                : skippedByDuration > 0
                                    ? "sources only had versions with the wrong length"
                                    : "no source had this track"));
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
            var available = await downloader.IsAvailableAsync(ct);
            _auth?.Observe(downloader);
            if (available) return true;

            if (AuthProblem(downloader) is { } problem)
                _logger.LogDebug("{Name} needs a login fix: {Reason}", downloader.Name, problem);
            else
                _logger.LogDebug("Skipping {Name}: unavailable", downloader.Name);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Skipping {Name}: availability probe threw", downloader.Name);
            return false;
        }
    }

    /// <summary>
    /// The plugin's short login problem, or null when its stored account
    /// is fine. A missing login or an unreachable check is not an auth
    /// problem; the card already calls that unavailable.
    /// </summary>
    private static string? AuthProblem(IDownloader downloader)
        => downloader is IPluginAuthStatusProvider { AuthStatus.NeedsAttention: true } provider
            ? provider.AuthStatus.Message ?? "The stored login no longer works"
            : null;

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

    /// <summary>
    /// The containers a plugin declares, as one short line for the picker
    /// when no exact probe covers a candidate ("FLAC (lossless), MP3 up to
    /// 320"). Null when the manifest names no concrete container.
    /// </summary>
    internal static string? DescribeCapabilities(PluginCapabilities capabilities)
    {
        var parts = capabilities.Deliverables
            .Where(d => d.Container != AudioFormat.Auto)
            .Select(d =>
            {
                var label = d.Container switch
                {
                    AudioFormat.Flac => "FLAC",
                    AudioFormat.Mp3 => "MP3",
                    AudioFormat.Opus => "Opus",
                    AudioFormat.Aac => "AAC",
                    _ => d.Container.ToString(),
                };
                var lossless = d.Lossless ? " (lossless)" : "";
                // Lossless files have no meaningful kbps band: showing one
                // would only confuse.
                var band = d.Controlled && !d.Lossless && (d.MinKbps is not null || d.MaxKbps is not null)
                    ? d switch
                    {
                        { MinKbps: { } min, MaxKbps: { } max } when min == max => $" {min}",
                        { MinKbps: { } min, MaxKbps: { } max } => $" {min}-{max}",
                        { MaxKbps: { } max } => $" up to {max}",
                        { MinKbps: { } min } => $" {min}+",
                        _ => "",
                    }
                    : "";
                return $"{label}{lossless}{band}";
            })
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }
}
