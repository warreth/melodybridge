using MelodyBridge.Core;
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
    private readonly TrackLinker? _linker;
    private readonly DataChangeTracker? _changes;

    public LibraryReconciler(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        ILogger<LibraryReconciler> logger,
        SettingsStore? settings = null,
        TrackLinker? linker = null,
        DataChangeTracker? changes = null)
    {
        _dbFactory = dbFactory;
        _logger = logger;
        _settings = settings;
        _linker = linker;
        _changes = changes;
    }

    /// <summary>Run once at startup. Returns (relinked, markedPending).</summary>
    public async Task<(int relinked, int lost)> ReconcileAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Only the columns this pass reads. A track row also carries its
        // lyrics and cover url, and a library with tens of thousands of
        // tracks would drag megabytes of text into memory for a pass that
        // never looks at them.
        var playlists = await db.Playlists
            .Select(p => new PlaylistEntity
            {
                Id = p.Id,
                Name = p.Name,
                TargetDirectory = p.TargetDirectory,
                ArchiveDirectory = p.ArchiveDirectory,
                ArchiveEnabled = p.ArchiveEnabled,
                PreferredFormat = p.PreferredFormat,
                Tracks = p.Tracks.Select(t => new TrackEntity
                {
                    Id = t.Id,
                    MelodyId = t.MelodyId,
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
                    SpectrumAccepted = t.SpectrumAccepted,
                    LastSeenAt = t.LastSeenAt,
                    PlaylistEntityId = t.PlaylistEntityId,
                }).ToList(),
            })
            .ToListAsync(ct);

        // A projection is not tracked, so a row this pass changes would
        // never be written. Attach a row when it really changes: only the
        // rows that move enter the change tracker, and the columns the
        // pass does not touch stay as they are in the database.
        void Touch(TrackEntity track)
        {
            if (db.Entry(track).State == EntityState.Detached) db.Attach(track);
        }

        // The relink index needs the same base the folder rules use: a
        // playlist without its own folder lives under the music path, so
        // its files must still be found there by MELODY_ID. Indexing only
        // the raw TargetDirectory made every such row re-download after a
        // manual file move once the page normalized the stored default to
        // null.
        var musicPath = _settings is null
            ? string.Empty
            : await _settings.GetAsync("music_path", "/music", ct);
        var playlistFolders = _settings is null
            || await _settings.GetBoolAsync("playlist_folders", true, ct);

        // Archive copies never satisfy a track: a playlist whose primary
        // file vanished must not count as downloaded just because the
        // archive mirror is still there. Both the configured archive
        // folders and every remembered archive path stay out of the index.
        var archivePath = _settings is null
            ? string.Empty
            : await _settings.GetAsync("archive_path", "", ct);
        var archiveRoots = playlists
            .Select(p => FirstNonEmpty(p.ArchiveDirectory, archivePath))
            .Append(archivePath)
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(FileRelocator.PathComparer)
            .ToList();
        var archivePaths = playlists
            .SelectMany(p => p.Tracks)
            .Select(t => t.ArchivePath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(FileRelocator.PathComparer);

        var relinked = 0;
        var lost = 0;

        // One walk per folder, not per playlist. Reading the MELODY_ID tag
        // of every file in a music folder is the expensive part of this
        // pass, and twenty playlists that live in the same folder used to
        // pay for that walk twenty times.
        var scans = new Dictionary<string, Dictionary<string, List<string>>>(FileRelocator.PathComparer);

        // The dedup switch and the candidates of a playlist are read once
        // per playlist, not once per track: a query per pending track
        // turned a startup pass over a big library into minutes.
        var dedupEnabled = _settings is null
            || await _settings.GetBoolAsync(PlaylistStore.DedupSettingKey, true, ct);

        foreach (var playlist in playlists)
        {
            var dir = FirstNonEmpty(playlist.TargetDirectory, musicPath);
            var ownFolder = PlaylistRules.ResolvePlaylistFolder(
                playlist.TargetDirectory, musicPath, playlist.Name, playlistFolders);
            var filesByMelodyId = string.IsNullOrWhiteSpace(dir)
                ? new Dictionary<string, List<string>>()
                : ScanDirectory(dir, archiveRoots, scans);

            var dedupCandidates = _linker is not null && dedupEnabled
                ? await _linker.FindCandidatesAsync(playlist.Tracks, ct)
                : new Dictionary<string, DedupMatch>();

            var preferred = playlist.PreferredFormat;
            if (string.IsNullOrWhiteSpace(preferred) && _settings is not null)
                preferred = await _settings.GetAsync("default_quality", "auto", ct);
            var quality = PlaylistRules.ParseQuality(preferred);

            foreach (var track in playlist.Tracks)
            {
                // A paused dedup conflict is a pending human decision: no
                // library pass may settle it behind the user's back.
                if (track.DownloadStatus == "needs-review") continue;

                // Fast path: the remembered path still exists, unless it
                // points into an archive folder. An archive copy is not a
                // primary: clear it so the track links or downloads again.
                if (track.CurrentPath is not null && File.Exists(track.CurrentPath))
                {
                    if (PlaylistRules.PathIsUnder(track.CurrentPath, archiveRoots))
                    {
                        Touch(track);
                        if (string.Equals(track.CurrentPath, track.ArchivePath, StringComparison.OrdinalIgnoreCase))
                            track.ArchivePath = null;
                        track.CurrentPath = null;
                        track.DownloadStatus = "pending";
                        track.Warning = string.IsNullOrEmpty(track.Warning)
                            ? "the file lived in the archive folder; download it again or link another playlist's copy"
                            : track.Warning;
                        lost++;
                        continue;
                    }
                    if (track.DownloadStatus != "downloaded")
                    {
                        Touch(track);
                        track.DownloadStatus = "downloaded";
                        track.DownloadError = null;
                        relinked++;
                    }
                    continue;
                }

                // The file found under the base folder: it is this
                // playlist's own when it sits in the playlist's folder,
                // and a foreign path to adopt only as a last resort.
                var found = track.MelodyId is not null
                    && filesByMelodyId.TryGetValue(track.MelodyId, out var files)
                    && PickFile(files, ownFolder) is { } picked
                    && !archivePaths.Contains(picked)
                        ? picked
                        : null;
                var foundIsOwn = found is not null
                    && ownFolder.Length > 0
                    && FileRelocator.IsUnder(found, ownFolder);

                // Another playlist already holds this track: link its
                // bytes into this playlist's own folder (archive copy
                // included) instead of adopting a foreign path. A quality
                // difference is left for a real download: the user never
                // asked for one here, and adopting the foreign file would
                // claim a version this playlist never picked.
                var foreignQualityDiffers = false;
                if (!foundIsOwn
                    && _linker is not null
                    && track.MelodyId is not null
                    && track.DownloadStatus is null or "pending" or "failed"
                    && dedupCandidates.TryGetValue(track.MelodyId, out var candidate))
                {
                    Touch(track);
                    var outcome = await _linker.TryLinkAsync(
                        track, db, quality, pauseOnQualityMismatch: false, ct, candidate,
                        resolvedFolder: ownFolder, dedupEnabled: dedupEnabled);
                    if (outcome == TrackLinkOutcome.Linked)
                    {
                        relinked++;
                        continue;
                    }
                    foreignQualityDiffers = outcome == TrackLinkOutcome.QualityMismatch;
                }

                // Slow path: the file found by its MELODY_ID tag under the
                // playlist folder (files move, ids do not). An archive copy
                // is not the track; it stays out.
                if (found is not null && !foreignQualityDiffers)
                {
                    Touch(track);
                    track.CurrentPath = found;
                    track.DownloadStatus = "downloaded";
                    track.DownloadError = null;
                    track.LastSeenAt = DateTime.UtcNow;
                    // The row now points at different bytes than the ones
                    // the old review and any Keep decision described.
                    track.ReviewKind = null;
                    track.ReviewText = null;
                    track.SpectrumAccepted = false;
                    relinked++;
                    continue;
                }

                // Nothing on disk: schedule a re-download. Any open quality
                // review described the vanished file, so it goes with it.
                if (track.DownloadStatus == "downloaded")
                {
                    Touch(track);
                    track.DownloadStatus = "pending";
                    track.CurrentPath = null;
                    track.Warning = "file missing on disk, will re-download";
                    track.ReviewKind = null;
                    track.ReviewText = null;
                    track.SpectrumAccepted = false;
                    track.SourcePluginId = null;
                    lost++;
                }
            }
        }

        var moved = await RelocateLooseFilesAsync(playlists, Touch, ct);

        if (relinked > 0 || lost > 0 || moved > 0)
        {
            await db.SaveChangesAsync(ct);
            _changes?.Bump();
        }

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
        List<PlaylistEntity> playlists, Action<TrackEntity> touch, CancellationToken ct)
    {
        if (_settings is null) return 0;

        var musicPath = await _settings.GetAsync("music_path", "/music", ct);
        var archivePath = await _settings.GetAsync("archive_path", "", ct);
        var playlistFolders = await _settings.GetBoolAsync("playlist_folders", true, ct);

        var layout = new Dictionary<string, PlaylistLayout>(StringComparer.Ordinal);
        foreach (var playlist in playlists)
        {
            layout[playlist.Id] = new PlaylistLayout(
                Base: FirstNonEmpty(playlist.TargetDirectory, musicPath),
                Folder: PlaylistRules.ResolvePlaylistFolder(
                    playlist.TargetDirectory, musicPath, playlist.Name, playlistFolders),
                ArchiveBase: FirstNonEmpty(playlist.ArchiveDirectory, archivePath),
                ArchiveFolder: playlist.ArchiveEnabled
                    ? PlaylistRules.ResolveArchiveFolder(
                        playlist.ArchiveDirectory, archivePath, playlist.Name, playlistFolders)
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
            foreach (var (track, _) in refs) { touch(track); track.CurrentPath = destination; }
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
            foreach (var (track, _) in refs) { touch(track); track.ArchivePath = destination; }
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

    /// <summary>MELODY_ID tag -> the files that carry it, one entry per
    /// audio file in the tree. Files under an archive folder never enter
    /// the index. The scan of a folder is kept, so a second playlist in
    /// the same folder reuses it instead of reading every tag again.</summary>
    private static Dictionary<string, List<string>> ScanDirectory(
        string dir, List<string> excludedRoots,
        Dictionary<string, Dictionary<string, List<string>>> scans)
    {
        if (scans.TryGetValue(dir, out var cached)) return cached;

        var index = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var extensions = new[] { ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac", ".wav", ".webm" };
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                if (!extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;
                if (excludedRoots.Any(root => FileRelocator.IsUnder(file, root)))
                    continue;
                var id = TaglibHelper.ReadMelodyId(file);
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (!index.TryGetValue(id, out var files))
                {
                    files = new List<string>();
                    index[id] = files;
                }
                files.Add(file);
            }
        }
        catch (Exception)
        {
            // Unreadable folder: leave the index partial rather than failing the run.
        }
        scans[dir] = index;
        return index;
    }

    /// <summary>The file a playlist adopts: the first one that sits in the
    /// playlist's own folder, otherwise the first one the scan found.</summary>
    private static string? PickFile(List<string> files, string preferUnder)
    {
        if (files.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(preferUnder))
            foreach (var file in files)
                if (FileRelocator.IsUnder(file, preferUnder))
                    return file;
        return files[0];
    }
}
