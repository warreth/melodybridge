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

        var relinked = 0;
        var lost = 0;

        foreach (var playlist in playlists)
        {
            var dir = playlist.TargetDirectory;
            var filesByMelodyId = dir is null
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
    /// archive path. Files outside the base folder, and destinations that
    /// already hold a file, are left alone: nothing is overwritten and
    /// nothing the user organized elsewhere moves. One failed move never
    /// stops the pass; its row simply keeps the old path.
    /// </summary>
    private async Task<int> RelocateLooseFilesAsync(
        List<PlaylistEntity> playlists, CancellationToken ct)
    {
        if (_settings is null) return 0;

        var musicPath = await _settings.GetAsync("music_path", "/music", ct);
        var archivePath = await _settings.GetAsync("archive_path", "", ct);
        var moved = 0;

        foreach (var playlist in playlists)
        {
            ct.ThrowIfCancellationRequested();

            var baseFolder = !string.IsNullOrWhiteSpace(playlist.TargetDirectory)
                ? playlist.TargetDirectory
                : musicPath;
            var playlistFolder = PlaylistRules.ResolvePlaylistFolder(
                playlist.TargetDirectory, musicPath, playlist.Name);
            var archiveBase = !string.IsNullOrWhiteSpace(playlist.ArchiveDirectory)
                ? playlist.ArchiveDirectory!
                : archivePath;
            var archiveFolder = playlist.ArchiveEnabled
                ? PlaylistRules.ResolveArchiveFolder(
                    playlist.ArchiveDirectory, archivePath, playlist.Name)
                : string.Empty;

            foreach (var track in playlist.Tracks)
            {
                if (track.CurrentPath is { Length: > 0 } primary
                    && File.Exists(primary)
                    && ShouldRelocate(primary, baseFolder, playlistFolder))
                {
                    if (FileRelocator.TryMove(primary, Path.Combine(playlistFolder, Path.GetFileName(primary)), _logger))
                    {
                        track.CurrentPath = Path.Combine(playlistFolder, Path.GetFileName(primary));
                        moved++;
                    }
                }

                if (track.ArchivePath is { Length: > 0 } archive
                    && File.Exists(archive)
                    && ShouldRelocate(archive, archiveBase, archiveFolder))
                {
                    if (FileRelocator.TryMove(archive, Path.Combine(archiveFolder, Path.GetFileName(archive)), _logger))
                    {
                        track.ArchivePath = Path.Combine(archiveFolder, Path.GetFileName(archive));
                        moved++;
                    }
                }
            }
        }

        return moved;
    }

    /// <summary>True when a file sits under the base folder but not yet in
    /// the playlist's own folder.</summary>
    private static bool ShouldRelocate(string path, string? baseFolder, string targetFolder)
        => !string.IsNullOrWhiteSpace(targetFolder)
           && !string.IsNullOrWhiteSpace(baseFolder)
           && !FileRelocator.PathsEqual(targetFolder, baseFolder)
           && FileRelocator.IsUnder(path, baseFolder)
           && !FileRelocator.IsUnder(path, targetFolder);

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
