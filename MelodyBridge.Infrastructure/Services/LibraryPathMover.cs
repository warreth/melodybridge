using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>What one settings save moved: files relocated, and the rows
/// (playlists, scan locations) that followed the new root. Failed files
/// kept their old path when a destination was already taken or a move
/// went wrong. A refused change (the new root sits inside the old one, or
/// the other way around) moves nothing and says so.</summary>
public readonly record struct LibraryMoveResult(
    int Files, int FailedFiles, int Playlists, int ScanLocations,
    bool MusicMoveRefused = false, bool ArchiveMoveRefused = false)
{
    public bool MovedAnything => Files > 0 || Playlists > 0 || ScanLocations > 0;
}

/// <summary>
/// Moves the library when the Music path or Archive path setting changes:
/// playlists and scan locations that followed the old root follow the new
/// one, and every tracked file under the old root lands at the same
/// relative place under the new root. Nothing is overwritten; a file whose
/// destination is taken keeps its path and is counted as failed, and one
/// failure never stops the pass.
/// </summary>
public sealed class LibraryPathMover
{
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly ILogger<LibraryPathMover> _logger;

    public LibraryPathMover(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        ILogger<LibraryPathMover> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<LibraryMoveResult> MoveAsync(
        string? oldMusicPath, string? newMusicPath,
        string? oldArchivePath, string? newArchivePath,
        CancellationToken ct = default)
    {
        var moveMusic = FileRelocator.CanMove(oldMusicPath, newMusicPath);
        var moveArchive = FileRelocator.CanMove(oldArchivePath, newArchivePath);
        // A nested root change moves nothing: report it so the settings
        // page can say so instead of promising a move that never happened.
        var refusedMusic = !moveMusic && IsNestedChange(oldMusicPath, newMusicPath);
        var refusedArchive = !moveArchive && IsNestedChange(oldArchivePath, newArchivePath);

        var files = 0;
        var failed = 0;
        var playlists = 0;
        var scanLocations = 0;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Files: every tracked path that sat under an old root moves to the
        // same relative spot under the new root. A shared path moves once,
        // and every row pointing at it follows. Progress is persisted in
        // small batches: a crash mid-pass then leaves at most the last
        // batch unrecorded (a few re-downloads) instead of the whole
        // library while the files are already gone from the old root.
        const int SaveEveryMoves = 20;
        if (moveMusic || moveArchive)
        {
            var moved = new Dictionary<string, string>(FileRelocator.PathComparer);
            var tracks = await db.Tracks
                .Where(t => t.CurrentPath != null || t.ArchivePath != null)
                .ToListAsync(ct);

            var pendingMoves = 0;
            foreach (var track in tracks)
            {
                ct.ThrowIfCancellationRequested();
                var movedBefore = files;
                if (moveMusic && track.CurrentPath is { Length: > 0 } primary
                    && Relocate(primary, oldMusicPath!, newMusicPath!, moved, ref files, ref failed) is { } newPrimary)
                {
                    track.CurrentPath = newPrimary;
                }
                if (moveArchive && track.ArchivePath is { Length: > 0 } archive
                    && Relocate(archive, oldArchivePath!, newArchivePath!, moved, ref files, ref failed) is { } newArchive)
                {
                    track.ArchivePath = newArchive;
                }

                pendingMoves += files - movedBefore;
                if (pendingMoves >= SaveEveryMoves)
                {
                    await db.SaveChangesAsync(ct);
                    pendingMoves = 0;
                }
            }
        }

        // Rows follow only when the files actually move: a refused root
        // change (cleared, nested, unchanged) must not leave folders and
        // scan locations pointing at a tree that was never relocated.
        if (moveMusic || moveArchive)
        {
            foreach (var playlist in await db.Playlists.ToListAsync(ct))
            {
                var rowChanged = false;
                if (moveMusic
                    && Rebased(playlist.TargetDirectory, oldMusicPath!, newMusicPath) is { Changed: true } target)
                {
                    playlist.TargetDirectory = target.Value;
                    rowChanged = true;
                }
                if (moveArchive
                    && Rebased(playlist.ArchiveDirectory, oldArchivePath!, newArchivePath) is { Changed: true } archive)
                {
                    playlist.ArchiveDirectory = archive.Value;
                    rowChanged = true;
                }
                if (rowChanged) playlists++;
            }
        }

        if (moveMusic)
        {
            foreach (var location in await db.ScanLocations.ToListAsync(ct))
            {
                if (Rebased(location.Path, oldMusicPath!, newMusicPath) is { Changed: true } rebased)
                {
                    location.Path = rebased.Value!;
                    scanLocations++;
                }
            }
        }

        if (files > 0 || playlists > 0 || scanLocations > 0)
            await db.SaveChangesAsync(ct);

        // The old roots keep no empty folders behind: a folder that still
        // holds a file (one without a row, or a failed move) survives.
        if (moveMusic) CleanupEmptyFolders(oldMusicPath!);
        if (moveArchive) CleanupEmptyFolders(oldArchivePath!);

        if (files > 0 || failed > 0 || playlists > 0 || scanLocations > 0)
        {
            _logger.LogInformation(
                "Library move {Old} -> {New}: {Files} file(s) moved ({Failed} failed), {Playlists} playlist(s), {Locations} scan location(s)",
                oldMusicPath, newMusicPath, files, failed, playlists, scanLocations);
        }
        if (refusedMusic || refusedArchive)
        {
            _logger.LogWarning(
                "Library move refused: the new root sits inside the old one (music: {OldMusic} -> {NewMusic}, archive: {OldArchive} -> {NewArchive})",
                oldMusicPath, newMusicPath, oldArchivePath, newArchivePath);
        }

        return new LibraryMoveResult(
            files, failed, playlists, scanLocations, refusedMusic, refusedArchive);
    }

    /// <summary>True when both roots are given, differ, and one sits inside
    /// the other: a move that would be ambiguous and is refused.</summary>
    private static bool IsNestedChange(string? oldRoot, string? newRoot)
        => !string.IsNullOrWhiteSpace(oldRoot)
           && !string.IsNullOrWhiteSpace(newRoot)
           && !FileRelocator.PathsEqual(oldRoot, newRoot)
           && (FileRelocator.IsUnder(newRoot, oldRoot) || FileRelocator.IsUnder(oldRoot, newRoot));

    /// <summary>Moves one file into the new root; returns its new path, or
    /// null when it was not under the old root or could not move.</summary>
    private string? Relocate(
        string path, string oldRoot, string newRoot,
        Dictionary<string, string> moved, ref int movedFiles, ref int failedFiles)
    {
        if (!FileRelocator.IsUnder(path, oldRoot)) return null;
        // A shared path (several rows pointing at one file) was already
        // moved: everyone follows the recorded destination.
        if (moved.TryGetValue(path, out var known)) return known;
        if (!File.Exists(path)) return null;

        var destination = Path.Combine(newRoot, Path.GetRelativePath(oldRoot, path));
        if (FileRelocator.TryMove(path, destination, _logger))
        {
            moved[path] = destination;
            movedFiles++;
            return destination;
        }
        failedFiles++;
        return null;
    }

    private static string? EmptyToNull(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// A stored folder rebased onto the new root: the old root itself
    /// becomes the new root, a folder below it keeps its relative place,
    /// and anything unrelated is left alone. Clearing the new root sends
    /// the followers back to their default (null).
    /// </summary>
    private static (bool Changed, string? Value) Rebased(string? path, string oldRoot, string? newRoot)
    {
        if (string.IsNullOrWhiteSpace(path)) return (false, null);
        if (FileRelocator.PathsEqual(path, oldRoot))
            return (true, EmptyToNull(newRoot));
        if (!FileRelocator.IsUnder(path, oldRoot))
            return (false, null);
        return string.IsNullOrWhiteSpace(newRoot)
            ? (true, null)
            : (true, Path.Combine(newRoot, Path.GetRelativePath(oldRoot, path)));
    }

    /// <summary>Removes directories the move left empty, deepest first.
    /// Directory.Delete without recursion throws on a folder that still
    /// holds anything, which is exactly the guard wanted here.</summary>
    private void CleanupEmptyFolders(string root)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            var folders = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .OrderByDescending(path => path.Length);
            foreach (var folder in folders)
            {
                try { Directory.Delete(folder, recursive: false); }
                catch (IOException) { /* still holds something: keep it */ }
                catch (UnauthorizedAccessException) { /* not ours to remove */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Library move: could not clean up empty folders under {Root}", root);
        }
    }
}
