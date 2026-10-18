using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Scanning;

/// <summary>
/// Reconciles playlist tracks with files on disk after a restart: a track
/// whose file still exists (at CurrentPath or anywhere under the playlist's
/// TargetDirectory, matched by MELODY_ID tag) goes back to "downloaded"
/// with fresh audio facts. Tracks whose file vanished flip to "pending" so
/// the next download run picks them up again. Downloaded files that sit
/// loose under the playlist's base folder (from before playlists had
/// their own folder) are moved into the playlist's folder, archive copies
/// included; files outside the base folder are left alone.
/// </summary>
public class LibraryReconciler
{
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly ILogger<LibraryReconciler> _logger;
    private readonly SettingsStore? _settings;

    public LibraryReconciler(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        ILogger<LibraryReconciler> logger,
        SettingsStore? settings = null)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _settings = settings;
    }

    /// <summary>Run once at startup. Returns (relinked, markedPending).</summary>
    public async Task<(int relinked, int lost)> ReconcileAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var playlists = await db.Playlists
            .Include(p => p.Tracks)
            .ToListAsync(ct);

        // The relink index needs the same base the folder rules use: a
        // playlist without its own folder lives under the music path, so
        // its files must still be found there by MELODY_ID. Indexing only
        // the raw TargetDirectory made every such row re-download after a
        // manual file move once the page normalized the stored default to
        // null.
        var musicPath = _settings is null
            ? string.Empty
            : await _settings.GetAsync("music_path", "/music", ct);

        var relinked = 0;
        var lost = 0;

        foreach (var playlist in playlists)
        {
            var dir = FirstNonEmpty(playlist.TargetDirectory, musicPath);
            var filesByMelodyId = string.IsNullOrWhiteSpace(dir)
                ? new Dictionary<string, string>()
                : IndexDirectory(dir);

            foreach (var track in playlist.Tracks)
            {
                // Fast path: the remembered path still exists.
                if (track.CurrentPath is not null && File.Exists(track.CurrentPath))
                {
                    if (track.DownloadStatus != "downloaded")
                    {
                        track.DownloadStatus = "downloaded";
                        track.DownloadError = null;
                        relinked++;
                    }
                    continue;
                }

                // Slow path: find the file by its MELODY_ID tag under the
                // playlist folder (files move, ids do not).
                if (track.MelodyId is not null
                    && filesByMelodyId.TryGetValue(track.MelodyId, out var found))
                {
                    track.CurrentPath = found;
                    track.DownloadStatus = "downloaded";
                    track.DownloadError = null;
                    track.LastSeenAt = DateTime.UtcNow;
                    relinked++;
                    continue;
                }

                // Nothing on disk: schedule a re-download. Any open quality
                // review described the vanished file, so it goes with it.
                if (track.DownloadStatus == "downloaded")
                {
                    track.DownloadStatus = "pending";
                    track.CurrentPath = null;
                    track.Warning = "file missing on disk, will re-download";
                    track.ReviewKind = null;
                    track.ReviewText = null;
                    track.SourcePluginId = null;
                    lost++;
                }
            }
        }

        var moved = await RelocateLooseFilesAsync(playlists, ct);

        if (relinked > 0 || lost > 0 || moved > 0)
            await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Startup reconciliation: {Relinked} tracks relinked, {Lost} missing files queued for re-download, {Moved} files moved into their playlist folder",
            relinked, lost, moved);
        return (relinked, lost);
    }

    /// <summary>
    /// Moves downloaded files that sit loose under a playlist's base folder
    /// (the layout from before playlists had their own folder) into the
    /// playlist's own folder; archive copies follow the same rule under the
    /// archive path. The decision is made once per physical path: a file
    /// several playlists share (the old shared-folder layout) moves once and
    /// every referencing row follows it, and a file that already sits in the
    /// folder of any playlist that references it stays there, so repeated
    /// passes never rename the same file between two playlist folders.
    /// Files outside the base folder, and destinations that already hold a
    /// file, are left alone: nothing is overwritten and nothing the user
    /// organized elsewhere moves. One failed move never stops the pass; its
    /// rows simply keep the old path.
    /// </summary>
    private async Task<int> RelocateLooseFilesAsync(
        List<PlaylistEntity> playlists, CancellationToken ct)
    {
        if (_settings is null) return 0;

        var musicPath = await _settings.GetAsync("music_path", "/music", ct);
        var archivePath = await _settings.GetAsync("archive_path", "", ct);

        var layout = new Dictionary<string, PlaylistLayout>(StringComparer.Ordinal);
        foreach (var playlist in playlists)
        {
            layout[playlist.Id] = new PlaylistLayout(
                Base: FirstNonEmpty(playlist.TargetDirectory, musicPath),
                Folder: PlaylistRules.ResolvePlaylistFolder(
                    playlist.TargetDirectory, musicPath, playlist.Name),
                ArchiveBase: FirstNonEmpty(playlist.ArchiveDirectory, archivePath),
                ArchiveFolder: playlist.ArchiveEnabled
                    ? PlaylistRules.ResolveArchiveFolder(
                        playlist.ArchiveDirectory, archivePath, playlist.Name)
                    : string.Empty);
        }

        var moved = 0;

        foreach (var (path, refs) in CollectRefs(playlists, track => track.CurrentPath))
        {
            ct.ThrowIfCancellationRequested();
            var owner = PickOwner(path, refs, layout, archive: false);
            if (owner is null) continue;
            if (refs.Any(r => layout[r.PlaylistId].Folder is { Length: > 0 } folder
                              && FileRelocator.IsUnder(path, folder)))
            {
                continue; // already home in the folder of a playlist that owns it
            }

            var destination = Path.Combine(layout[owner.PlaylistId].Folder, Path.GetFileName(path));
            if (!FileRelocator.TryMove(path, destination, _logger)) continue;
            foreach (var (track, _) in refs) track.CurrentPath = destination;
            moved++;
        }

        foreach (var (path, refs) in CollectRefs(playlists, track => track.ArchivePath))
        {
            ct.ThrowIfCancellationRequested();
            var owner = PickOwner(path, refs, layout, archive: true);
            if (owner is null) continue;
            if (refs.Any(r => layout[r.PlaylistId].ArchiveFolder is { Length: > 0 } folder
                              && FileRelocator.IsUnder(path, folder)))
            {
                continue;
            }

            var destination = Path.Combine(layout[owner.PlaylistId].ArchiveFolder, Path.GetFileName(path));
            if (!FileRelocator.TryMove(path, destination, _logger)) continue;
            foreach (var (track, _) in refs) track.ArchivePath = destination;
            moved++;
        }

        return moved;
    }

    /// <summary>Every existing file referenced by a track path, grouped by
    /// the exact path, with the playlist each referencing row belongs to.</summary>
    private static Dictionary<string, List<PathRef>> CollectRefs(
        List<PlaylistEntity> playlists, Func<TrackEntity, string?> pathSelector)
    {
        var refs = new Dictionary<string, List<PathRef>>(FileRelocator.PathComparer);
        foreach (var playlist in playlists)
        {
            foreach (var track in playlist.Tracks)
            {
                if (pathSelector(track) is not { Length: > 0 } path || !File.Exists(path)) continue;
                if (!refs.TryGetValue(path, out var list)) refs[path] = list = new List<PathRef>();
                list.Add(new PathRef(track, playlist.Id));
            }
        }
        return refs;
    }

    /// <summary>The playlist whose own folder receives a loose file: the
    /// first referencing playlist (stable id order) that has such a folder
    /// and whose base contains the file. Null when no referencing playlist
    /// wants or owns it, which leaves the file where it is.</summary>
    private static PathRef? PickOwner(
        string path, List<PathRef> refs, Dictionary<string, PlaylistLayout> layout, bool archive)
        => refs
            .Where(r => archive
                ? layout[r.PlaylistId].ArchiveFolder is { Length: > 0 }
                  && FileRelocator.IsUnder(path, layout[r.PlaylistId].ArchiveBase)
                : layout[r.PlaylistId].Folder is { Length: > 0 }
                  && FileRelocator.IsUnder(path, layout[r.PlaylistId].Base))
            .OrderBy(r => r.PlaylistId, StringComparer.Ordinal)
            .FirstOrDefault();

    private static string FirstNonEmpty(string? first, string second)
        => string.IsNullOrWhiteSpace(first) ? second : first;

    private sealed record PathRef(TrackEntity Track, string PlaylistId);

    private readonly record struct PlaylistLayout(
        string Base, string Folder, string ArchiveBase, string ArchiveFolder);

    /// <summary>MELODY_ID tag -> path, one entry per audio file in the tree.</summary>
    private static Dictionary<string, string> IndexDirectory(string dir)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        var extensions = new[] { ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac", ".wav", ".webm" };
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                if (!extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;
                var id = TaglibHelper.ReadMelodyId(file);
                if (!string.IsNullOrWhiteSpace(id))
                    index.TryAdd(id, file);
            }
        }
        catch (Exception)
        {
            // Unreadable folder: leave the index partial rather than failing the run.
        }
        return index;
    }
}
