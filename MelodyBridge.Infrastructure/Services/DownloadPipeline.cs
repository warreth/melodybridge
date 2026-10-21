using MelodyBridge.Core;
using MelodyBridge.Core.Logging;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// The per-track download pipeline: claim, dedup, waterfall, tagging,
/// integrity gate, archive copy and the final status write. This is where
/// the store's download logic lives; the store itself only delegates.
/// </summary>
internal sealed class DownloadPipeline
{
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly IDownloadManager _downloadManager;
    private readonly SettingsStore _settings;
    private readonly TrackLinker _linker;
    private readonly IsrcResolver? _isrcResolver;
    private readonly CoverArtFetcher? _coverArt;
    private readonly LyricsResolver? _lyrics;
    private readonly ILogger _logger;
    private readonly Func<VerificationOptions> _verification;
    private readonly DataChangeTracker _changes;

    public DownloadPipeline(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        IDownloadManager downloadManager,
        SettingsStore settings,
        TrackLinker linker,
        IsrcResolver? isrcResolver,
        CoverArtFetcher? coverArt,
        LyricsResolver? lyrics,
        ILogger logger,
        Func<VerificationOptions> verification,
        DataChangeTracker? changes = null)
    {
        _dbFactory = dbFactory;
        _downloadManager = downloadManager;
        _settings = settings;
        _linker = linker;
        _isrcResolver = isrcResolver;
        _coverArt = coverArt;
        _lyrics = lyrics;
        _logger = logger;
        _verification = verification;
        _changes = changes ?? new DataChangeTracker();
    }

    /// <summary>
    /// Download every track of a playlist that has no local file yet, into the
    /// playlist's TargetDirectory (or override). Updates DownloadStatus/
    /// CurrentPath per track as it goes. Returns (downloaded, failed).
    ///
    /// Tracks are claimed one at a time (atomically, status pending ->
    /// in_progress) so several concurrent callers never race on the same
    /// track: that is what makes the coordinator's max-concurrent
    /// workers safe.
    /// </summary>
    public async Task<(int downloaded, int failed)> DownloadMissingAsync(
        string playlistId, string? outputDirectoryOverride = null, int limit = int.MaxValue, CancellationToken ct = default)
    {
        // Prologue: read the run-wide settings (folder, format) once, then
        // let go of the context. Each track below runs in its own fresh
        // context so nothing stale survives between claims.
        string dir, playlistName, preferredFormat;
        bool strict, allowContainerChange;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var entity = await db.Playlists.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == playlistId, ct)
                ?? throw new InvalidOperationException($"Playlist '{playlistId}' not found");

            // Folder precedence: an explicit override wins outright;
            // otherwise the playlist's own folder under the default music
            // path (one subfolder per playlist, so dedup links and archive
            // copies stay separated per playlist).
            var musicPath = await _settings.GetAsync("music_path", "/music", ct);
            var playlistFolders = await _settings.GetBoolAsync("playlist_folders", true, ct);
            dir = outputDirectoryOverride is not null
                ? outputDirectoryOverride
                : PlaylistRules.ResolvePlaylistFolder(entity.TargetDirectory, musicPath, entity.Name, playlistFolders);
            if (string.IsNullOrWhiteSpace(dir))
                throw new InvalidOperationException(
                    "No download folder configured. Set one in the playlist settings.");
            playlistName = entity.Name;
            preferredFormat = entity.PreferredFormat;
            if (string.IsNullOrWhiteSpace(preferredFormat))
                preferredFormat = await _settings.GetAsync("default_quality", "auto", ct);
            strict = entity.StrictQuality;
            allowContainerChange = entity.AllowContainerChange;
        }

        var downloaded = 0;
        var failed = 0;
        var verification = _verification();
        // Tracks already attempted in this call: failed tracks stay
        // claimable (so later runs retry them), but never twice in one call.
        var attempted = new HashSet<int>();

        for (var attempt = 0; attempt < limit; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var trackId = await ClaimNextPendingAsync(playlistId, attempted, ct);
            if (trackId is null) break; // nothing left to claim

            var result = await DownloadClaimedTrackAsync(
                trackId.Value, dir, preferredFormat, strict, allowContainerChange, verification, ct);
            if (result == "downloaded") downloaded++;
            else if (result == "failed") failed++;
            // "needs-review" tracks are neither downloaded nor failed:
            // they wait for the user's dedup decision and the run moves on.
        }

        _logger.LogInformation("Download run for '{Playlist}' ({Quality}): {Downloaded} downloaded, {Failed} failed",
            playlistName, preferredFormat, downloaded, failed);

        return (downloaded, failed);
    }

    /// <summary>
    /// Downloads exactly one track (the per-track button). Claims that
    /// track atomically, runs the same pipeline as a full run, and
    /// returns "downloaded", "failed" or "claimed-by-another-run",
    /// never touching any other track of the playlist.
    /// </summary>
    public async Task<string> DownloadTrackAsync(
        int trackId, CancellationToken ct = default, bool avoidLastProvider = false)
    {
        int id;
        string? avoidPluginId = null;
        string dir, preferredFormat;
        bool strict, allowContainerChange;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var track = await db.Tracks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == trackId, ct);
            if (track is null) return "not-found";
            id = track.Id;

            if (avoidLastProvider && track.DownloadStatus == "failed"
                && !string.IsNullOrWhiteSpace(track.MelodyId))
            {
                avoidPluginId = _downloadManager.LastAttemptedPluginId(track.MelodyId);
            }

            var playlist = await db.Playlists.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == track.PlaylistEntityId, ct);
            var musicPath = await _settings.GetAsync("music_path", "/music", ct);
            var playlistFolders = await _settings.GetBoolAsync("playlist_folders", true, ct);
            dir = PlaylistRules.ResolvePlaylistFolder(playlist?.TargetDirectory, musicPath, playlist?.Name, playlistFolders);
            preferredFormat = playlist?.PreferredFormat ?? "";
            if (string.IsNullOrWhiteSpace(preferredFormat))
                preferredFormat = await _settings.GetAsync("default_quality", "auto", ct);
            strict = playlist?.StrictQuality ?? true;
            allowContainerChange = playlist?.AllowContainerChange ?? true;
        }
        if (string.IsNullOrWhiteSpace(dir))
            throw new InvalidOperationException(
                "No download folder configured. Set one in the playlist settings.");

        // Claim exactly this track: same conditional UPDATE as the
        // full-run claim, so a single-track click and a running batch
        // never download the same track twice.
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var claimed = await SqliteDatabase.RetryBusyAsync(() => db.Tracks
                .Where(t => t.Id == id
                    && (t.DownloadStatus == null || t.DownloadStatus == "pending" || t.DownloadStatus == "failed"))
                .ExecuteUpdateAsync(
                    s => s.SetProperty(t => t.DownloadStatus, "in_progress"), ct));
            if (claimed != 1) return "claimed-by-another-run";
        }

        // The hint is consumed by the claimed-track step, so it must be
        // on the row before that step reads it.
        if (!string.IsNullOrWhiteSpace(avoidPluginId))
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            await SqliteDatabase.RetryBusyAsync(() => db.Tracks
                .Where(t => t.Id == id)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(t => t.AvoidPluginId, avoidPluginId), ct));
        }

        return await DownloadClaimedTrackAsync(
            id, dir, preferredFormat, strict, allowContainerChange, _verification(), ct);
    }

    /// <summary>
    /// The per-track pipeline shared by the full run and the single-track
    /// button: search, download, tag, integrity-check, write status.
    /// The caller has already claimed the track.
    /// </summary>
    private async Task<string> DownloadClaimedTrackAsync(
        int trackId, string dir, string preferredFormat,
        bool strict, bool allowContainerChange, VerificationOptions verification, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(CancellationToken.None);
        // The lookup uses CancellationToken.None: a cancel arriving between
        // the claim and this call must still reach the status write below,
        // otherwise the claimed row stays in_progress forever.
        var track = await db.Tracks.FindAsync(new object[] { trackId }, CancellationToken.None);
        if (track is null) return "skipped";

        using var correlation = LogCorrelation.Begin(
            $"Track: {track.Artist ?? "Unknown artist"} - {track.Title ?? track.MelodyId ?? "Unknown track"}");

        // The retry hint is one-shot: consume it now, whatever happens
        // next, so a failed attempt never locks the provider out forever.
        var avoidPluginId = track.AvoidPluginId;
        track.AvoidPluginId = null;

        if (string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title))
        {
            track.DownloadStatus = "failed";
            track.DownloadError = "missing artist/title metadata";
            // The same busy-tolerant save as every other status write: a
            // locked database must not turn a missing tag into a crash.
            await SaveQuietlyAsync(db);
            return "failed";
        }

        try
        {
            var primary = PlaylistRules.ParseQuality(preferredFormat);
            var expectedDuration = track.DurationMs is > 0
                ? TimeSpan.FromMilliseconds(track.DurationMs.Value)
                : (TimeSpan?)null;

            // ISRC: the source may know it (Spotify account path) or not
            // (embed scrape, YouTube, CSV). The resolver fills the gap via
            // Deezer so ISRC-indexed catalogs can match exactly; the result
            // persists with the track row in the finally below.
            if (string.IsNullOrWhiteSpace(track.Isrc) && _isrcResolver is not null)
            {
                track.Isrc = await _isrcResolver.ResolveAsync(
                    track.Artist!, track.Title!, expectedDuration, ct);
            }

            // Dedup first: when the same track already exists in another
            // playlist folder, no network call is needed at all. Exact
            // quality matches hardlink instantly; a mismatch pauses the
            // track for a human decision instead of guessing.
            if (await TryDeduplicateAsync(track, primary, db, ct) is { } verdict)
                return verdict;

            var query = new TrackQuery(track.Artist!, track.Title!, primary, expectedDuration, track.Isrc);
            // Strict: exactly this quality or nothing. Non-strict: the
            // manager walks the ladder the enabled plugins declare, and
            // the playlist decides whether a step may change container.
            // A "Retry" review left one provider out: the attempt honors
            // it and the provider may come back on a later plain attempt.
            var path = await _downloadManager.DownloadTrackAsync(
                query, dir, track.MelodyId!,
                strict ? null : new DownloadFallback(allowContainerChange), avoidPluginId, ct);

            if (path is null)
            {
                track.DownloadStatus = "failed";
                // Say why: filter misses can be fixed by the user,
                // missing tracks cannot.
                track.DownloadError = _downloadManager.LastFailure(track.MelodyId!)
                    ?? "no plugin could download this track";
                return "failed";
            }

            return await CompleteDownloadAsync(track, db, path, expectedDuration, verification, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cooperative cancel: the claim must not stick. Save with a
            // non-cancelled token, otherwise the row stays "in_progress"
            // and no later run can ever claim it again.
            track.DownloadStatus = "failed";
            track.DownloadError = "cancelled";
            await SaveQuietlyAsync(db);
            throw;
        }
        catch (OperationCanceledException)
        {
            // A provider or HTTP client cancelled its own call while the
            // run is still alive. That is a failed track, not a reason to
            // stop the worker (and the rest of the run with it). The
            // failure is written here and the cancellation is passed on,
            // so the caller can retry this track once with a fresh
            // attempt: swallowing it here would cost the retry.
            track.DownloadStatus = "failed";
            track.DownloadError = "the provider cancelled the download";
            throw;
        }
        catch (Exception ex)
        {
            track.DownloadStatus = "failed";
            track.DownloadError = ex.Message.Truncate(1000);
            return "failed";
        }
        finally
        {
            // A cancelled token must not swallow the status write: the
            // claimed row has to leave "in_progress" whatever happened.
            await SaveQuietlyAsync(db);
        }
    }

    /// <summary>
    /// The post-download half shared by the waterfall and a hand-picked
    /// source: write-through tags, the integrity verdict, metadata
    /// enrichment, the archive copy and the final row state. Returns
    /// "downloaded" or "failed" (corrupt bytes are deleted).
    /// </summary>
    private async Task<string> CompleteDownloadAsync(
        TrackEntity track, MelodyBridgeDbContext db, string path,
        TimeSpan? expectedDuration, VerificationOptions verification, CancellationToken ct)
    {
        // Write-through: the file always ends up with full tags,
        // whatever the plugin could or could not read from the source.
        Tagging.TaglibHelper.WriteTags(
            path,
            title: track.Title,
            artist: track.Artist,
            album: track.Album,
            albumArtist: track.AlbumArtist,
            // Position is 0-based; the tag and the UI are 1-based.
            track: (uint?)(track.Position + 1),
            lyrics: track.Lyrics);

        // Verification, not rejection: broken bytes fail the track
        // (deleted, retried on a later run), a wrong length or an
        // inflated bitrate flags the file and keeps it, so the user
        // can keep it or retry it at another provider. DurationMs is
        // what the source platform advertised for the track.
        var integrity = Audio.FileIntegrity.Check(
            path, expectedDuration,
            expectedDuration is { } expected ? verification.ToleranceFor(expected) : null);
        if (integrity.Issue == Audio.IntegrityIssue.Corrupt)
        {
            try { System.IO.File.Delete(path); } catch { /* best effort */ }
            track.DownloadStatus = "failed";
            track.DownloadError = $"corrupt download: {integrity.Reason}";
            return "failed";
        }

        // Media-server metadata: cover art and lyrics, both optional and
        // both isolated. They run after the integrity gate so a corrupt
        // file never costs a network call.
        await EnrichMetadataAsync(track, path, ct);

        // The fresh file replaces what the row held: delete the previous
        // primary and archive copy by name first (a hardlink twin in
        // another playlist keeps its bytes), so a retry that lands under a
        // new name cannot leave orphans and the archive copy below always
        // matches this download.
        await DeleteReplacedFilesAsync(db, track, path, ct);
        track.DownloadStatus = "downloaded";
        track.CurrentPath = path;
        track.LastSeenAt = DateTime.UtcNow;
        track.Bitrate = Audio.BitrateProbe.MeasureKbps(path);
        AudioProbe.Fill(track, path);
        track.SourcePluginId = _downloadManager.LastPluginId(track.MelodyId!);
        ApplyQualityReview(track, path, integrity, verification);
        // No ffprobe means the check could not run at all. The file is
        // kept (deleting a good download because a tool is missing is
        // worse), and the row says it was not verified.
        if (integrity.ToolMissing)
        {
            const string unverified = "could not verify the download: ffprobe is not installed";
            track.Warning = string.IsNullOrEmpty(track.Warning)
                ? unverified : $"{track.Warning}; {unverified}";
        }
        await ArchiveDownloadedTrackAsync(track, db, ct);
        return "downloaded";
    }

    /// <summary>
    /// Deletes the files a rerun replaced. Name-wise and reference-checked:
    /// a path another row still points at (a hardlink twin or a shared
    /// archive) survives, and a failed delete only warns on the row.
    /// </summary>
    private async Task DeleteReplacedFilesAsync(
        MelodyBridgeDbContext db, TrackEntity track, string newPath, CancellationToken ct)
    {
        var oldPaths = new[] { track.CurrentPath, track.ArchivePath }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(p => !PlaylistStore.PathsEqual(p, newPath))
            .ToList();
        foreach (var oldPath in oldPaths)
        {
            if (await db.Tracks.AsNoTracking()
                .AnyAsync(t => t.Id != track.Id
                    && (t.CurrentPath == oldPath || t.ArchivePath == oldPath), ct))
            {
                continue;
            }
            if (!File.Exists(oldPath)) continue;
            try
            {
                File.Delete(oldPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not delete the replaced file {Path}: {Reason}",
                    oldPath, ex.Message);
                track.Warning = string.IsNullOrEmpty(track.Warning)
                    ? $"could not remove the previous file {oldPath}: {ex.Message}"
                    : $"{track.Warning}; could not remove the previous file {oldPath}: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// Candidates every enabled plugin offers for a track, for the manual
    /// source picker. A read-only search: no claim, no row change.
    /// </summary>
    public Task<IReadOnlyList<DownloadCandidate>> SearchCandidatesAsync(
        int trackId, CancellationToken ct = default)
        => SearchCandidatesAsync(trackId, onCandidate: null, ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<DownloadCandidate>> SearchCandidatesAsync(
        int trackId, Action<DownloadCandidate>? onCandidate, CancellationToken ct = default)
        => (await SearchCandidatesDetailedAsync(trackId, onCandidate, ct)).Candidates;

    /// <summary>The same search, with the plugins that could not run.</summary>
    public async Task<SourceSearchOutcome> SearchCandidatesDetailedAsync(
        int trackId, Action<DownloadCandidate>? onCandidate, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var track = await db.Tracks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == trackId, ct);
        if (track is null
            || string.IsNullOrWhiteSpace(track.Artist)
            || string.IsNullOrWhiteSpace(track.Title))
        {
            return new SourceSearchOutcome(
                Array.Empty<DownloadCandidate>(), Array.Empty<SourceSearchProblem>());
        }

        var playlist = await db.Playlists.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == track.PlaylistEntityId, ct);
        var preferred = playlist?.PreferredFormat ?? "";
        if (string.IsNullOrWhiteSpace(preferred))
            preferred = await _settings.GetAsync("default_quality", "auto", ct);
        var expectedDuration = track.DurationMs is > 0
            ? TimeSpan.FromMilliseconds(track.DurationMs.Value)
            : (TimeSpan?)null;

        var query = new TrackQuery(
            track.Artist!, track.Title!, PlaylistRules.ParseQuality(preferred),
            expectedDuration, track.Isrc);
        return await _downloadManager.SearchCandidatesDetailedAsync(query, onCandidate, ct);
    }

    /// <summary>
    /// The exact audio facts one candidate's plugin can read before a
    /// download, for the picker's quality line. Best effort: null when
    /// the plugin cannot know or the probe fails.
    /// </summary>
    public Task<CandidateAudioQuality?> ProbeCandidateQualityAsync(
        string pluginId, string sourceUrl, CancellationToken ct = default)
        => _downloadManager.ProbeQualityAsync(pluginId, sourceUrl, ct);

    /// <summary>
    /// Downloads one candidate a human picked for a track, replacing any
    /// existing file. The pick overrides the ranking, not the pipeline:
    /// the old primary and archive are removed (name-wise, so a hardlink
    /// twin elsewhere survives), the row is claimed like any download, and
    /// the result goes through the same tagging, integrity, enrichment and
    /// archive steps. Returns "downloaded", "failed",
    /// "claimed-by-another-run" or "not-found".
    /// </summary>
    public async Task<string> DownloadChosenTrackAsync(
        int trackId, DownloadCandidate candidate, CancellationToken ct = default)
    {
        int id;
        string dir, preferredFormat;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var track = await db.Tracks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == trackId, ct);
            if (track is null) return "not-found";
            id = track.Id;

            var playlist = await db.Playlists.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == track.PlaylistEntityId, ct);
            var musicPath = await _settings.GetAsync("music_path", "/music", ct);
            var playlistFolders = await _settings.GetBoolAsync("playlist_folders", true, ct);
            dir = PlaylistRules.ResolvePlaylistFolder(playlist?.TargetDirectory, musicPath, playlist?.Name, playlistFolders);
            preferredFormat = playlist?.PreferredFormat ?? "";
            if (string.IsNullOrWhiteSpace(preferredFormat))
                preferredFormat = await _settings.GetAsync("default_quality", "auto", ct);
        }
        if (string.IsNullOrWhiteSpace(dir))
        {
            throw new InvalidOperationException(
                "No download folder configured. Set one in the playlist settings.");
        }

        await using (var db = await _dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            var track = await db.Tracks.FindAsync(new object[] { id }, CancellationToken.None);
            if (track is null) return "not-found";

            // Read the old names before the claim clears them.
            var oldPaths = new[] { track.CurrentPath, track.ArchivePath }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Claim and reset in one conditional UPDATE; a running batch
            // that already holds the row wins, the pick waits for a later
            // moment instead of racing it.
            var claimed = await db.Tracks
                .Where(t => t.Id == id && t.DownloadStatus != "in_progress")
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.DownloadStatus, "in_progress")
                    .SetProperty(t => t.CurrentPath, (string?)null)
                    .SetProperty(t => t.ArchivePath, (string?)null)
                    .SetProperty(t => t.IsHardLink, false)
                    .SetProperty(t => t.DownloadError, (string?)null)
                    .SetProperty(t => t.Warning, (string?)null)
                    .SetProperty(t => t.ReviewKind, (string?)null)
                    .SetProperty(t => t.ReviewText, (string?)null)
                    .SetProperty(t => t.SpectrumAccepted, false)
                    .SetProperty(t => t.Bitrate, (int?)null)
                    .SetProperty(t => t.SampleRateHz, (int?)null)
                    .SetProperty(t => t.FileSizeBytes, (long?)null)
                    .SetProperty(t => t.MediaType, (string?)null), CancellationToken.None);
            if (claimed != 1) return "claimed-by-another-run";

            // Replace: the old file and archive go, name-wise. A path
            // another row still references is a shared name and survives.
            foreach (var path in oldPaths)
            {
                if (await db.Tracks.AsNoTracking()
                    .AnyAsync(t => t.Id != id && (t.CurrentPath == path || t.ArchivePath == path),
                        CancellationToken.None))
                {
                    continue;
                }
                if (!File.Exists(path)) continue;
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        "Could not delete the old file {Path} for the picked source ({Reason}); the pick is refused",
                        path, ex.Message);
                    track.DownloadStatus = "failed";
                    track.DownloadError = $"could not replace the existing file {path}: {ex.Message}";
                    await SaveQuietlyAsync(db);
                    return "failed";
                }
            }
        }

        return await DownloadPickedTrackAsync(id, candidate, dir, preferredFormat, ct);
    }

    /// <summary>
    /// The claimed half of a hand-picked download: builds the query,
    /// downloads exactly the chosen candidate and runs the shared
    /// post-download steps.
    /// </summary>
    private async Task<string> DownloadPickedTrackAsync(
        int trackId, DownloadCandidate candidate, string dir, string preferredFormat, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(CancellationToken.None);
        var track = await db.Tracks.FindAsync(new object[] { trackId }, CancellationToken.None);
        if (track is null) return "skipped";

        if (string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title))
        {
            track.DownloadStatus = "failed";
            track.DownloadError = "missing artist/title metadata";
            // The same busy-tolerant save as every other status write: a
            // locked database must not turn a missing tag into a crash.
            await SaveQuietlyAsync(db);
            return "failed";
        }

        try
        {
            var primary = PlaylistRules.ParseQuality(preferredFormat);
            var expectedDuration = track.DurationMs is > 0
                ? TimeSpan.FromMilliseconds(track.DurationMs.Value)
                : (TimeSpan?)null;
            var query = new TrackQuery(track.Artist!, track.Title!, primary, expectedDuration, track.Isrc);

            var path = await _downloadManager.DownloadCandidateAsync(
                candidate, query, dir, track.MelodyId!, ct);
            if (path is null)
            {
                track.DownloadStatus = "failed";
                track.DownloadError = _downloadManager.LastFailure(track.MelodyId!)
                    ?? $"'{candidate.PluginName}' produced nothing for this pick";
                return "failed";
            }

            return await CompleteDownloadAsync(track, db, path, expectedDuration, _verification(), ct);
        }
        catch (OperationCanceledException)
        {
            track.DownloadStatus = "failed";
            track.DownloadError = "cancelled";
            await SaveQuietlyAsync(db);
            throw;
        }
        catch (Exception ex)
        {
            track.DownloadStatus = "failed";
            track.DownloadError = ex.Message.Truncate(1000);
            return "failed";
        }
        finally
        {
            await SaveQuietlyAsync(db);
        }
    }

    /// <summary>
    /// Fills the two metadata extras a downloaded file can carry: the cover
    /// art and the lyrics. Both are best effort: a missing fetcher, a
    /// missing URL, a timeout or a miss leaves the file exactly as it was,
    /// and nothing here can fail the track.
    /// </summary>
    private async Task EnrichMetadataAsync(TrackEntity track, string path, CancellationToken ct)
    {
        try
        {
            if (_coverArt is not null
                && !string.IsNullOrWhiteSpace(track.CoverUrl)
                && !Tagging.TaglibHelper.HasPictures(path))
            {
                var cover = await _coverArt.FetchAsync(track.CoverUrl, ct);
                if (cover is { Length: > 0 })
                    Tagging.TaglibHelper.WriteTags(path, coverArt: cover);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Cover embed failed for '{Title}'", track.Title);
        }

        try
        {
            if (_lyrics is not null
                && string.IsNullOrWhiteSpace(track.Lyrics)
                && !string.IsNullOrWhiteSpace(track.Title))
            {
                var lyrics = await _lyrics.ResolveAsync(
                    track.Artist, track.Title, track.Album,
                    track.DurationMs is > 0 ? TimeSpan.FromMilliseconds(track.DurationMs.Value) : null,
                    ct);
                if (!string.IsNullOrWhiteSpace(lyrics))
                {
                    track.Lyrics = lyrics;
                    Tagging.TaglibHelper.WriteTags(path, lyrics: lyrics);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Lyrics embed failed for '{Title}'", track.Title);
        }
    }

    /// <summary>
    /// SaveChanges that survives a cancelled caller token and a short
    /// overlap with another writer. A write that still fails leaves the
    /// row in its claimed state; the next run's stale-claim recovery is
    /// the safety net, so this never takes the run down.
    /// </summary>
    private async Task SaveQuietlyAsync(MelodyBridgeDbContext db)
    {
        try
        {
            await SqliteDatabase.RetryBusyAsync(async () =>
            {
                await db.SaveChangesAsync(CancellationToken.None);
                return true;
            });
            _changes.Bump();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not persist track status after the download attempt");
        }
    }

    /// <summary>
    /// The dedup decision for one claimed track, made before any network
    /// call: an exact quality match links the existing file into this
    /// playlist's folder (and its archive, see <see cref="TrackLinker"/>),
    /// a quality mismatch pauses the track as "needs-review" with the
    /// conflict spelled out, and no usable existing file (or dedup off, or
    /// a cross-device link) returns null so the normal waterfall runs.
    /// </summary>
    private async Task<string?> TryDeduplicateAsync(
        TrackEntity track, DownloadQuality quality, MelodyBridgeDbContext db,
        CancellationToken ct)
    {
        // A user decision to download a fresh copy trumps the conflict:
        // consume the one-shot flag and let the waterfall run.
        if (track.SkipDedup)
        {
            track.SkipDedup = false;
            return null;
        }

        return await _linker.TryLinkAsync(track, db, quality, pauseOnQualityMismatch: true, ct) switch
        {
            TrackLinkOutcome.Linked => "downloaded",
            TrackLinkOutcome.QualityMismatch => "needs-review",
            // No usable existing file or an impossible link: download.
            _ => null,
        };
    }

    /// <summary>See <see cref="TrackLinker.FindCandidateAsync"/>.</summary>
    public Task<DedupMatch?> FindDedupCandidateAsync(
        string melodyId, int excludingTrackId, CancellationToken ct = default)
        => _linker.FindCandidateAsync(melodyId, excludingTrackId, ct);

    /// <summary>
    /// Resolves a paused dedup conflict the way the user picked.
    /// "download-new": clears the pause so the next run downloads a
    /// fresh file in the playlist's own quality. "hardlink-existing":
    /// links the other playlist's file as-is, accepting that this one
    /// track differs from the playlist's target quality.
    /// Returns false when the track is not in review or the existing
    /// file vanished meanwhile.
    /// </summary>
    public async Task<bool> ResolveReviewAsync(int trackId, bool hardlinkExisting, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var track = await db.Tracks.FindAsync(new object[] { trackId }, ct);
        if (track is null || track.DownloadStatus != "needs-review" || track.MelodyId is null)
            return false;

        if (!hardlinkExisting)
        {
            track.DownloadStatus = "pending";
            // The next attempt must skip dedup: the user chose a fresh
            // file and the alternative is the same pause again.
            track.SkipDedup = true;
            track.Warning = null;
            await db.SaveChangesAsync(ct);
            _changes.Bump();
            return true;
        }

        var candidate = await _linker.FindCandidateAsync(track.MelodyId, track.Id, ct);
        if (candidate is null) return false;

        var playlist = await db.Playlists.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == track.PlaylistEntityId, ct);
        var outcome = await _linker.TryLinkAsync(track, db,
            PlaylistRules.ParseQuality(playlist?.PreferredFormat),
            pauseOnQualityMismatch: false, ct,
            candidate: candidate,
            acceptQualityDifference: true,
            warning: $"hardlinked from {candidate.SourcePlaylistId}: "
                + $"{candidate.Describe()} (user accepted the quality difference)");
        if (outcome != TrackLinkOutcome.Linked)
        {
            _logger.LogInformation(
                "Hard link for review '{Title}' failed; leaving the track in review", track.Title);
            return false;
        }
        await db.SaveChangesAsync(ct);
        _changes.Bump();
        return true;
    }

    /// <summary>
    /// Atomically claims the next pending track of a playlist (status
    /// pending/failed/null -> in_progress) and returns its row id, or
    /// null when nothing is left. The conditional UPDATE is what makes
    /// concurrent claimers safe: two callers never get the same track.
    /// Tracks in <paramref name="exclude"/> were already attempted by this
    /// caller and are skipped, so failed tracks are not retried in-loop.
    /// </summary>
    private async Task<int?> ClaimNextPendingAsync(string playlistId, HashSet<int> exclude, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var trackId = await db.Tracks
            .Where(t => t.PlaylistEntityId == playlistId
                && !exclude.Contains(t.Id)
                && (t.DownloadStatus == null || t.DownloadStatus == "pending" || t.DownloadStatus == "failed"))
            .OrderBy(t => t.Position)
            .Select(t => t.Id)
            .FirstOrDefaultAsync(ct);
        if (trackId == 0) return null;

        var claimed = await db.Tracks
            .Where(t => t.Id == trackId
                && (t.DownloadStatus == null || t.DownloadStatus == "pending" || t.DownloadStatus == "failed"))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.DownloadStatus, "in_progress"), ct);
        if (claimed != 1) return null; // lost the race: another caller won it

        exclude.Add(trackId);
        return trackId;
    }

    /// <summary>See <see cref="TrackLinker.ArchiveDownloadedTrackAsync"/>.</summary>
    private Task ArchiveDownloadedTrackAsync(
        TrackEntity track, MelodyBridgeDbContext db, CancellationToken ct)
        => _linker.ArchiveDownloadedTrackAsync(track, db, ct);

    /// <summary>
    /// Rebuilds the archive copy of one downloaded track from its primary
    /// file, on demand from the row's archive action. A previous archive
    /// failure is dropped from the warning first, so the new verdict is the
    /// only one on the row. Returns (ok, reason): reason is null on success
    /// and the failure text otherwise.
    /// </summary>
    public async Task<(bool Ok, string? Reason)> RetryArchiveAsync(
        int trackId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var track = await db.Tracks.FindAsync(new object[] { trackId }, ct);
        if (track is null) return (false, "track not found");
        if (track.DownloadStatus != "downloaded" || string.IsNullOrWhiteSpace(track.CurrentPath))
            return (false, "the track has no downloaded file yet");
        if (!File.Exists(track.CurrentPath))
            return (false, "the downloaded file is gone; download the track again");

        track.Warning = StripArchiveFailures(track.Warning);
        var outcome = await _linker.ArchiveDownloadedTrackAsync(track, db, ct);
        await db.SaveChangesAsync(ct);
        _changes.Bump();
        return outcome switch
        {
            ArchiveOutcome.Written => (true, null),
            ArchiveOutcome.Skipped => (false, "no archive folder is set up for this playlist"),
            _ => (false, track.Warning ?? "the archive copy could not be written"),
        };
    }

    /// <summary>
    /// Removes every "archive copy failed: ..." segment from a warning,
    /// keeping the other segments (a warning can be several facts joined
    /// with "; "). The archive verdict is rebuilt by the retry itself.
    /// </summary>
    internal static string? StripArchiveFailures(string? warning)
    {
        if (warning is not { Length: > 0 }) return warning;
        var kept = warning
            .Split("; ", StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.StartsWith("archive copy failed:", StringComparison.Ordinal))
            .ToList();
        return kept.Count == 0 ? null : string.Join("; ", kept);
    }

    /// <summary>
    /// Settles a flagged quality review (length mismatch, inflated
    /// bitrate) for one track the way the user picked. Keep: the file
    /// stays and the flag is cleared. Retry: the flagged primary and its
    /// archive copy are deleted, the track goes back to pending with a
    /// one-shot hint to avoid the plugin that delivered the flagged file,
    /// and the caller runs the download. Returns false when the track is
    /// not in a flagged state.
    /// </summary>
    public async Task<bool> ResolveQualityReviewAsync(
        int trackId, QualityReviewAction action, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var track = await db.Tracks.FindAsync(new object[] { trackId }, ct);
        if (track is null || track.DownloadStatus != "downloaded" || track.ReviewKind is null)
            return false;

        if (action == QualityReviewAction.Keep)
        {
            _logger.LogInformation(
                "Quality review for '{Title}': file kept as it is ({Reason})", track.Title, track.ReviewText);
            // Remember a settled spectrum verdict so the next recompute
            // does not reopen the same decision for the same file.
            if (TrackReview.Has(track.ReviewKind, TrackReview.Spectrum))
                track.SpectrumAccepted = true;
            track.ReviewKind = null;
            track.ReviewText = null;
            await db.SaveChangesAsync(ct);
            _changes.Bump();
            return true;
        }

        // Retry: replace the flagged file. The archive copy mirrors it, so
        // it goes too; the next download produces a fresh one. Deleting
        // first keeps the folder clean and stops plugins that refuse to
        // overwrite an existing file. The deletion is verified: when the
        // flagged bytes stay on disk, the library passes would re-link
        // them as if a fresh download had landed, so the retry refuses
        // instead and the row keeps its open review.
        if (track.CurrentPath is not null && File.Exists(track.CurrentPath))
        {
            try
            {
                File.Delete(track.CurrentPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Quality review for '{Title}': could not delete the flagged file {Path} ({Reason}); retry refused",
                    track.Title, track.CurrentPath, ex.Message);
                return false;
            }
        }
        if (track.ArchivePath is not null)
            FileCleanup.DeleteQuietly(track.ArchivePath);

        var avoid = track.SourcePluginId;
        track.DownloadStatus = "pending";
        track.DownloadError = null;
        track.CurrentPath = null;
        track.ArchivePath = null;
        track.IsHardLink = false;
        track.Warning = null;
        track.ReviewKind = null;
        track.ReviewText = null;
        track.SpectrumAccepted = false;
        track.Bitrate = null;
        track.SampleRateHz = null;
        track.FileSizeBytes = null;
        track.MediaType = null;
        track.AvoidPluginId = string.IsNullOrEmpty(avoid) ? null : avoid;
        await db.SaveChangesAsync(ct);
        _changes.Bump();
        _logger.LogInformation(
            "Quality review for '{Title}': retrying{Elsewhere}",
            track.Title,
            avoid is null ? "" : $", avoiding provider {avoid}");
        return true;
    }

    /// <summary>
    /// Fills the review and warning columns of a just-downloaded file from
    /// the post-download verdicts: a wrong length and an inflated bitrate
    /// become one review the user settles (Keep or Retry), a low but
    /// honest bandwidth stays a plain informational warning. The file
    /// itself always stays; only broken bytes are deleted one gate above.
    /// </summary>
    private static void ApplyQualityReview(
        TrackEntity track, string path, Audio.IntegrityResult integrity, VerificationOptions verification)
    {
        var kinds = new List<string>();
        var texts = new List<string>();
        string? warning = null;

        // This file is new: a Keep decision for a previous file does not
        // carry over to it.
        track.SpectrumAccepted = false;

        if (integrity.Issue == Audio.IntegrityIssue.DurationMismatch && integrity.Reason is { } lengthReason)
        {
            kinds.Add(TrackReview.Length);
            texts.Add(lengthReason);
        }

        if (SpectrumAnalyzer.Verify(path, verification.Spectrum, verification.SpectrumClassGap) is { } spectrum)
        {
            if (spectrum.LooksInflated)
            {
                kinds.Add(TrackReview.Spectrum);
                texts.Add($"bitrate looks inflated: {spectrum.Note}");
            }
            else if (spectrum.CodecCutoff && spectrum.EffectiveKbpsClass is > 0 and < 320)
            {
                warning = $"quality hint: {spectrum.Note}";
            }
        }

        track.ReviewKind = kinds.Count == 0 ? null : TrackReview.Join(kinds);
        track.ReviewText = texts.Count == 0 ? null : string.Join("; ", texts);
        track.Warning = warning;
    }
}
