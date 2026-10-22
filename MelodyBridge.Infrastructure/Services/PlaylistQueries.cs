using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Reading, listing, lifecycle and settings for stored playlists, plus the
/// two maintenance passes (recompute audio facts and re-tag the library).
/// Owns its own database contexts; no download or source logic lives here.
/// </summary>
internal sealed class PlaylistQueries
{
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly SettingsStore _settings;
    private readonly CoverArtFetcher? _coverArt;
    private readonly LyricsResolver? _lyrics;
    private readonly ILogger _logger;
    private readonly DataChangeTracker _changes;
    /// <summary>Quality-check options; the recompute pass re-runs the
    /// spectrum verdict with them so a check that was skipped or ran with
    /// older rules can be settled from the UI.</summary>
    private readonly Func<Audio.VerificationOptions> _verification;

    public PlaylistQueries(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        SettingsStore settings,
        CoverArtFetcher? coverArt,
        LyricsResolver? lyrics,
        ILogger logger,
        DataChangeTracker? changes = null,
        Func<Audio.VerificationOptions>? verification = null)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _coverArt = coverArt;
        _lyrics = lyrics;
        _logger = logger;
        _changes = changes ?? new DataChangeTracker();
        _verification = verification ?? (() => new Audio.VerificationOptions(Audio.SpectrumMode.Off));
    }

    /// <summary>All persisted playlists (without tracks), newest sync first.</summary>
    public async Task<List<PlaylistEntity>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Playlists
            .AsNoTracking()
            .OrderByDescending(p => p.LastSyncAt ?? DateTime.MinValue)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Download counters and the bytes the finished files take on disk,
    /// per playlist id, for the playlist cards.
    /// </summary>
    public async Task<Dictionary<string, (int downloaded, int failed, int total, long sizeBytes, int attention)>> GetDownloadStatsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Tracks.AsNoTracking()
            .GroupBy(t => t.PlaylistEntityId)
            .Select(g => new
            {
                Id = g.Key,
                Downloaded = g.Count(t => t.DownloadStatus == "downloaded"),
                Failed = g.Count(t => t.DownloadStatus == "failed"),
                Total = g.Count(),
                SizeBytes = g.Where(t => t.DownloadStatus == "downloaded")
                    .Sum(t => t.FileSizeBytes ?? 0),
                // The same rows the playlist page's Needs attention filter
                // shows: failed downloads and open quality or dedup reviews.
                Attention = g.Count(t => t.DownloadStatus == "failed"
                    || t.DownloadStatus == "needs-review"
                    || (t.DownloadStatus == "downloaded" && t.ReviewKind != null)),
            })
            .ToListAsync(ct);

        return rows
            .Where(r => r.Id is not null)
            .ToDictionary(r => r.Id!, r => (r.Downloaded, r.Failed, r.Total, r.SizeBytes, r.Attention));
    }

    /// <summary>
    /// Pins or unpins one playlist. The list order is a view concern;
    /// the row keeps the choice across visits and sorts above the others.
    /// </summary>
    public async Task SetPinnedAsync(string playlistId, bool pinned, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var playlist = await db.Playlists.FirstOrDefaultAsync(p => p.Id == playlistId, ct);
        if (playlist is null || playlist.IsPinned == pinned) return;
        playlist.IsPinned = pinned;
        await db.SaveChangesAsync(ct);
        _changes.Bump();
    }

    /// <summary>
    /// The ids of the playlists already saved locally, for marking
    /// imports as duplicates. Matched on source platform + external id,
    /// the same pair a re-import updates instead of duplicating.
    /// </summary>
    public async Task<HashSet<string>> GetSavedExternalIdsAsync(
        Platform platform, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var ids = await db.Playlists.AsNoTracking()
            .Where(p => p.SourcePlatform == platform && p.ExternalId != null)
            .Select(p => p.ExternalId!)
            .ToListAsync(ct);
        return ids.ToHashSet();
    }

    /// <summary>
    /// Removes one track from a playlist snapshot and, when asked,
    /// deletes its files (the primary download and its archive copy).
    /// Hard link aware: a dedup file is one name of possibly several,
    /// so the delete removes this playlist's name only; another playlist
    /// still holding the same bytes (its own name, its own row) keeps a
    /// working file and an untouched database entry. When no other row
    /// still points at the exact path, the last name goes and the bytes
    /// are freed with it.
    /// </summary>
    public async Task<bool> RemoveTrackAsync(string playlistId, int trackId, bool deleteFile, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var track = await db.Tracks
            .FirstOrDefaultAsync(t => t.Id == trackId && t.PlaylistEntityId == playlistId, ct);
        if (track is null) return false;

        if (deleteFile)
        {
            foreach (var path in OwnPaths([track]))
            {
                // Another row on the same path (primary or archive) means
                // the name is shared: the file must survive for that row,
                // only this row disappears.
                if (await PathReferencedByOthersAsync(db, track.Id, path, ct)) continue;
                try { System.IO.File.Delete(path); } catch { /* best effort */ }
            }
        }
        db.Tracks.Remove(track);
        await db.SaveChangesAsync(ct);
        _changes.Bump();
        return true;
    }

    /// <summary>The distinct, non-empty file paths a set of rows points at.</summary>
    private static IEnumerable<string> OwnPaths(IEnumerable<TrackEntity> tracks)
        => tracks
            .SelectMany(t => new[] { t.CurrentPath, t.ArchivePath })
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Deletes one track's local files (the primary download and its
    /// archive copy) but keeps the row: it returns to "pending" so a later
    /// download restores it, with every measured fact cleared. Name-wise
    /// like every delete, so a path another row still references (a hard
    /// link twin) survives for that row. The deletion is verified: a file
    /// that cannot be deleted leaves the row exactly as it was, because
    /// resetting it would let the next scan re-adopt the same bytes as a
    /// fresh download.
    /// </summary>
    public async Task<TrackFileDeleteResult> DeleteTrackFilesAsync(
        string playlistId, int trackId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var track = await db.Tracks
            .FirstOrDefaultAsync(t => t.Id == trackId && t.PlaylistEntityId == playlistId, ct);
        if (track is null) return new TrackFileDeleteResult(false, null);

        var hadFiles = false;
        var deleted = new List<string>();
        // Archive copies first: when one of them cannot be deleted, the
        // primary file the row points at is still on disk and the row stays
        // honest. The primary goes last.
        foreach (var path in OwnPaths([track])
                     .OrderBy(p => string.Equals(p, track.CurrentPath, StringComparison.Ordinal) ? 1 : 0))
        {
            hadFiles = true;
            // Another row on the same path owns the name now: nothing of
            // this row's is left there, so the reset below is still honest.
            if (await PathReferencedByOthersAsync(db, track.Id, path, ct)) continue;
            if (!System.IO.File.Exists(path)) continue;
            try
            {
                System.IO.File.Delete(path);
                deleted.Add(path);
            }
            catch (Exception ex)
            {
                // The primary only fails after its archive copies went, so
                // clear the pointer to the copy that is gone: the row must
                // not claim an archive it no longer has.
                if (string.Equals(path, track.CurrentPath, StringComparison.Ordinal)
                    && deleted.Count > 0)
                {
                    // Only the archive pointer is stale: IsHardLink
                    // describes the primary file, which is still on disk.
                    track.ArchivePath = null;
                    track.ArchiveSizeBytes = null;
                    await db.SaveChangesAsync(ct);
                    _changes.Bump();
                }
                _logger.LogWarning(
                    "Could not delete {Path} of '{Title}': {Reason}", path, track.Title, ex.Message);
                return new TrackFileDeleteResult(false, $"could not delete {path}: {ex.Message}");
            }
        }

        track.CurrentPath = null;
        track.ArchivePath = null;
        track.ArchiveSizeBytes = null;
        track.IsHardLink = false;
        track.DownloadStatus = "pending";
        track.DownloadError = null;
        track.Warning = null;
        track.ReviewKind = null;
        track.ReviewText = null;
        track.SpectrumAccepted = false;
        track.SourcePluginId = null;
        track.AvoidPluginId = null;
        track.SkipDedup = false;
        track.Bitrate = null;
        track.SampleRateHz = null;
        track.FileSizeBytes = null;
        track.MediaType = null;
        track.LastSeenAt = null;
        await db.SaveChangesAsync(ct);
        _changes.Bump();
        return new TrackFileDeleteResult(hadFiles, null);
    }

    /// <summary>True when any other row still references the exact path,
    /// as its primary file or as its archive copy.</summary>
    private static async Task<bool> PathReferencedByOthersAsync(
        MelodyBridgeDbContext db, int trackId, string path, CancellationToken ct)
        => await db.Tracks.AsNoTracking()
            .AnyAsync(t => t.Id != trackId
                && (t.CurrentPath == path || t.ArchivePath == path), ct);

    /// <summary>
    /// What deleting a playlist with its files touches: the distinct
    /// primary and archive paths of its tracks that exist on disk, and the
    /// bytes they take. Missing files are not counted: they cannot be
    /// deleted. Used for the delete dialog's honest count and size.
    /// </summary>
    public async Task<(int Files, long Bytes)> GetDeleteImpactAsync(
        string playlistId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var tracks = await db.Tracks.AsNoTracking()
            .Where(t => t.PlaylistEntityId == playlistId)
            .Select(t => new { t.CurrentPath, t.ArchivePath })
            .ToListAsync(ct);

        var paths = tracks
            .SelectMany(t => new[] { t.CurrentPath, t.ArchivePath })
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var files = 0;
        var bytes = 0L;
        foreach (var path in paths)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists) continue;
                files++;
                bytes += info.Length;
            }
            catch (Exception)
            {
                // An unreadable path cannot be judged: leave it out.
            }
        }
        return (files, bytes);
    }

    /// <summary>
    /// One persisted playlist including its current track snapshot, with
    /// only the columns the pages and the exports read. The track rows
    /// carry the resolved lyrics and the cover url, and a playlist of a
    /// few thousand tracks would drag megabytes of text into memory for
    /// a table that never shows them. Add a column here when a page
    /// starts to show it.
    /// </summary>
    public async Task<PlaylistEntity?> GetForDisplayAsync(string id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Playlists
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(p => new PlaylistEntity
            {
                Id = p.Id,
                Name = p.Name,
                Description = p.Description,
                SourceUrl = p.SourceUrl,
                SourcePlatform = p.SourcePlatform,
                ExternalId = p.ExternalId,
                CoverImageUrl = p.CoverImageUrl,
                Owner = p.Owner,
                TrackCount = p.TrackCount,
                AutoSyncEnabled = p.AutoSyncEnabled,
                AutoSyncIntervalMinutes = p.AutoSyncIntervalMinutes,
                ScheduleCron = p.ScheduleCron,
                AutoDownloadEnabled = p.AutoDownloadEnabled,
                SyncMode = p.SyncMode,
                LastSyncAt = p.LastSyncAt,
                LastSyncStatus = p.LastSyncStatus,
                SyncWarning = p.SyncWarning,
                TargetDirectory = p.TargetDirectory,
                ArchiveDirectory = p.ArchiveDirectory,
                ArchiveFormat = p.ArchiveFormat,
                PreferredFormat = p.PreferredFormat,
                StrictQuality = p.StrictQuality,
                AllowContainerChange = p.AllowContainerChange,
                ArchiveEnabled = p.ArchiveEnabled,
                LastAttemptAt = p.LastAttemptAt,
                IsPinned = p.IsPinned,
                Tracks = p.Tracks
                    .OrderBy(t => t.Position)
                    .Select(t => new TrackEntity
                    {
                        Id = t.Id,
                        MelodyId = t.MelodyId,
                        ExternalId = t.ExternalId,
                        Title = t.Title,
                        Artist = t.Artist,
                        Album = t.Album,
                        MediaType = t.MediaType,
                        Bitrate = t.Bitrate,
                        SampleRateHz = t.SampleRateHz,
                        FileSizeBytes = t.FileSizeBytes,
                        DurationMs = t.DurationMs,
                        Position = t.Position,
                        CurrentPath = t.CurrentPath,
                        ArchivePath = t.ArchivePath,
                        DownloadStatus = t.DownloadStatus,
                        DownloadError = t.DownloadError,
                        Warning = t.Warning,
                        SourcePluginId = t.SourcePluginId,
                        ReviewKind = t.ReviewKind,
                        ReviewText = t.ReviewText,
                        IsHardLink = t.IsHardLink,
                        SkipDedup = t.SkipDedup,
                        PlaylistEntityId = t.PlaylistEntityId,
                    })
                    .ToList(),
            })
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>
    /// Removes a playlist and its snapshot. When deleteFiles is set, the
    /// music files on disk go too (each track's primary download and its
    /// archive copy); otherwise only the database rows change.
    /// Hard link aware like RemoveTrackAsync: a path another playlist's
    /// row still references is a shared name and survives, keeping that
    /// playlist's file and database entry intact.
    /// </summary>
    public async Task<bool> DeleteAsync(string playlistId, bool deleteFiles = false, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Playlists
            .Include(p => p.Tracks)
            .FirstOrDefaultAsync(p => p.Id == playlistId, ct);
        if (entity is null) return false;

        if (deleteFiles)
        {
            // Paths other rows (in any playlist) still reference, as
            // primary or archive file, are shared names: only this
            // playlist's rows disappear.
            var ownIds = entity.Tracks.Select(t => t.Id).ToHashSet();
            var shared = (await db.Tracks.AsNoTracking()
                    .Where(t => !ownIds.Contains(t.Id)
                        && (t.CurrentPath != null || t.ArchivePath != null))
                    .Select(t => new { t.CurrentPath, t.ArchivePath })
                    .ToListAsync(ct))
                .SelectMany(t => new[] { t.CurrentPath, t.ArchivePath })
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var path in OwnPaths(entity.Tracks))
            {
                if (shared.Contains(path)) continue; // another playlist still holds this name
                try { System.IO.File.Delete(path); } catch { /* best effort */ }
            }
        }

        db.Tracks.RemoveRange(entity.Tracks);
        db.Playlists.Remove(entity);
        await db.SaveChangesAsync(ct);
        _changes.Bump();
        return true;
    }

    public async Task UpdateSettingsAsync(string playlistId, string? name, bool autoSync, int? intervalMinutes, string? targetDirectory, CancellationToken ct = default)
        => await UpdateSettingsAsync(playlistId, name, autoSync, intervalMinutes, targetDirectory, null, null, ct);

    public async Task UpdateSettingsAsync(string playlistId, string? name, bool autoSync, int? intervalMinutes, string? targetDirectory, PlaylistSyncMode? syncMode, string? preferredFormat = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Playlists.FindAsync(new object[] { playlistId }, ct)
            ?? throw new InvalidOperationException($"Playlist '{playlistId}' not found");
        if (name is { Length: > 0 } n) entity.Name = n;
        entity.AutoSyncEnabled = autoSync;
        entity.AutoSyncIntervalMinutes = autoSync ? intervalMinutes : null;
        // Empty clears the per-playlist override, null leaves it alone.
        if (targetDirectory is not null)
            entity.TargetDirectory = targetDirectory.Length == 0 ? null : targetDirectory;
        if (syncMode is not null) entity.SyncMode = syncMode.Value.ToString();
        if (preferredFormat is { Length: > 0 } fmt)
            entity.PreferredFormat = PlaylistStore.IsValidFormat(fmt) ? fmt : "auto";
        await db.SaveChangesAsync(ct);
        _changes.Bump();
    }

    /// <summary>
    /// Schedule-based variant: the schedule string (empty or a cron
    /// expression, as ScanSchedule stores it) is the single source of
    /// truth; the legacy boolean and minutes columns are kept in sync so
    /// nothing reading them sees a lie.
    /// </summary>
    public async Task UpdateScheduleAsync(
        string playlistId, string? name, ScanSchedule schedule,
        string? targetDirectory = null, PlaylistSyncMode? syncMode = null,
        string? preferredFormat = null, string? archiveDirectory = null,
        string? archiveFormat = null, bool? autoDownload = null,
        bool? strictQuality = null, bool? allowContainerChange = null,
        bool? archiveEnabled = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Playlists.FindAsync(new object[] { playlistId }, ct)
            ?? throw new InvalidOperationException($"Playlist '{playlistId}' not found");
        if (name is { Length: > 0 } n) entity.Name = n;
        entity.ScheduleCron = schedule.ToString();
        entity.AutoSyncEnabled = schedule.Mode != ScanScheduleMode.Manual;
        entity.AutoSyncIntervalMinutes = schedule.Mode == ScanScheduleMode.Interval
            ? schedule.IntervalMinutes
            : null;
        // Empty clears the per-playlist override (the default music path
        // takes over again), null leaves the stored value.
        if (targetDirectory is not null)
            entity.TargetDirectory = targetDirectory.Length == 0 ? null : targetDirectory;
        if (syncMode is not null) entity.SyncMode = syncMode.Value.ToString();
        if (preferredFormat is { Length: > 0 } fmt)
            entity.PreferredFormat = PlaylistStore.IsValidFormat(fmt) ? fmt : "auto";
        // Archive copy: empty string clears the override (the global
        // default takes over again), null leaves the stored value. The
        // same rule as targetDirectory above. "default" is the explicit
        // UI form of clearing; "auto" means copy as-is for this playlist.
        if (archiveDirectory is not null)
            entity.ArchiveDirectory = archiveDirectory.Length == 0 ? null : archiveDirectory;
        if (archiveFormat is not null)
            entity.ArchiveFormat = archiveFormat.Length == 0 || archiveFormat == "default"
                ? null
                : archiveFormat;
        // Per-playlist archive switch: null leaves the stored value.
        if (archiveEnabled is { } archiveOn)
            entity.ArchiveEnabled = archiveOn;
        // Auto-download toggle: null leaves the stored value, so callers
        // that only touch the schedule never reset it.
        if (autoDownload is { } ad)
            entity.AutoDownloadEnabled = ad;
        // Quality strictness: null leaves the stored value too.
        if (strictQuality is { } strict)
            entity.StrictQuality = strict;
        if (allowContainerChange is { } change)
            entity.AllowContainerChange = change;
        await db.SaveChangesAsync(ct);
        _changes.Bump();
    }

    /// <summary>
    /// Stamps the auto-sync attempt. Called after every try, success or
    /// failure, so a failing source waits for its next interval.
    /// </summary>
    public async Task RecordSyncAttemptAsync(string playlistId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Playlists.FirstOrDefaultAsync(p => p.Id == playlistId, ct);
        if (row is null) return;
        row.LastAttemptAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        _changes.Bump();
    }

    /// <summary>
    /// All playlists whose auto-sync schedule is due. The schedule is the
    /// shared ScanSchedule string; the legacy boolean is still honoured for
    /// rows that predate it (converted by the schema patcher, but a row
    /// saved by an older build in between gets one more interval run).
    /// Used by the background scheduler.
    /// </summary>
    public async Task<List<PlaylistEntity>> GetDueForAutoSyncAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var candidates = await db.Playlists
            .AsNoTracking()
            .Where(p => (p.ScheduleCron != null && p.ScheduleCron != ""
                        || p.AutoSyncEnabled)
                        // File imports have no live source to pull: never due.
                        && !p.SourceUrl.StartsWith("spotify:import:"))
            .ToListAsync(ct);

        return candidates
            .Where(p =>
            {
                var schedule = p.ScheduleCron is { Length: > 0 }
                    ? ScanSchedule.Parse(p.ScheduleCron)
                    : ScanSchedule.FromInterval(p.AutoSyncIntervalMinutes ?? 60);
                // A failed attempt stamps LastAttemptAt: the playlist then
                // waits for the next interval instead of retrying every
                // minute. Never attempted: due immediately.
                var last = (p.LastAttemptAt ?? p.LastSyncAt) is { } at
                    ? new DateTimeOffset(at, TimeSpan.Zero)
                    : DateTimeOffset.MinValue;
                return schedule.IsDue(last, now);
            })
            .ToList();
    }

    /// <summary>
    /// Inserts a ScanLocation for <paramref name="path"/> when none exists
    /// (case-insensitive, no duplicates). Manual defaults: live monitoring
    /// off (it spams rescans on big folders), no interval, no cron.
    /// </summary>
    internal static async Task EnsureScanLocationAsync(MelodyBridgeDbContext db, string path, CancellationToken ct)
    {
        var exists = await db.ScanLocations
            .AnyAsync(l => l.Path.ToLower() == path.ToLower(), ct);
        if (!exists)
            db.ScanLocations.Add(new ScanLocationEntity
            {
                Path = path,
                LiveMonitoring = false,
                ScheduleCron = null,
                ScanIntervalHours = null,
            });
    }

    /// <summary>
    /// Fills missing tags on already-downloaded files: the cover art from the
    /// row's CoverUrl, the lyrics (resolved from LRCLIB and stored when the
    /// row has none), the release credit and the album name. Only missing
    /// fields are written, so art and lyrics a plugin embedded are never
    /// overwritten. One unreadable or vanished file counts as failed and never
    /// stops the run. Returns (updated, skipped, failed).
    /// </summary>
    public async Task<(int updated, int skipped, int failed)> RetagLibraryAsync(CancellationToken ct = default)
    {
        List<TrackEntity> rows;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            rows = await db.Tracks.AsNoTracking()
                .Where(t => t.DownloadStatus == "downloaded"
                    && t.CurrentPath != null
                    && (t.CoverUrl != null || t.Album != null || t.AlbumArtist != null
                        || (t.Lyrics == null && t.Title != null)))
                .ToListAsync(ct);
        }

        var updated = 0;
        var skipped = 0;
        var failed = 0;
        // Bounded parallelism: the lyrics resolver paces its own requests and
        // cover art is cached per album, so a handful of workers is plenty and
        // keeps a large library from opening hundreds of file handles.
        await Parallel.ForEachAsync(rows,
            new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (row, token) =>
            {
                try
                {
                    if (!File.Exists(row.CurrentPath))
                    {
                        Interlocked.Increment(ref failed);
                        return;
                    }

                    if (await RetagRowAsync(row, token))
                        Interlocked.Increment(ref updated);
                    else
                        Interlocked.Increment(ref skipped);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Re-tag failed for '{Title}'", row.Title);
                    Interlocked.Increment(ref failed);
                }
            });

        _logger.LogInformation(
            "Re-tag: {Updated} file(s) updated, {Skipped} already complete, {Failed} failed",
            updated, skipped, failed);
        return (updated, skipped, failed);
    }

    /// <summary>
    /// One row's re-tag: what the file is missing decides what is fetched.
    /// Returns true when the file changed.
    /// </summary>
    private async Task<bool> RetagRowAsync(TrackEntity row, CancellationToken ct)
    {
        var path = row.CurrentPath!;
        var (needsAlbum, needsAlbumArtist, needsLyrics, needsCover) = PlaylistRules.MissingTags(path);

        var cover = needsCover && _coverArt is not null && !string.IsNullOrWhiteSpace(row.CoverUrl)
            ? await _coverArt.FetchAsync(row.CoverUrl, ct)
            : null;

        var lyrics = row.Lyrics;
        if (needsLyrics && string.IsNullOrWhiteSpace(lyrics) && _lyrics is not null
            && !string.IsNullOrWhiteSpace(row.Title))
        {
            lyrics = await _lyrics.ResolveAsync(
                row.Artist, row.Title, row.Album,
                row.DurationMs is > 0 ? TimeSpan.FromMilliseconds(row.DurationMs.Value) : null,
                ct);
            if (!string.IsNullOrWhiteSpace(lyrics))
                await StoreLyricsAsync(row.Id, lyrics, ct);
        }

        var changeAlbum = needsAlbum && !string.IsNullOrWhiteSpace(row.Album);
        var changeAlbumArtist = needsAlbumArtist && !string.IsNullOrWhiteSpace(row.AlbumArtist);
        var changeLyrics = needsLyrics && !string.IsNullOrWhiteSpace(lyrics);
        var changeCover = needsCover && cover is { Length: > 0 };
        if (!changeAlbum && !changeAlbumArtist && !changeLyrics && !changeCover)
            return false;

        Tagging.TaglibHelper.WriteTags(path,
            album: changeAlbum ? row.Album : null,
            albumArtist: changeAlbumArtist ? row.AlbumArtist : null,
            coverArt: changeCover ? cover : null,
            lyrics: changeLyrics ? lyrics : null);
        return true;
    }

    /// <summary>Persists a resolved lyrics text on the row, without a shared context.</summary>
    private async Task StoreLyricsAsync(int trackId, string lyrics, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await db.Tracks
            .Where(t => t.Id == trackId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Lyrics, lyrics), ct);
    }

    /// <summary>
    /// The library maintenance pass: every downloaded row gets its audio
    /// facts backfilled when missing (bitrate, sample rate, file size),
    /// the spectrum verdict is re-run so a check that ran with older rules
    /// (or was skipped) can flag a file now, and a row whose file vanished
    /// is queued for a re-download. Returns (recomputed, missing, flagged):
    /// files read, files gone, and rows that ended the pass with a fresh
    /// quality review.
    ///
    /// The work runs in small batches off the circuit, each with its own
    /// DbContext and only ids carried across: a big library does not block
    /// the UI for minutes, and a download that lands between batches is
    /// seen as it is instead of being overwritten from a stale snapshot.
    /// </summary>
    public async Task<(int recomputed, int missing, int flagged)> RecomputeMissingFactsAsync(
        CancellationToken ct = default)
    {
        List<int> ids;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            ids = await db.Tracks
                .Where(t => t.DownloadStatus == "downloaded" && t.CurrentPath != null)
                .Select(t => t.Id)
                .ToListAsync(ct);
        }

        var verification = _verification();
        var recomputed = 0;
        var missing = 0;
        var flagged = 0;

        foreach (var batch in ids.Chunk(25))
        {
            ct.ThrowIfCancellationRequested();
            var batchIds = batch.ToList();
            var counts = await Task.Run(async () =>
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var rows = await db.Tracks
                    .Where(t => batchIds.Contains(t.Id))
                    .ToListAsync(ct);

                var localRecomputed = 0;
                var localMissing = 0;
                var localFlagged = 0;
                foreach (var track in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!File.Exists(track.CurrentPath))
                    {
                        // Same wording LibraryReconciler uses: the UI explains it once.
                        track.DownloadStatus = "pending";
                        track.Warning = "file missing on disk, will re-download";
                        track.ReviewKind = null;
                        track.ReviewText = null;
                        track.SpectrumAccepted = false;
                        track.SourcePluginId = null;
                        localMissing++;
                        continue;
                    }

                    if (track.Bitrate == null || track.SampleRateHz == null || track.FileSizeBytes == null)
                    {
                        track.Bitrate = Audio.BitrateProbe.MeasureKbps(track.CurrentPath!);
                        AudioProbe.Fill(track, track.CurrentPath!);
                        localRecomputed++;
                    }

                    if (ApplySpectrumVerdict(track, verification)) localFlagged++;
                }

                if (rows.Count > 0) await db.SaveChangesAsync(ct);
                return (localRecomputed, localMissing, localFlagged);
            }, ct);

            recomputed += counts.Item1;
            missing += counts.Item2;
            flagged += counts.Item3;
        }

        if (ids.Count > 0) _changes.Bump();
        return (recomputed, missing, flagged);
    }

    /// <summary>
    /// Re-runs the spectrum verdict for one downloaded row: an inflated
    /// file gains the spectrum review, a stale one is cleared when the
    /// current file no longer looks inflated, and the informational hint
    /// is refreshed. Other warning segments (an archive failure, a dedup
    /// note) are never touched. A file the user settled with Keep is not
    /// reopened for the same verdict. Returns true when the row ends with
    /// the spectrum review open.
    /// </summary>
    private static bool ApplySpectrumVerdict(TrackEntity track, Audio.VerificationOptions verification)
    {
        var result = Audio.SpectrumAnalyzer.Verify(
            track.CurrentPath!, verification.Spectrum, verification.SpectrumClassGap);
        if (result is null) return TrackReview.Has(track.ReviewKind, TrackReview.Spectrum);

        var accepted = track.SpectrumAccepted;
        if (!result.LooksInflated) track.SpectrumAccepted = false;

        // Rebuild only the spectrum parts: the length review, the dedup
        // warning and everything else stay exactly as they were.
        var kinds = (track.ReviewKind ?? "")
            .Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => k != TrackReview.Spectrum)
            .ToList();
        var texts = (track.ReviewText ?? "")
            .Split("; ", StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.StartsWith("bitrate looks inflated:", StringComparison.Ordinal))
            .ToList();

        if (result.LooksInflated && !accepted)
        {
            kinds.Add(TrackReview.Spectrum);
            texts.Add($"bitrate looks inflated: {result.Note}");
        }

        track.ReviewKind = kinds.Count == 0 ? null : TrackReview.Join(kinds);
        track.ReviewText = texts.Count == 0 ? null : string.Join("; ", texts);

        var warnings = (track.Warning ?? "")
            .Split("; ", StringSplitOptions.RemoveEmptyEntries)
            // Drop both the advisory hint and a legacy claim an older
            // checker wrote into the warning: the fresh verdict decides
            // what stays on the row.
            .Where(w => !w.StartsWith("quality hint:", StringComparison.Ordinal)
                     && !w.StartsWith("bitrate looks inflated:", StringComparison.Ordinal))
            .ToList();
        if (!result.LooksInflated && result.CodecCutoff && result.EffectiveKbpsClass is > 0 and < 320)
            warnings.Add($"quality hint: {result.Note}");
        track.Warning = warnings.Count == 0 ? null : string.Join("; ", warnings);

        return result.LooksInflated && !accepted;
    }
}
