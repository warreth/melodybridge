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
    int Shared,
    int Outside,
    int ArchiveSkipped,
    int Total,
    bool KeepsSourceName);

/// <summary>What one rename run did.</summary>
public sealed record RenameFilesReport(
    int Renamed, int Skipped, int Failed, int ArchiveSkipped);

/// <summary>
/// Applies the library file name pattern to files that are already on
/// disk, on request from the Advanced page. It only touches files the app
/// put in its own folders, never renames a path another song references,
/// and moves the archive copy together with its track. A failure on one
/// row never stops the run.
///
/// A path can be shared by several playlist rows for the same song when
/// playlist folders are off. Such a path moves once and every row that
/// points at it follows. A path shared by different songs stays put.
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
        var skipped = summary.AlreadyNamed + summary.Missing
            + summary.Shared + summary.Outside;
        if (plan.Moves.Count == 0)
            return new RenameFilesReport(0, skipped, 0, summary.ArchiveSkipped);

        var renamedRows = new HashSet<int>();
        var failedRows = new HashSet<int>();

        // Main files first: when one cannot move, its archive copy stays
        // with the old name too. Shared paths appear once and carry every
        // row that follows the move.
        foreach (var move in plan.Moves.Where(m => !m.Archive))
        {
            ct.ThrowIfCancellationRequested();
            if (await ExecuteMoveAsync(move, ct)) renamedRows.UnionWith(move.TrackIds);
            else failedRows.UnionWith(move.TrackIds);
        }

        foreach (var move in plan.Moves.Where(m => m.Archive))
        {
            ct.ThrowIfCancellationRequested();
            // A shared archive file only moves when every row behind it
            // still has its main file in place.
            if (move.TrackIds.Any(failedRows.Contains))
            {
                failedRows.UnionWith(move.TrackIds);
                continue;
            }
            if (await ExecuteMoveAsync(move, ct)) renamedRows.UnionWith(move.TrackIds);
            else failedRows.UnionWith(move.TrackIds);
        }

        var renamed = renamedRows.Except(failedRows).Count();
        var failed = failedRows.Count;
        _logger.LogInformation(
            "File name pass: {Renamed} renamed, {Skipped} skipped, {ArchiveSkipped} archive copies skipped, {Failed} failed",
            renamed, skipped, summary.ArchiveSkipped, failed);
        return new RenameFilesReport(renamed, skipped, failed, summary.ArchiveSkipped);
    }

    private async Task<bool> ExecuteMoveAsync(PlannedMove move, CancellationToken ct)
    {
        try
        {
            return await MoveOneAsync(move, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not rename track {TrackId}", move.TrackId);
            return false;
        }
    }

    /// <summary>One file move plus its row updates; undo the move when the
    /// rows changed underneath.</summary>
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
            if (await UpdateRowsAsync(move, ct)) return true;
            // The rows moved on between the plan and the update (a
            // re-download): put the file back where they point.
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

    /// <summary>
    /// Moves every downloaded row that still points at the old path. A
    /// path shared by several rows for the same song must move once and
    /// leave no row on the old name.
    /// </summary>
    private async Task<bool> UpdateRowsAsync(PlannedMove move, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // A download that started after the plan must win: its row is no
        // longer "downloaded" once it claims the track.
        var query = db.Tracks.Where(t => t.DownloadStatus == "downloaded");
        query = move.Archive
            ? query.Where(t => t.ArchivePath == move.OldPath)
            : query.Where(t => t.CurrentPath == move.OldPath);
        var updated = move.Archive
            ? await query.ExecuteUpdateAsync(
                s => s.SetProperty(t => t.ArchivePath, move.NewPath), ct)
            : await query.ExecuteUpdateAsync(
                s => s.SetProperty(t => t.CurrentPath, move.NewPath), ct);
        return updated > 0;
    }

    /// <summary>One file to move; the archive copy is its own move. The
    /// first row creates it, later rows on the same path join TrackIds.</summary>
    private sealed class PlannedMove
    {
        public PlannedMove(int trackId, string oldPath, string newPath, bool archive)
        {
            TrackId = trackId;
            OldPath = oldPath;
            NewPath = newPath;
            Archive = archive;
            TrackIds.Add(trackId);
        }

        public int TrackId { get; }
        public string OldPath { get; }
        public string NewPath { get; }
        public bool Archive { get; }
        public List<int> TrackIds { get; } = new();
    }

    private sealed record Plan(List<PlannedMove> Moves, RenamePlanSummary Summary);

    private sealed record Row(
        int Id, string? MelodyId, string? Artist, string? Title, string? Album, int? Position,
        string CurrentPath, string? ArchivePath);

    /// <summary>Who points at one path: rows, distinct songs and whether
    /// every owner is downloaded. A path with one song is safe to move.</summary>
    private sealed class PathOwners
    {
        public HashSet<int> RowIds { get; } = new();
        public HashSet<string> MelodyIds { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Main { get; set; }
        public bool Archive { get; set; }
        public bool AllDownloaded { get; set; } = true;
    }

    private async Task<Plan> BuildPlanAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var pattern = await _settings.GetAsync(
            FileNamePattern.SettingKey, FileNamePattern.Default, ct);

        // One read for both the plan and the reference map: the map must
        // see every row, including rows that are not downloaded, or a move
        // could pull a file out from under a pending row.
        var allRows = await db.Tracks.AsNoTracking()
            .Select(t => new
            {
                t.Id, t.MelodyId, t.Artist, t.Title, t.Album, t.Position,
                t.CurrentPath, t.ArchivePath, t.DownloadStatus,
            })
            .ToListAsync(ct);

        var owners = new Dictionary<string, PathOwners>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in allRows)
        {
            if (!string.IsNullOrEmpty(entry.CurrentPath))
                AddOwner(owners, entry.CurrentPath, entry.Id, entry.MelodyId,
                    entry.DownloadStatus, archive: false);
            if (!string.IsNullOrEmpty(entry.ArchivePath))
                AddOwner(owners, entry.ArchivePath, entry.Id, entry.MelodyId,
                    entry.DownloadStatus, archive: true);
        }

        var rows = allRows
            .Where(t => t.DownloadStatus == "downloaded" && t.CurrentPath is { Length: > 0 })
            .OrderBy(t => t.Id)
            .Select(t => new Row(
                t.Id, t.MelodyId, t.Artist, t.Title, t.Album, t.Position,
                t.CurrentPath!, t.ArchivePath))
            .ToList();
        if (rows.Count == 0)
            return EmptyPlan(0, keepsSource: false);

        // A pattern that expands to nothing (empty, or {source}) means the
        // user wants plugin names: nothing to plan.
        if (FileNamePattern.Expand(pattern, "Artist", "Title") is null)
            return EmptyPlan(rows.Count, keepsSource: true);

        // One name per path, chosen from the lowest row id. Rows that
        // share a path must follow the same move, even when their tags
        // disagree after an older scan; otherwise the next pass would
        // rename the file back and forth.
        var mainNames = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (mainNames.TryGetValue(row.CurrentPath, out var known) && known is not null)
                continue;
            mainNames[row.CurrentPath] = FileNamePattern.Expand(
                pattern, row.Artist, row.Title, row.Album,
                row.Position is { } position ? position + 1 : null);
        }
        var archiveNames = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.ArchivePath is not { Length: > 0 } archivePath
                || archiveNames.ContainsKey(archivePath)) continue;
            if (mainNames.TryGetValue(row.CurrentPath, out var name) && name is not null)
                archiveNames[archivePath] = name;
        }

        var roots = await ManagedRootsAsync(db, ct);
        var moves = new List<PlannedMove>();
        var mainMoves = new Dictionary<string, PlannedMove>(StringComparer.OrdinalIgnoreCase);
        var archiveMoves = new Dictionary<string, PlannedMove>(StringComparer.OrdinalIgnoreCase);
        var alreadyNamed = 0;
        var missing = 0;
        var shared = 0;
        var outside = 0;
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
                    "Rename skipped for track {Id} ({Title}) at {Path}: {Reason}",
                    row.Id, row.Title, row.CurrentPath, reason);

            if (!File.Exists(row.CurrentPath))
            {
                missing++;
                Skip("the file is missing");
                continue;
            }
            if (!IsManaged(row.CurrentPath, roots))
            {
                outside++;
                Skip("the file is outside the managed folders");
                continue;
            }

            var mainOwner = owners.GetValueOrDefault(row.CurrentPath);
            var sharedReason = SharedReason(mainOwner, row.MelodyId);
            if (sharedReason is not null)
            {
                shared++;
                Skip(sharedReason);
                continue;
            }

            var baseName = mainNames.GetValueOrDefault(row.CurrentPath);
            if (baseName is null) continue;

            var directory = Path.GetDirectoryName(row.CurrentPath);
            if (string.IsNullOrEmpty(directory))
            {
                outside++;
                Skip("the file has no folder to rename it in");
                continue;
            }
            var extension = Path.GetExtension(row.CurrentPath);
            var rowHasMove = false;

            if (!string.Equals(
                    Path.GetFileNameWithoutExtension(row.CurrentPath), baseName,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (mainMoves.TryGetValue(row.CurrentPath, out var existing))
                {
                    existing.TrackIds.Add(row.Id);
                    rowHasMove = true;
                }
                else
                {
                    var candidate = Reserve(FilesIn(directory), directory, baseName, extension);
                    if (!PathsEqual(candidate, row.CurrentPath))
                    {
                        var move = new PlannedMove(
                            row.Id, row.CurrentPath, candidate, archive: false);
                        mainMoves[row.CurrentPath] = move;
                        moves.Add(move);
                        rowHasMove = true;
                    }
                }
            }

            var archivedPath = row.ArchivePath;
            var archiveBaseName = archivedPath is { Length: > 0 }
                ? archiveNames.GetValueOrDefault(archivedPath) ?? baseName
                : baseName;
            if (archivedPath is { Length: > 0 } archivePath
                && File.Exists(archivePath)
                && !string.Equals(
                    Path.GetFileNameWithoutExtension(archivePath), archiveBaseName,
                    StringComparison.OrdinalIgnoreCase))
            {
                var archiveOwner = owners.GetValueOrDefault(archivePath);
                var archiveSharedReason = SharedReason(archiveOwner, row.MelodyId);
                if (archiveSharedReason is not null)
                {
                    archiveSkipped++;
                    Skip("its archive copy: " + archiveSharedReason);
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
                        if (archiveMoves.TryGetValue(archivePath, out var existingArchive))
                        {
                            existingArchive.TrackIds.Add(row.Id);
                            rowHasMove = true;
                        }
                        else
                        {
                            var archiveExtension = Path.GetExtension(archivePath);
                            var archiveCandidate = Reserve(
                                FilesIn(archiveDirectory), archiveDirectory, archiveBaseName, archiveExtension);
                            if (!PathsEqual(archiveCandidate, archivePath))
                            {
                                var move = new PlannedMove(
                                    row.Id, archivePath, archiveCandidate, archive: true);
                                archiveMoves[archivePath] = move;
                                moves.Add(move);
                                rowHasMove = true;
                            }
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

        // Count rows, not files: a shared path and its archive copy still
        // describe the tracks whose rows will follow the move.
        var pendingRows = moves.SelectMany(m => m.TrackIds).Distinct().Count();
        return new Plan(moves, new RenamePlanSummary(
            pendingRows, alreadyNamed, missing, shared, outside, archiveSkipped,
            rows.Count, KeepsSourceName: false));
    }

    private static Plan EmptyPlan(int total, bool keepsSource)
        => new(new List<PlannedMove>(), new RenamePlanSummary(
            0, 0, 0, 0, 0, 0, total, keepsSource));

    private static void AddOwner(
        Dictionary<string, PathOwners> owners, string path, int id, string? melodyId,
        string? downloadStatus, bool archive)
    {
        if (!owners.TryGetValue(path, out var owner))
        {
            owner = new PathOwners();
            owners[path] = owner;
        }
        owner.RowIds.Add(id);
        owner.MelodyIds.Add(string.IsNullOrWhiteSpace(melodyId) ? $"row:{id}" : melodyId);
        if (archive) owner.Archive = true;
        else owner.Main = true;
        if (downloadStatus != "downloaded") owner.AllDownloaded = false;
    }

    /// <summary>
    /// A path with several rows is only safe to move when every row is a
    /// downloaded copy of the same song. Returns null when it is safe, and
    /// the reason to log when it is not. Different songs on one path, a
    /// path used as both a main file and an archive copy, or a pending row
    /// on the same name stays put.
    /// </summary>
    private static string? SharedReason(PathOwners? owner, string? melodyId)
    {
        if (owner is null) return null;
        if (owner.Main && owner.Archive)
            return "the path is used as both a main file and an archive copy";
        if (owner.RowIds.Count <= 1) return null;
        if (!owner.AllDownloaded)
            return "another row on the path is not downloaded";
        if (string.IsNullOrWhiteSpace(melodyId))
            return "the shared rows have no MELODY_ID";
        if (owner.MelodyIds.Count != 1)
            return "the path is shared by different songs";
        return owner.MelodyIds.Contains(melodyId)
            ? null
            : "the path is shared by a different song";
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
