using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>What the preview found, by outcome. Counts are track rows,
/// except <see cref="ArchiveSkipped"/>, which counts archive copies.</summary>
public sealed record RenamePlanSummary(
    int ToRename,
    int AlreadyNamed,
    int Missing,
    int SharedOrOutside,
    int ArchiveSkipped,
    int Total,
    bool KeepsSourceName);

/// <summary>What one rename run did.</summary>
public sealed record RenameFilesReport(
    int Renamed, int Skipped, int Failed, int ArchiveSkipped);

/// <summary>
/// Applies the library file name pattern to files that are already on
/// disk, on request from the Advanced page. It only touches files the app
/// put in its own folders, never renames a path another row references,
/// and moves the archive copy together with its track. A failure on one
/// row never stops the run.
///
/// Renaming a hardlink changes one directory entry only: the other
/// playlist's name still points at the same bytes, so dedup keeps working.
/// </summary>
public sealed class FileNameRenamer
{
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly SettingsStore _settings;
    private readonly PathLockTable _pathLocks;
    private readonly ILogger<FileNameRenamer> _logger;

    public FileNameRenamer(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        SettingsStore settings,
        ILogger<FileNameRenamer>? logger = null,
        PathLockTable? pathLocks = null)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions
            .NullLogger<FileNameRenamer>.Instance;
        _pathLocks = pathLocks ?? PathLockTable.Shared;
    }

    /// <summary>
    /// The preview: how many rows would move and why the others would not.
    /// </summary>
    public async Task<RenamePlanSummary> CountAsync(CancellationToken ct = default)
    {
        // A preview must answer, even when a network mount makes a stat
        // hang. The pass itself may still run to completion in the
        // background; the caller only gets a clear timeout.
        var plan = await BuildPlanAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
        return plan.Summary;
    }

    /// <summary>Renames the planned files and updates their rows.</summary>
    public async Task<RenameFilesReport> RenameAsync(CancellationToken ct = default)
    {
        var plan = await BuildPlanAsync(ct);
        var summary = plan.Summary;
        var skipped = summary.AlreadyNamed + summary.Missing + summary.SharedOrOutside;
        if (plan.Moves.Count == 0)
            return new RenameFilesReport(0, skipped, 0, summary.ArchiveSkipped);

        var renamedRows = new HashSet<int>();
        var failedRows = new HashSet<int>();
        foreach (var group in plan.Moves.GroupBy(move => move.TrackId))
        {
            ct.ThrowIfCancellationRequested();
            var ok = true;
            try
            {
                // Main file first: when that move cannot happen, the
                // archive copy stays with the old name too.
                foreach (var move in group.OrderBy(move => move.Archive))
                {
                    if (!await MoveOneAsync(move, ct))
                    {
                        ok = false;
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not rename track {TrackId}", group.Key);
                ok = false;
            }

            if (ok) renamedRows.Add(group.Key);
            else failedRows.Add(group.Key);
        }

        var renamed = renamedRows.Except(failedRows).Count();
        var failed = failedRows.Count;
        _logger.LogInformation(
            "File name pass: {Renamed} renamed, {Skipped} skipped, {ArchiveSkipped} archive copies skipped, {Failed} failed",
            renamed, skipped, summary.ArchiveSkipped, failed);
        return new RenameFilesReport(renamed, skipped, failed, summary.ArchiveSkipped);
    }

    /// <summary>One file move plus its conditional row update; undo the
    /// move when the row changed underneath.</summary>
    private async Task<bool> MoveOneAsync(PlannedMove move, CancellationToken ct)
    {
        // A stuck download may hold the name lock for a long time; the
        // rename pass must not wait forever on it.
        using var lockTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lockTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var scope = await _pathLocks.AcquireAsync(move.NewPath, lockTimeout.Token);
            // A second look under the lock: another writer may have taken
            // the name since the plan was built.
            if (File.Exists(move.NewPath) || !File.Exists(move.OldPath)) return false;
            File.Move(move.OldPath, move.NewPath);
            if (await UpdateRowAsync(move, ct)) return true;
            // The row moved on between the plan and the update (a re-download):
            // put the file back where its row says.
            TryMoveBack(move.NewPath, move.OldPath);
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Timed out waiting for the name lock on {Path}; leaving the file alone",
                move.OldPath);
            return false;
        }
    }

    private static void TryMoveBack(string from, string to)
    {
        try
        {
            if (File.Exists(from) && !File.Exists(to)) File.Move(from, to);
        }
        catch
        {
            // Best effort: the row is the source of truth for the next scan.
        }
    }

    private async Task<bool> UpdateRowAsync(PlannedMove move, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // A download that started after the plan must win: its row is no
        // longer "downloaded" once it claims the track.
        var query = db.Tracks.Where(t => t.Id == move.TrackId
            && t.DownloadStatus == "downloaded");
        query = move.Archive
            ? query.Where(t => t.ArchivePath == move.OldPath)
            : query.Where(t => t.CurrentPath == move.OldPath);
        var updated = move.Archive
            ? await query.ExecuteUpdateAsync(
                s => s.SetProperty(t => t.ArchivePath, move.NewPath), ct)
            : await query.ExecuteUpdateAsync(
                s => s.SetProperty(t => t.CurrentPath, move.NewPath), ct);
        return updated == 1;
    }

    /// <summary>One file to move; the archive copy is its own move.</summary>
    private sealed record PlannedMove(
        int TrackId, string OldPath, string NewPath, bool Archive);

    private sealed record Plan(List<PlannedMove> Moves, RenamePlanSummary Summary);

    private sealed record Row(
        int Id, string? Artist, string? Title, string? Album, int? Position,
        string CurrentPath, string? ArchivePath);

    private async Task<Plan> BuildPlanAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var pattern = await _settings.GetAsync(
            FileNamePattern.SettingKey, FileNamePattern.Default, ct);

        var rows = await db.Tracks.AsNoTracking()
            .Where(t => t.DownloadStatus == "downloaded" && t.CurrentPath != null)
            .OrderBy(t => t.Id)
            .Select(t => new Row(
                t.Id, t.Artist, t.Title, t.Album, t.Position,
                t.CurrentPath!, t.ArchivePath))
            .ToListAsync(ct);
        if (rows.Count == 0)
            return EmptyPlan(0, keepsSource: false);

        // A pattern that expands to nothing (empty, or {source}) means the
        // user wants plugin names: nothing to plan.
        if (FileNamePattern.Expand(pattern, "Artist", "Title") is null)
            return EmptyPlan(rows.Count, keepsSource: true);

        // Paths another row references must not move under it.
        var references = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var allPaths = await db.Tracks.AsNoTracking()
            .Select(t => new { t.CurrentPath, t.ArchivePath })
            .ToListAsync(ct);
        foreach (var entry in allPaths)
        {
            AddReference(references, entry.CurrentPath);
            AddReference(references, entry.ArchivePath);
        }

        var roots = await ManagedRootsAsync(db, ct);
        var moves = new List<PlannedMove>();
        var alreadyNamed = 0;
        var missing = 0;
        var sharedOrOutside = 0;
        var archiveSkipped = 0;

        // One directory listing per folder for the whole pass: a playlist
        // folder with 3000 tracks must not be enumerated 3000 times.
        var directoryFiles = new Dictionary<string, HashSet<string>>(
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> FilesIn(string directory)
        {
            if (directoryFiles.TryGetValue(directory, out var known)) return known;
            try
            {
                known = new HashSet<string>(
                    Directory.EnumerateFiles(directory), StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                // An unreadable folder must not fail the whole pass.
                _logger.LogDebug(ex, "Could not list {Directory} for the rename pass", directory);
                known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            directoryFiles[directory] = known;
            return known;
        }
        static string Reserve(HashSet<string> used, string directory, string baseName, string extension)
        {
            var candidate = Path.Combine(directory, baseName + extension);
            for (var suffix = 2; used.Contains(candidate); suffix++)
                candidate = Path.Combine(directory, $"{baseName} ({suffix}){extension}");
            used.Add(candidate);
            return candidate;
        }

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            void Skip(string reason)
                => _logger.LogDebug(
                    "Rename skipped for track {Id} ({Title}): {Reason}",
                    row.Id, row.Title, reason);

            if (!File.Exists(row.CurrentPath))
            {
                missing++;
                Skip("the file is missing");
                continue;
            }
            if (!IsManaged(row.CurrentPath, roots))
            {
                sharedOrOutside++;
                Skip("the file is outside the managed folders");
                continue;
            }
            if (references.GetValueOrDefault(row.CurrentPath) > 1)
            {
                sharedOrOutside++;
                Skip("the path is shared with another row");
                continue;
            }

            var baseName = FileNamePattern.Expand(
                pattern, row.Artist, row.Title, row.Album,
                row.Position is { } position ? position + 1 : null);
            if (baseName is null) continue;

            var directory = Path.GetDirectoryName(row.CurrentPath);
            if (string.IsNullOrEmpty(directory))
            {
                sharedOrOutside++;
                Skip("the file has no folder to rename it in");
                continue;
            }
            var extension = Path.GetExtension(row.CurrentPath);
            var rowHasMove = false;

            if (!string.Equals(
                    Path.GetFileNameWithoutExtension(row.CurrentPath), baseName,
                    StringComparison.OrdinalIgnoreCase))
            {
                var candidate = Reserve(FilesIn(directory), directory, baseName, extension);
                if (!PathsEqual(candidate, row.CurrentPath))
                {
                    moves.Add(new PlannedMove(row.Id, row.CurrentPath, candidate, Archive: false));
                    rowHasMove = true;
                }
            }

            if (row.ArchivePath is { Length: > 0 } archivePath
                && File.Exists(archivePath)
                && !string.Equals(
                    Path.GetFileNameWithoutExtension(archivePath), baseName,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (references.GetValueOrDefault(archivePath) > 1)
                {
                    archiveSkipped++;
                    Skip("its archive copy is shared with another row");
                }
                else if (!IsManaged(archivePath, roots))
                {
                    archiveSkipped++;
                    Skip("its archive copy is outside the managed folders");
                }
                else
                {
                    var archiveDirectory = Path.GetDirectoryName(archivePath);
                    if (!string.IsNullOrEmpty(archiveDirectory))
                    {
                        var archiveExtension = Path.GetExtension(archivePath);
                        var archiveCandidate = Reserve(
                            FilesIn(archiveDirectory), archiveDirectory, baseName, archiveExtension);
                        if (!PathsEqual(archiveCandidate, archivePath))
                        {
                            moves.Add(new PlannedMove(
                                row.Id, archivePath, archiveCandidate, Archive: true));
                            rowHasMove = true;
                        }
                    }
                }
            }

            if (!rowHasMove)
            {
                alreadyNamed++;
                Skip("the file already follows the pattern");
            }
        }

        // Count pending rows, not pending files: a track with an archive
        // copy contributes two moves but is one renamed track.
        var pendingRows = moves.Select(m => m.TrackId).Distinct().Count();
        return new Plan(moves, new RenamePlanSummary(
            pendingRows, alreadyNamed, missing, sharedOrOutside, archiveSkipped,
            rows.Count, KeepsSourceName: false));
    }

    private static Plan EmptyPlan(int total, bool keepsSource)
        => new(new List<PlannedMove>(), new RenamePlanSummary(
            0, 0, 0, 0, 0, total, keepsSource));

    private static void AddReference(Dictionary<string, int> references, string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        references[path] = references.GetValueOrDefault(path) + 1;
    }

    private static bool IsManaged(string path, IReadOnlyList<string> roots)
        => roots.Any(root => FileRelocator.IsUnder(path, root));

    private static async Task<List<string>> ManagedRootsAsync(
        MelodyBridgeDbContext db, CancellationToken ct)
    {
        var roots = new List<string>();
        void Add(string? root)
        {
            if (string.IsNullOrWhiteSpace(root)) return;
            var trimmed = root.TrimEnd('/', '\\');
            if (trimmed.Length == 0) return;
            if (!roots.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                roots.Add(trimmed);
        }

        Add(await db.DownloaderSettings.AsNoTracking()
            .Where(s => s.Key == "music_path").Select(s => s.Value).FirstOrDefaultAsync(ct));
        Add(await db.DownloaderSettings.AsNoTracking()
            .Where(s => s.Key == "archive_path").Select(s => s.Value).FirstOrDefaultAsync(ct));
        foreach (var playlist in await db.Playlists.AsNoTracking()
                     .Select(p => new { p.TargetDirectory, p.ArchiveDirectory })
                     .ToListAsync(ct))
        {
            Add(playlist.TargetDirectory);
            Add(playlist.ArchiveDirectory);
        }
        foreach (var path in await db.ScanLocations.AsNoTracking()
                     .Select(l => l.Path).ToListAsync(ct))
        {
            Add(path);
        }
        return roots;
    }

    private static bool PathsEqual(string? left, string? right)
        => left is not null && right is not null
           && string.Equals(
               left.Replace('\\', '/').TrimEnd('/'),
               right.Replace('\\', '/').TrimEnd('/'),
               StringComparison.OrdinalIgnoreCase);
}
