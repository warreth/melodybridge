using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
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

    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly SettingsStore _settings;
    private readonly ILogger<ArchiveCopyService> _logger;

    public ArchiveCopyService(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        SettingsStore settings,
        ILogger<ArchiveCopyService> logger)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _logger = logger;
    }

    public sealed record BackfillProgress(
        int Total, int Done, int Failed, string? Current, bool Running, string? Message);

    /// <summary>
    /// Ensures every enabled profile has a copy for this track. The caller
    /// passes its own context so the track warning and copy rows persist in
    /// the same SaveChanges as the download; a backfill saves explicitly.
    /// </summary>
    public async Task ArchiveExtraCopiesAsync(
        TrackEntity track, MelodyBridgeDbContext db, bool save,
        string? onlyProfileId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(track.CurrentPath) || !File.Exists(track.CurrentPath))
            return;

        var playlist = track.PlaylistEntityId is { Length: > 0 } playlistId
            ? await db.Playlists.AsNoTracking().FirstOrDefaultAsync(p => p.Id == playlistId, ct)
            : null;
        var selection = PlaylistArchiveSelection.Parse(playlist?.ArchiveProfileIds);
        if (selection is null && playlist is { ArchiveEnabled: false })
        {
            await ClearRowsAsync(track.Id, db, ct);
            if (save) await db.SaveChangesAsync(ct);
            return;
        }

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
                db.TrackArchiveCopies.RemoveRange(stale);
                existingRows.RemoveAll(row => !allowed.Contains(row.ProfileId));
            }
        }

        if (profiles.Count == 0)
        {
            // A deleted profile must not leave dead rows behind.
            db.TrackArchiveCopies.RemoveRange(existingRows);
            if (save) await db.SaveChangesAsync(ct);
            return;
        }

        var playlistFolders = await _settings.GetBoolAsync("playlist_folders", true, ct);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            ct.ThrowIfCancellationRequested();
            var row = existingRows.FirstOrDefault(existing =>
                string.Equals(existing.ProfileId, profile.Id, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                row = new TrackArchiveCopyEntity
                {
                    TrackId = track.Id,
                    ProfileId = profile.Id,
                };
                db.TrackArchiveCopies.Add(row);
                existingRows.Add(row);
            }

            var folder = PlaylistRules.ResolveArchiveFolder(
                null, profile.RootPath, playlist?.Name, playlistFolders);
            if (folder.Length == 0)
            {
                Fail(row, track, profile, "the archive folder is empty");
                continue;
            }

            var target = ArchiveCopier.TargetPath(track.CurrentPath, folder, profile.Format);
            if (!seen.Add(target))
            {
                Fail(row, track, profile, "another profile already writes this file");
                continue;
            }

            if (row.Path is { Length: > 0 } current
                && PlaylistRules.PathEquals(current, target)
                && File.Exists(current)
                && new FileInfo(current).Length > 0)
            {
                row.SizeBytes = new FileInfo(current).Length;
                row.Warning = null;
                row.UpdatedAt = DateTime.UtcNow;
                ClearWarning(track, profile.Name);
                continue;
            }

            if (!await NameIsFreeAsync(db, track.Id, target, ct))
            {
                Fail(row, track, profile,
                    "another track already owns this archive file");
                continue;
            }

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
                row.Warning = null;
                row.UpdatedAt = DateTime.UtcNow;
                ClearWarning(track, profile.Name);
                continue;
            }

            // Keep an earlier copy that is still on disk; otherwise the row
            // points nowhere and the next attempt starts clean.
            var previous = new[] { result.ArchivePath, row.Path }
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)
                    && File.Exists(path)
                    && !PlaylistRules.PathEquals(path, track.CurrentPath));
            row.Path = previous;
            row.SizeBytes = previous is null ? 0 : new FileInfo(previous).Length;
            Fail(row, track, profile, result.Reason ?? "unknown error");
        }

        if (save) await db.SaveChangesAsync(ct);
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

    /// <summary>Builds every missing copy for one profile, one track at a time.</summary>
    public async Task BuildMissingAsync(
        string profileId,
        IProgress<BackfillProgress>? progress = null,
        CancellationToken ct = default)
    {
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var profile = await db.ArchiveProfiles.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == profileId, ct);
            if (profile is null) return;
        }

        var ids = await TrackIdsAsync(ct);
        var done = 0;
        var failed = 0;
        foreach (var id in ids)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var track = await db.Tracks.FirstOrDefaultAsync(t => t.Id == id, ct);
                if (track is null || string.IsNullOrWhiteSpace(track.CurrentPath)
                    || !File.Exists(track.CurrentPath))
                {
                    done++;
                    continue;
                }

                await ArchiveExtraCopiesAsync(track, db, save: true, profileId, ct);
                var copy = await db.TrackArchiveCopies.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.TrackId == id && c.ProfileId == profileId, ct);
                if (copy?.Warning is { Length: > 0 } || copy is null) failed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                failed++;
                _logger.LogDebug(ex, "Archive backfill failed for track {Id}", id);
            }

            done++;
            progress?.Report(new BackfillProgress(
                ids.Count, done, failed, null, true, null));
        }

        progress?.Report(new BackfillProgress(
            ids.Count, done, failed, null, false,
            failed == 0 ? "Every archive copy is present." : $"{failed} copies need another try."));
    }

    /// <summary>Tracks that have a primary file on disk.</summary>
    private async Task<List<int>> TrackIdsAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Tracks.AsNoTracking()
            .Where(track => track.CurrentPath != null && track.CurrentPath != "")
            .OrderBy(track => track.Id)
            .Select(track => track.Id)
            .ToListAsync(ct);
    }

    private static async Task ClearRowsAsync(
        int trackId, MelodyBridgeDbContext db, CancellationToken ct)
    {
        var rows = await db.TrackArchiveCopies
            .Where(copy => copy.TrackId == trackId)
            .ToListAsync(ct);
        db.TrackArchiveCopies.RemoveRange(rows);
    }

    private static async Task<bool> NameIsFreeAsync(
        MelodyBridgeDbContext db, int trackId, string path, CancellationToken ct)
    {
        if (await db.TrackArchiveCopies.AsNoTracking().AnyAsync(
                copy => copy.TrackId != trackId && copy.Path == path, ct))
            return false;
        return !await db.Tracks.AsNoTracking().AnyAsync(
            track => track.Id != trackId
                && (track.CurrentPath == path || track.ArchivePath == path), ct);
    }

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

    private static void Fail(
        TrackArchiveCopyEntity row, TrackEntity track, ArchiveProfileEntity profile, string reason)
    {
        row.Warning = reason;
        row.UpdatedAt = DateTime.UtcNow;
        var segment = $"{WarningPrefix} ({profile.Name}): {reason}";
        var segments = (track.Warning ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(existing => !existing.StartsWith(
                $"{WarningPrefix} ({profile.Name}):", StringComparison.Ordinal))
            .ToList();
        segments.Add(segment);
        track.Warning = string.Join("; ", segments);
    }

    private static void ClearWarning(TrackEntity track, string profileName)
    {
        if (string.IsNullOrWhiteSpace(track.Warning)) return;
        var prefix = $"{WarningPrefix} ({profileName}):";
        var segments = track.Warning
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(existing => !existing.StartsWith(prefix, StringComparison.Ordinal))
            .ToList();
        track.Warning = segments.Count == 0 ? null : string.Join("; ", segments);
    }

    private static bool IsUnder(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }
}
