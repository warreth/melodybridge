using System.Diagnostics;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Writes and repairs the named extra archive copies. The original single
/// archive (Tracks.ArchivePath) is still owned by TrackLinker; this service
/// adds one copy per enabled profile and keeps those rows true when files
/// move or disappear. A failed profile copy is a warning, never a failed
/// download.
/// </summary>
public sealed class ArchiveCopyService
{
    private const string WarningPrefix = "archive copy failed";
    private const string UnverifiedPrefix = "archive copy unverified";
    private static readonly HardLinkService HardLinks = new();

    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly SettingsStore _settings;
    private readonly ILogger<ArchiveCopyService> _logger;
    private readonly DownloadThrottle? _throttle;

    public ArchiveCopyService(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        SettingsStore settings,
        ILogger<ArchiveCopyService> logger,
        DownloadThrottle? throttle = null)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _logger = logger;
        _throttle = throttle;
    }

    public sealed record BackfillProgress(
        int Total, int Done, int Failed, string? Current, bool Running, string? Message,
        TimeSpan? Eta = null);

    /// <summary>
    /// Ensures every selected profile has a copy for this track. The rows
    /// are saved before the per-track lock is released, so a concurrent
    /// writer sees them; the caller can still save its own changes in the
    /// same context. Returns false when the playlist does not use the
    /// requested profile, so a one-profile backfill can tell "not selected"
    /// from "written".
    /// </summary>
    public async Task<bool> ArchiveExtraCopiesAsync(
        TrackEntity track, MelodyBridgeDbContext db,
        string? onlyProfileId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(track.CurrentPath) || !File.Exists(track.CurrentPath))
            return false;

        var playlist = track.PlaylistEntityId is { Length: > 0 } playlistId
            ? await db.Playlists.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playlistId, ct)
            : null;
        var selection = PlaylistArchiveSelection.Parse(playlist?.ArchiveProfileIds);
        if (selection is null && playlist is { ArchiveEnabled: false })
        {
            // A one-profile backfill only owns that profile's row, and this
            // playlist writes no archive copies at all.
            if (onlyProfileId is not null)
                return false;
            await ClearRowsAsync(track.Id, db, ct);
            await db.SaveChangesAsync(ct);
            return true;
        }

        // A one-profile backfill for a profile this playlist does not use
        // must be a no-op. Otherwise the empty profile list below would be
        // mistaken for "no profiles at all" and wipe the selected rows.
        if (onlyProfileId is not null
            && selection is not null
            && !selection.Contains(onlyProfileId, StringComparer.Ordinal))
            return false;

        var profiles = await db.ArchiveProfiles.AsNoTracking()
            .Where(profile => profile.Enabled
                && (onlyProfileId == null || profile.Id == onlyProfileId))
            .OrderBy(profile => profile.Priority)
            .ThenBy(profile => profile.Name)
            .ToListAsync(ct);
        if (selection is not null)
            profiles = profiles
                .Where(profile => selection.Contains(profile.Id, StringComparer.Ordinal))
                .ToList();
        var existingRows = await db.TrackArchiveCopies
            .Where(copy => copy.TrackId == track.Id)
            .ToListAsync(ct);

        // An explicit playlist selection owns the profile rows. Drop rows
        // for profiles that are no longer selected, but keep their files:
        // changing the configuration must not delete a library.
        if (selection is not null && onlyProfileId is null)
        {
            var allowed = profiles.Select(profile => profile.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stale = existingRows
                .Where(row => !allowed.Contains(row.ProfileId))
                .ToList();
            if (stale.Count > 0)
            {
                await ClearWarningsForProfilesAsync(
                    db, track, stale.Select(row => row.ProfileId), ct);
                db.TrackArchiveCopies.RemoveRange(stale);
                existingRows.RemoveAll(row => !allowed.Contains(row.ProfileId));
            }
        }

        if (profiles.Count == 0)
        {
            // A deleted profile must not leave dead rows behind. A
            // one-profile backfill only touches that profile's row.
            var stale = onlyProfileId is null
                ? existingRows
                : existingRows.Where(row => string.Equals(
                    row.ProfileId, onlyProfileId, StringComparison.OrdinalIgnoreCase)).ToList();
            await ClearWarningsForProfilesAsync(
                db, track, stale.Select(row => row.ProfileId), ct);
            db.TrackArchiveCopies.RemoveRange(stale);
            await db.SaveChangesAsync(ct);
            return true;
        }

        var playlistFolders = await _settings.GetBoolAsync("playlist_folders", true, ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            ct.ThrowIfCancellationRequested();
            // One row per track and profile. This lock serializes the
            // row creation and save for every branch, including the
            // error branches that have no target path.
            using var rowLock = await PathLockTable.Shared.AcquireAsync(
                RowKey(track.Id, profile.Id), foldCase: false, ct);
            var folder = PlaylistRules.ResolveArchiveFolder(
                null, profile.RootPath, playlist?.Name, playlistFolders);
            if (folder.Length == 0)
            {
                var failed = await EnsureRowAsync(db, existingRows, track, profile, ct);
                Fail(failed, track, profile, "the archive folder is empty");
                await db.SaveChangesAsync(ct);
                continue;
            }

            var target = ArchiveCopier.TargetPath(track.CurrentPath, folder, profile.Format);
            if (!seen.Add(target))
            {
                var failed = await EnsureRowAsync(db, existingRows, track, profile, ct);
                Fail(failed, track, profile, "another profile already writes this file");
                await db.SaveChangesAsync(ct);
                continue;
            }

            // One writer per name, shared with the legacy archive path and
            // the download workers. The name check, the copy, the row
            // update and the row save must see each other.
            using var targetLock = await PathLockTable.Shared.AcquireAsync(target, ct);

            // The row is created under the row lock: a concurrent writer
            // for the same track and profile finds it instead of adding a
            // second row that the unique index would reject.
            var row = await EnsureRowAsync(db, existingRows, track, profile, ct);

            if (row.Path is { Length: > 0 } current
                && PlaylistRules.PathEquals(current, target)
                && string.IsNullOrEmpty(row.Warning)
                && File.Exists(current)
                && new FileInfo(current).Length > 0)
            {
                row.SizeBytes = new FileInfo(current).Length;
                row.Warning = null;
                row.UpdatedAt = DateTime.UtcNow;
                ClearWarning(track, profile.Name);
            }
            else if (!await NameIsFreeAsync(db, track, target, ct))
            {
                Fail(row, track, profile,
                    "another track already owns this archive file");
            }
            else
            {
                var adopted = false;
                // The same song already wrote this file (a duplicate
                // playlist row or a hardlink twin). Adopt it instead of
                // transcoding the same bytes again, but check it first: a
                // row with a warning is not adopted, and an unverified
                // file stays unverified on the adopting row too.
                if (string.IsNullOrEmpty(row.Warning) && File.Exists(target))
                {
                    var existingId = TaglibHelper.ReadMelodyId(target);
                    if (!string.IsNullOrEmpty(existingId)
                        && (string.Equals(existingId, track.MelodyId, StringComparison.Ordinal)
                            || await TrackFileNamePolicy.IsTwinCopyAsync(
                                db, track, existingId, HardLinks, ct)))
                    {
                        var integrity = VerifyCopy(track, target);
                        if (integrity.Ok)
                        {
                            Adopt(row, track, profile, target, null);
                            adopted = true;
                        }
                        else if (integrity.ToolMissing || integrity.ToolFailed)
                        {
                            Adopt(row, track, profile, target,
                                $"could not verify the archive copy: {integrity.Reason}");
                            adopted = true;
                        }
                        // A real defect falls through to a rebuild.
                    }
                }

                if (!adopted)
                {
                    var result = ArchiveCopier.Copy(
                        track.CurrentPath, folder, profile.Format,
                        melodyId: track.MelodyId,
                        title: track.Title, artist: track.Artist, album: track.Album,
                        trackNumber: (uint?)(track.Position + 1),
                        expectedDuration: track.DurationMs is > 0
                            ? TimeSpan.FromMilliseconds(track.DurationMs.Value)
                            : null);

                    if (result.Ok)
                    {
                        row.Path = result.ArchivePath;
                        row.SizeBytes = new FileInfo(result.ArchivePath).Length;
                        row.UpdatedAt = DateTime.UtcNow;
                        if (result.Unverified)
                        {
                            // The file is there but ffprobe could not
                            // check it. Keep the row and name the state,
                            // so the next backfill checks it again
                            // instead of trusting it.
                            row.Warning = result.Reason ?? "could not verify the copy";
                            WarnUnverified(track, profile.Name, row.Warning);
                        }
                        else
                        {
                            row.Warning = null;
                            ClearWarning(track, profile.Name);
                        }
                    }
                    else
                    {
                        // Keep an earlier copy that is still on disk;
                        // otherwise the row points nowhere and the next
                        // attempt starts clean.
                        var previous = new[] { result.ArchivePath, row.Path }
                            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)
                                && File.Exists(path)
                                && !PlaylistRules.PathEquals(path, track.CurrentPath));
                        row.Path = previous;
                        row.SizeBytes = previous is null ? 0 : new FileInfo(previous).Length;
                        Fail(row, track, profile, result.Reason ?? "unknown error");
                    }
                }
            }

            // Saved under the row lock, so the next writer sees the row
            // instead of creating a duplicate.
            await db.SaveChangesAsync(ct);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Removes extra copy rows and, when asked, their files.</summary>
    public async Task<int> DeleteForTrackAsync(
        int trackId, MelodyBridgeDbContext db, bool deleteFiles, CancellationToken ct = default)
    {
        var copies = await db.TrackArchiveCopies
            .Where(copy => copy.TrackId == trackId)
            .ToListAsync(ct);
        var removed = 0;
        foreach (var copy in copies)
        {
            if (deleteFiles && copy.Path is { Length: > 0 } path
                && File.Exists(path)
                && !await OwnedElsewhereAsync(db, trackId, path, ct))
            {
                try { File.Delete(path); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not delete extra archive {Path}", path);
                }
            }
            db.TrackArchiveCopies.Remove(copy);
            removed++;
        }
        return removed;
    }

    /// <summary>Follows a primary rename in every extra archive folder.</summary>
    public async Task RenameForTrackAsync(
        int trackId, string newPrimaryPath, MelodyBridgeDbContext db, CancellationToken ct = default)
    {
        var copies = await db.TrackArchiveCopies
            .Where(copy => copy.TrackId == trackId && copy.Path != null)
            .ToListAsync(ct);
        foreach (var copy in copies)
        {
            if (copy.Path is not { Length: > 0 } path) continue;
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory)) continue;

            var baseName = Path.GetFileNameWithoutExtension(newPrimaryPath) + Path.GetExtension(path);
            var target = Path.Combine(directory, baseName);
            if (PlaylistRules.PathEquals(target, path)) continue;

            if (File.Exists(path) && !PlaylistRules.PathEquals(path, newPrimaryPath))
            {
                if (File.Exists(target)) target = FileMove.UniqueTarget(directory, baseName);
                FileMove.MoveAcrossDevices(path, target);
                copy.SizeBytes = new FileInfo(target).Length;
            }

            copy.Path = target;
            copy.UpdatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>Moves every extra copy when a profile root changes.</summary>
    public async Task MoveRootAsync(
        string oldRoot, string newRoot, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(oldRoot) || string.IsNullOrWhiteSpace(newRoot)) return;
        var oldFull = Path.GetFullPath(oldRoot);
        var newFull = Path.GetFullPath(newRoot);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var copies = await db.TrackArchiveCopies
            .Where(copy => copy.Path != null)
            .ToListAsync(ct);
        var moved = 0;
        foreach (var copy in copies)
        {
            if (copy.Path is not { Length: > 0 } path) continue;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { continue; }
            if (!IsUnder(full, oldFull)) continue;

            var relative = Path.GetRelativePath(oldFull, full);
            var target = Path.Combine(newFull, relative);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(full) && !PlaylistRules.PathEquals(full, target))
                {
                    if (File.Exists(target))
                        target = FileMove.UniqueTarget(Path.GetDirectoryName(target)!, Path.GetFileName(target));
                    FileMove.MoveAcrossDevices(full, target);
                    copy.SizeBytes = new FileInfo(target).Length;
                    moved++;
                }
                copy.Path = target;
                copy.UpdatedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                copy.Warning = "archive move failed: " + ex.Message;
                _logger.LogWarning(ex, "Could not move extra archive {Path}", full);
            }
        }
        await db.SaveChangesAsync(ct);
        if (moved > 0)
            _logger.LogInformation("Moved {Count} extra archive copies to {Root}", moved, newFull);
    }

    /// <summary>Builds every missing copy for one profile.</summary>
    /// <remarks>
    /// Only tracks that need work are visited: a copy that is already on
    /// disk is skipped before any context is opened, and copies for
    /// different files run in parallel. Two tracks that would write the
    /// same archive file are serialized on that target, so the name check
    /// and the write still see each other.
    /// </remarks>
    public async Task BuildMissingAsync(
        string profileId,
        IProgress<BackfillProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArchiveProfileEntity? profile;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            profile = await db.ArchiveProfiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == profileId, ct);
        }
        if (profile is null) return;

        var candidates = await MissingCandidatesAsync(profile, ct);
        var total = candidates.Count;
        var done = 0;
        var failed = 0;
        var started = Stopwatch.StartNew();

        void Report(string? current, bool running, string? message)
        {
            var finished = Volatile.Read(ref done);
            TimeSpan? eta = null;
            if (running && finished > 0 && total > finished)
            {
                var perItem = started.Elapsed.Ticks / (double)finished;
                eta = TimeSpan.FromTicks((long)(perItem * (total - finished)));
            }
            progress?.Report(new BackfillProgress(
                total, finished, Volatile.Read(ref failed), current, running, message, eta));
        }

        if (total == 0)
        {
            Report(null, false, "Every archive copy is present.");
            return;
        }

        Report(null, true, "Preparing…");
        var lastUiReport = Stopwatch.GetTimestamp();
        // With a shared throttle, downloads and the backfill compete for
        // the same slots: the setting bounds the whole box, not each pass.
        // Without one (tests, direct callers), keep the old setting cap.
        var maxParallel = 8;
        if (_throttle is null)
        {
            var configured = await _settings.GetAsync(
                "download_max_concurrent", DownloadThrottle.DefaultLimit.ToString(), ct);
            maxParallel = int.TryParse(configured, out var parsed)
                ? Math.Clamp(parsed, 1, 8)
                : DownloadThrottle.DefaultLimit;
        }
        await Parallel.ForEachAsync(candidates,
            new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = ct },
            async (candidate, token) =>
            {
                IDisposable? slot = null;
                try
                {
                    if (_throttle is not null)
                        slot = await _throttle.AcquireAsync(token);
                    await using var db = await _dbFactory.CreateDbContextAsync(token);
                    var track = await db.Tracks.FirstOrDefaultAsync(t => t.Id == candidate.Id, token);
                    if (track is not null)
                    {
                        var applied = await ArchiveExtraCopiesAsync(
                            track, db, profileId, token);
                        if (applied)
                        {
                            var copy = await db.TrackArchiveCopies.AsNoTracking()
                                .FirstOrDefaultAsync(c => c.TrackId == candidate.Id
                                    && c.ProfileId == profileId, token);
                            if (copy?.Warning is { Length: > 0 } || copy is null)
                                Interlocked.Increment(ref failed);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    _logger.LogDebug(ex, "Archive backfill failed for track {Id}", candidate.Id);
                }
                finally
                {
                    slot?.Dispose();
                }

                var finished = Interlocked.Increment(ref done);
                var now = Stopwatch.GetTimestamp();
                if (finished == total
                    || now - Volatile.Read(ref lastUiReport) >= Stopwatch.Frequency / 4)
                {
                    Volatile.Write(ref lastUiReport, now);
                    Report(candidate.Title, true, null);
                }
            });

        Report(null, false,
            failed == 0 ? "Every archive copy is present." : $"{failed} copies need another try.");
    }

    private sealed record BackfillCandidate(int Id, string? Title);

    private static bool IsPresent(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Tracks whose primary file is on disk, whose playlist selects this
    /// profile, and whose copy is not already there. Reading the primary
    /// and copy paths once keeps a repeat pass cheap: nothing that is done
    /// is opened in a context again.
    /// </summary>
    private async Task<List<BackfillCandidate>> MissingCandidatesAsync(
        ArchiveProfileEntity profile, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var tracks = await db.Tracks.AsNoTracking()
            .Where(track => track.CurrentPath != null && track.CurrentPath != "")
            .Select(track => new
            {
                track.Id,
                track.Title,
                track.CurrentPath,
                track.PlaylistEntityId,
            })
            .OrderBy(track => track.Id)
            .ToListAsync(ct);
        if (tracks.Count == 0) return new List<BackfillCandidate>();

        var playlistRows = await db.Playlists.AsNoTracking()
            .Select(playlist => new
            {
                playlist.Id,
                playlist.Name,
                playlist.ArchiveProfileIds,
                playlist.ArchiveEnabled,
            })
            .ToListAsync(ct);
        var playlists = playlistRows.ToDictionary(playlist => playlist.Id, StringComparer.Ordinal);

        var copyRows = await db.TrackArchiveCopies.AsNoTracking()
            .Where(copy => copy.ProfileId == profile.Id)
            .Select(copy => new { copy.TrackId, copy.Path, copy.Warning })
            .ToListAsync(ct);
        var copies = copyRows.ToDictionary(copy => copy.TrackId);

        var playlistFolders = await _settings.GetBoolAsync("playlist_folders", true, ct);
        var candidates = new List<BackfillCandidate>();
        foreach (var track in tracks)
        {
            playlists.TryGetValue(track.PlaylistEntityId ?? "", out var playlist);
            var selection = PlaylistArchiveSelection.Parse(playlist?.ArchiveProfileIds);
            if (selection is null)
            {
                if (playlist is { ArchiveEnabled: false }) continue;
            }
            else if (!selection.Contains(profile.Id, StringComparer.Ordinal))
            {
                continue;
            }

            if (!File.Exists(track.CurrentPath)) continue;

            var folder = PlaylistRules.ResolveArchiveFolder(
                null, profile.RootPath, playlist?.Name, playlistFolders);
            var target = ArchiveCopier.TargetPath(track.CurrentPath!, folder, profile.Format);
            if (copies.TryGetValue(track.Id, out var copy)
                && copy.Path is { Length: > 0 } existing
                && PlaylistRules.PathEquals(existing, target)
                && string.IsNullOrEmpty(copy.Warning)
                && IsPresent(existing))
            {
                continue;
            }

            candidates.Add(new BackfillCandidate(track.Id, track.Title));
        }

        return candidates;
    }

    private static async Task ClearRowsAsync(
        int trackId, MelodyBridgeDbContext db, CancellationToken ct)
    {
        var rows = await db.TrackArchiveCopies
            .Where(copy => copy.TrackId == trackId)
            .ToListAsync(ct);
        db.TrackArchiveCopies.RemoveRange(rows);
    }

    private static Task<bool> NameIsFreeAsync(
        MelodyBridgeDbContext db, TrackEntity track, string path, CancellationToken ct)
        => TrackFileNamePolicy.IsFreeAsync(
            db, track, path, HardLinks, allowPrimaryOwner: false, ct);

    private static async Task<bool> OwnedElsewhereAsync(
        MelodyBridgeDbContext db, int trackId, string path, CancellationToken ct)
    {
        if (await db.TrackArchiveCopies.AsNoTracking().AnyAsync(
                copy => copy.TrackId != trackId && copy.Path == path, ct))
            return true;
        return await db.Tracks.AsNoTracking().AnyAsync(
            track => track.Id != trackId
                && (track.CurrentPath == path || track.ArchivePath == path), ct);
    }

    /// <summary>The lock key for one track/profile row, never a file path.</summary>
    private static string RowKey(int trackId, string profileId)
        => $"archive-row:{trackId}:{profileId}";

    /// <summary>
    /// The row for this track and profile, created under the row lock
    /// when it does not exist yet. A concurrent writer that already saved
    /// the row is found by the query instead of adding a second one.
    /// </summary>
    private static async Task<TrackArchiveCopyEntity> EnsureRowAsync(
        MelodyBridgeDbContext db,
        List<TrackArchiveCopyEntity> existingRows,
        TrackEntity track,
        ArchiveProfileEntity profile,
        CancellationToken ct)
    {
        var row = existingRows.FirstOrDefault(existing =>
            string.Equals(existing.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase));
        if (row is not null) return row;

        row = await db.TrackArchiveCopies.FirstOrDefaultAsync(
            copy => copy.TrackId == track.Id && copy.ProfileId == profile.Id, ct);
        if (row is not null)
        {
            existingRows.Add(row);
            return row;
        }

        row = new TrackArchiveCopyEntity
        {
            TrackId = track.Id,
            ProfileId = profile.Id,
        };
        db.TrackArchiveCopies.Add(row);
        existingRows.Add(row);
        return row;
    }

    /// <summary>
    /// Checks an archive file against its primary with the same window the
    /// copier uses: the primary's measured length is the yardstick.
    /// </summary>
    private static IntegrityResult VerifyCopy(TrackEntity track, string path)
    {
        var primaryDuration = FileIntegrity.MeasureDuration(track.CurrentPath!);
        var tolerance = primaryDuration is { } source
            ? TimeSpan.FromSeconds(Math.Max(2, source.TotalSeconds * 0.02))
            : (TimeSpan?)null;
        return FileIntegrity.Check(path, primaryDuration, tolerance);
    }

    private static void Adopt(
        TrackArchiveCopyEntity row, TrackEntity track, ArchiveProfileEntity profile,
        string target, string? warning)
    {
        row.Path = target;
        row.SizeBytes = new FileInfo(target).Length;
        row.Warning = warning;
        row.UpdatedAt = DateTime.UtcNow;
        if (warning is null)
            ClearWarning(track, profile.Name);
        else
            WarnUnverified(track, profile.Name, warning);
    }

    private static async Task ClearWarningsForProfilesAsync(
        MelodyBridgeDbContext db, TrackEntity track,
        IEnumerable<string> profileIds, CancellationToken ct)
    {
        var ids = profileIds.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (ids.Count == 0) return;
        var names = await db.ArchiveProfiles.AsNoTracking()
            .Where(profile => ids.Contains(profile.Id))
            .Select(profile => profile.Name)
            .ToListAsync(ct);
        foreach (var name in names)
            ClearWarning(track, name);
    }

    private static void Fail(
        TrackArchiveCopyEntity row, TrackEntity track, ArchiveProfileEntity profile, string reason)
    {
        row.Warning = reason;
        row.UpdatedAt = DateTime.UtcNow;
        ReplaceWarning(track, WarningPrefix, profile.Name, reason);
    }

    private static void WarnUnverified(TrackEntity track, string profileName, string reason)
        => ReplaceWarning(track, UnverifiedPrefix, profileName, reason);

    /// <summary>
    /// Replaces this profile's verdict on the track warning, whichever
    /// state it was in: a failed copy and an unverified copy are two
    /// answers to the same question.
    /// </summary>
    private static void ReplaceWarning(
        TrackEntity track, string prefix, string profileName, string reason)
    {
        var segment = $"{prefix} ({profileName}): {reason}";
        var segments = ProfileSegments(track, profileName).ToList();
        segments.Add(segment);
        track.Warning = string.Join("; ", segments);
    }

    internal static void ClearWarning(TrackEntity track, string profileName)
    {
        if (string.IsNullOrWhiteSpace(track.Warning)) return;
        var segments = ProfileSegments(track, profileName).ToList();
        track.Warning = segments.Count == 0 ? null : string.Join("; ", segments);
    }

    /// <summary>
    /// Every warning segment except this profile's own failed and
    /// unverified verdicts. Both belong to the same row, so a new answer
    /// replaces the old one.
    /// </summary>
    private static IEnumerable<string> ProfileSegments(TrackEntity track, string profileName)
    {
        var failed = $"{WarningPrefix} ({profileName}):";
        var unverified = $"{UnverifiedPrefix} ({profileName}):";
        return (track.Warning ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(existing => !existing.StartsWith(failed, StringComparison.Ordinal)
                && !existing.StartsWith(unverified, StringComparison.Ordinal));
    }

    private static bool IsUnder(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }
}
