using MelodyBridge.Core;
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
    private readonly HardLinkService _hardLinks;
    private readonly IsrcResolver? _isrcResolver;
    private readonly CoverArtFetcher? _coverArt;
    private readonly LyricsResolver? _lyrics;
    private readonly ILogger _logger;
    private readonly Func<VerificationOptions> _verification;

    public DownloadPipeline(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        IDownloadManager downloadManager,
        SettingsStore settings,
        HardLinkService hardLinks,
        IsrcResolver? isrcResolver,
        CoverArtFetcher? coverArt,
        LyricsResolver? lyrics,
        ILogger logger,
        Func<VerificationOptions> verification)
    {
        _dbFactory = dbFactory;
        _downloadManager = downloadManager;
        _settings = settings;
        _hardLinks = hardLinks;
        _isrcResolver = isrcResolver;
        _coverArt = coverArt;
        _lyrics = lyrics;
        _logger = logger;
        _verification = verification;
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

            // Folder precedence: an explicit override, the playlist's own
            // folder, then the app-wide music_path default.
            dir = outputDirectoryOverride ?? entity.TargetDirectory
                ?? await _settings.GetAsync("music_path", "/music", ct);
            if (string.IsNullOrWhiteSpace(dir))
                throw new InvalidOperationException(
                    "No download folder configured. Set one in the playlist settings.");
            playlistName = entity.Name;
            preferredFormat = entity.PreferredFormat;
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
    public async Task<string> DownloadTrackAsync(int trackId, CancellationToken ct = default)
    {
        int id;
        string dir, preferredFormat;
        bool strict, allowContainerChange;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var track = await db.Tracks.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == trackId, ct);
            if (track is null) return "not-found";
            id = track.Id;

            var playlist = await db.Playlists.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == track.PlaylistEntityId, ct);
            dir = playlist?.TargetDirectory
                ?? await _settings.GetAsync("music_path", "/music", ct);
            preferredFormat = playlist?.PreferredFormat ?? "";
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
            var claimed = await db.Tracks
                .Where(t => t.Id == id
                    && (t.DownloadStatus == null || t.DownloadStatus == "pending" || t.DownloadStatus == "failed"))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.DownloadStatus, "in_progress"), ct);
            if (claimed != 1) return "claimed-by-another-run";
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

        // The retry hint is one-shot: consume it now, whatever happens
        // next, so a failed attempt never locks the provider out forever.
        var avoidPluginId = track.AvoidPluginId;
        track.AvoidPluginId = null;

        if (string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title))
        {
            track.DownloadStatus = "failed";
            track.DownloadError = "missing artist/title metadata";
            await db.SaveChangesAsync(CancellationToken.None);
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
            if (await TryDeduplicateAsync(track, dir, primary, db, ct) is { } verdict)
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
        catch (OperationCanceledException)
        {
            // Cooperative cancel: the claim must not stick. Save with a
            // non-cancelled token, otherwise the row stays "in_progress"
            // and no later run can ever claim it again.
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
            // A cancelled token must not swallow the status write: the
            // claimed row has to leave "in_progress" whatever happened.
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

    /// <summary>SaveChanges that survives a cancelled caller token.</summary>
    private async Task SaveQuietlyAsync(MelodyBridgeDbContext db)
    {
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The in-memory track is lost either way; the next run's
            // stale-claim recovery is the safety net.
            _logger.LogWarning(ex, "Could not persist track status after cancellation");
        }
    }

    /// <summary>
    /// The dedup decision for one claimed track, made before any network
    /// call. Finds an existing downloaded file for the same MelodyId in
    /// another playlist folder and:
    ///  - exact quality match: hardlinks it into this playlist's folder
    ///    (no download, no duplicate bytes) and returns "downloaded";
    ///  - quality mismatch: pauses the track as "needs-review" with the
    ///    conflict spelled out, returns "needs-review";
    ///  - no usable existing file, dedup disabled, or a cross-device
    ///    link: returns null so the normal download waterfall runs.
    ///
    /// Copying is never a fallback here: when a hard link is impossible
    /// the honest options are a real download or a user decision.
    /// </summary>
    private async Task<string?> TryDeduplicateAsync(
        TrackEntity track, string dir, DownloadQuality quality, MelodyBridgeDbContext db,
        CancellationToken ct)
    {
        // A user decision to download a fresh copy trumps the conflict:
        // consume the one-shot flag and let the waterfall run.
        if (track.SkipDedup)
        {
            track.SkipDedup = false;
            return null;
        }

        if (track.MelodyId is null || !await _settings.GetBoolAsync(PlaylistStore.DedupSettingKey, true, ct))
            return null;

        // The fresh file may already sit in the target folder from an
        // earlier interrupted run: link to that one instead of hunting
        // for a foreign original.
        var candidate = await FindDedupCandidateAsync(track.MelodyId, track.Id, ct);
        if (candidate is null) return null;

        if (!DedupQuality.Matches(candidate.ExistingFormat, candidate.ExistingBitrateKbps, quality))
        {
            // Pause for a human: the requested quality differs from the
            // file another playlist owns. Both options are spelled out
            // for the UI; nothing is guessed, nothing is overwritten.
            track.DownloadStatus = "needs-review";
            track.DownloadError = null;
            track.Warning = $"dedup conflict: playlist wants {PlaylistRules.DescribeQuality(quality)}, "
                + $"existing file is {candidate.Describe()}. Choose hardlink existing or download new.";
            _logger.LogInformation(
                "Dedup conflict for '{Title}': existing {Existing} vs requested {Requested}; paused for review",
                track.Title, candidate.Describe(), PlaylistRules.DescribeQuality(quality));
            return "needs-review";
        }

        // Exact match: link the existing file under its own name into
        // this playlist's folder. A link failure (cross-device EXDEV,
        // read-only target, ...) falls back to a real download - a copy
        // would silently defeat the space saving the user asked for.
        var fileName = Path.GetFileName(candidate.ExistingPath);
        var linkPath = Path.Combine(dir, fileName);
        try
        {
            if (File.Exists(linkPath) && _hardLinks.LinkCount(linkPath) is not > 1)
                File.Delete(linkPath); // stale unlinked file from an earlier run
            Directory.CreateDirectory(dir);
            _hardLinks.Create(linkPath, candidate.ExistingPath);
        }
        catch (IOException ex)
        {
            _logger.LogInformation(
                "Hard link for '{Title}' failed ({Reason}); downloading instead", track.Title, ex.Message);
            return null; // fall back to the download waterfall
        }

        track.DownloadStatus = "downloaded";
        track.IsHardLink = true;
        track.CurrentPath = linkPath;
        track.LastSeenAt = DateTime.UtcNow;
        track.Bitrate = candidate.ExistingBitrateKbps;
        track.MediaType = candidate.ExistingFormat;
        AudioProbe.Fill(track, linkPath);
        track.Warning = null;
        // The linked file was verified where it came from; no review
        // carries over, and there is no provider to avoid.
        track.ReviewKind = null;
        track.ReviewText = null;
        track.SourcePluginId = null;

        // The archive copy belongs to the track, not to one playlist row:
        // when the original already has one, share that path instead of
        // writing the same bytes a second time. Without one, make it now
        // so a deduplicated track is not the only one missing from the
        // archive.
        if (!string.IsNullOrWhiteSpace(candidate.ExistingArchivePath)
            && File.Exists(candidate.ExistingArchivePath))
        {
            track.ArchivePath = candidate.ExistingArchivePath;
        }
        else
        {
            await ArchiveDownloadedTrackAsync(track, db, ct);
        }

        _logger.LogInformation(
            "Dedup: hardlinked '{Title}' from {Source} into {Target} (zero new bytes)",
            track.Title, candidate.ExistingPath, linkPath);
        return "downloaded";
    }

    /// <summary>
    /// Best existing file for the melody id: a downloaded row in another
    /// playlist (or a non-hardlink original in this one), whose file is
    /// still on disk. Originals win over hardlinks so the link target is
    /// the primary copy, and newer rows win over stale ones.
    /// </summary>
    public async Task<DedupMatch?> FindDedupCandidateAsync(
        string melodyId, int excludingTrackId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Tracks.AsNoTracking()
            .Where(t => t.MelodyId == melodyId
                && t.Id != excludingTrackId
                && t.DownloadStatus == "downloaded"
                && t.ReviewKind == null
                && t.CurrentPath != null)
            .OrderByDescending(t => t.IsHardLink == false)
            .ThenByDescending(t => t.LastSeenAt ?? DateTime.MinValue)
            .ToListAsync(ct);

        foreach (var row in existing)
        {
            if (!File.Exists(row.CurrentPath)) continue;
            return new DedupMatch(
                row.CurrentPath!,
                Path.GetExtension(row.CurrentPath).TrimStart('.').ToLowerInvariant(),
                row.Bitrate,
                row.PlaylistEntityId,
                // Quality facts live on the row; a missing bitrate is
                // treated as unverifiable (mismatch) by the verdict.
                row.Bitrate is > 0,
                row.ArchivePath);
        }
        return null;
    }

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
            return true;
        }

        var candidate = await FindDedupCandidateAsync(track.MelodyId, track.Id, ct);
        if (candidate is null) return false;

        var playlist = await db.Playlists.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == track.PlaylistEntityId, ct);
        var dir = playlist?.TargetDirectory
            ?? await _settings.GetAsync("music_path", "/music", ct);
        if (string.IsNullOrWhiteSpace(dir)) return false;

        try
        {
            var linkPath = Path.Combine(dir, Path.GetFileName(candidate.ExistingPath));
            if (File.Exists(linkPath) && _hardLinks.LinkCount(linkPath) is not > 1)
                File.Delete(linkPath);
            Directory.CreateDirectory(dir);
            _hardLinks.Create(linkPath, candidate.ExistingPath);

            track.DownloadStatus = "downloaded";
            track.IsHardLink = true;
            track.CurrentPath = linkPath;
            track.LastSeenAt = DateTime.UtcNow;
            track.Bitrate = candidate.ExistingBitrateKbps;
            track.MediaType = candidate.ExistingFormat;
            AudioProbe.Fill(track, linkPath);
            track.Warning = $"hardlinked from {candidate.SourcePlaylistId}: "
                + $"{candidate.Describe()} (user accepted the quality difference)";
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (IOException ex)
        {
            _logger.LogInformation(
                "Hard link for review '{Title}' failed ({Reason}); leaving the track in review", track.Title, ex.Message);
            return false;
        }
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

    /// <summary>
    /// Produces the secondary archive copy of a just-downloaded track:
    /// the playlist's archive directory (or the global default), a plain
    /// copy for format auto, a local ffmpeg transcode otherwise. Never
    /// throws and never touches the primary: a failed archive becomes a
    /// track warning the next run can repair, while the download stays
    /// "downloaded". Runs after the integrity gate, so the archive path
    /// persists with the track row in the same SaveChanges.
    /// </summary>
    private async Task ArchiveDownloadedTrackAsync(
        TrackEntity track, MelodyBridgeDbContext db, CancellationToken ct)
    {
        string? archiveDir = null, archiveFormat = null;

        // The archive lives on the playlist row (its own directory and
        // format); the global defaults fill in what it leaves empty.
        if (track.PlaylistEntityId is { Length: > 0 } playlistId)
        {
            var playlist = await db.Playlists.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == playlistId, ct);
            archiveDir = playlist?.ArchiveDirectory;
            archiveFormat = playlist?.ArchiveFormat;
        }
        if (string.IsNullOrWhiteSpace(archiveDir))
            archiveDir = await _settings.GetAsync("archive_path", "", ct);
        if (string.IsNullOrWhiteSpace(archiveFormat))
            archiveFormat = await _settings.GetAsync("archive_format", "auto", ct);

        if (string.IsNullOrWhiteSpace(archiveDir))
        {
            // No archive target anywhere: clear a stale archive path
            // from an earlier configuration so the UI never shows a
            // file that no longer belongs to this setup.
            track.ArchivePath = null;
            return;
        }

        var result = Audio.ArchiveCopier.Copy(
            track.CurrentPath!, archiveDir, archiveFormat,
            melodyId: track.MelodyId,
            title: track.Title, artist: track.Artist, album: track.Album,
            trackNumber: (uint?)(track.Position + 1),
            expectedDuration: track.DurationMs is > 0
                ? TimeSpan.FromMilliseconds(track.DurationMs.Value)
                : null);

        if (result.Ok)
        {
            track.ArchivePath = result.ArchivePath;
        }
        else
        {
            // Failure isolation: the primary download stands; the
            // archive problem rides along as a warning and the next
            // run (or per-track retry) repairs the copy alone.
            track.ArchivePath = null;
            track.Warning = string.IsNullOrEmpty(track.Warning)
                ? $"archive copy failed: {result.Reason}"
                : $"{track.Warning}; archive copy failed: {result.Reason}";
        }
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
            track.ReviewKind = null;
            track.ReviewText = null;
            await db.SaveChangesAsync(ct);
            return true;
        }

        // Retry: replace the flagged file. The archive copy mirrors it, so
        // it goes too; the next download produces a fresh one. Deleting
        // first keeps the folder clean and stops plugins that refuse to
        // overwrite an existing file.
        if (track.CurrentPath is not null)
            FileCleanup.DeleteQuietly(track.CurrentPath);
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
        track.Bitrate = null;
        track.SampleRateHz = null;
        track.FileSizeBytes = null;
        track.MediaType = null;
        track.AvoidPluginId = string.IsNullOrEmpty(avoid) ? null : avoid;
        await db.SaveChangesAsync(ct);
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
