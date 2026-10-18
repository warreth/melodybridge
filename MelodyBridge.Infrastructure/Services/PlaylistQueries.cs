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

    public PlaylistQueries(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        SettingsStore settings,
        CoverArtFetcher? coverArt,
        LyricsResolver? lyrics,
        ILogger logger,
        DataChangeTracker? changes = null)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _coverArt = coverArt;
        _lyrics = lyrics;
        _logger = logger;
        _changes = changes ?? new DataChangeTracker();
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
    public async Task<Dictionary<string, (int downloaded, int failed, int total, long sizeBytes)>> GetDownloadStatsAsync(CancellationToken ct = default)
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
            })
            .ToListAsync(ct);

        return rows
            .Where(r => r.Id is not null)
            .ToDictionary(r => r.Id!, r => (r.Downloaded, r.Failed, r.Total, r.SizeBytes));
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

    /// <summary>One persisted playlist including its current track snapshot.</summary>
    public async Task<PlaylistEntity?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Playlists
            .Include(p => p.Tracks.OrderBy(t => t.Position))
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct);
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
                var last = p.LastSyncAt is { } at ? new DateTimeOffset(at, TimeSpan.Zero)
                    // Never synced: due immediately so the first run happens.
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
    /// Backfills audio facts (bitrate, sample rate, file size) on downloaded
    /// tracks from before those columns existed, and flags downloaded tracks
    /// whose file vanished as pending so the next download run picks them up.
    /// Returns (recomputed, missing).
    /// </summary>
    public async Task<(int recomputed, int missing)> RecomputeMissingFactsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var stale = await db.Tracks
            .Where(t => t.DownloadStatus == "downloaded"
                && (t.Bitrate == null || t.SampleRateHz == null || t.FileSizeBytes == null)
                && t.CurrentPath != null)
            .ToListAsync(ct);

        var recomputed = 0;
        var missing = 0;
        foreach (var track in stale)
        {
            if (!File.Exists(track.CurrentPath))
            {
                // Same wording LibraryReconciler uses: the UI explains it once.
                track.DownloadStatus = "pending";
                track.Warning = "file missing on disk, will re-download";
                track.ReviewKind = null;
                track.ReviewText = null;
                track.SourcePluginId = null;
                missing++;
                continue;
            }

            track.Bitrate = Audio.BitrateProbe.MeasureKbps(track.CurrentPath!);
            AudioProbe.Fill(track, track.CurrentPath!);
            recomputed++;
        }

        if (recomputed > 0 || missing > 0)
            await db.SaveChangesAsync(ct);
        return (recomputed, missing);
    }
}
