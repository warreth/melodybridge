using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Repairs the connection between playlist rows and files that are
/// already on disk: adopts orphan files by MELODY_ID or by their tags,
/// rewrites old random ids to the deterministic id, and reports duplicate
/// copies so a removed-and-re-added playlist does not leave " (2)" files
/// behind forever. Nothing here deletes audio unless the caller asks for
/// the orphan list and every safety check still passes.
/// </summary>
public sealed class LibraryFileRepair
{
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly SettingsStore _settings;
    private readonly TrackLinker _linker;
    private readonly ILogger<LibraryFileRepair> _logger;

    public LibraryFileRepair(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        SettingsStore settings,
        TrackLinker linker,
        ILogger<LibraryFileRepair> logger)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _linker = linker;
        _logger = logger;
    }

    /// <summary>
    /// Adopts pending rows and normalizes legacy tags of downloaded rows.
    /// Safe to run repeatedly; a file owned by another row is never
    /// rewritten.
    /// </summary>
    public async Task<FileLinkRepairReport> RepairAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var playlistIds = await db.Playlists.AsNoTracking()
            .Select(p => p.Id)
            .ToListAsync(ct);

        var linked = 0;
        foreach (var playlistId in playlistIds)
        {
            ct.ThrowIfCancellationRequested();
            linked += await _linker.LinkSharedTracksAsync(playlistId, ct);
        }

        var allRows = await db.Tracks.AsNoTracking()
            .Where(t => t.MelodyId != null)
            .Select(t => new TrackEntity
            {
                Id = t.Id,
                MelodyId = t.MelodyId,
                Artist = t.Artist,
                Title = t.Title,
                DurationMs = t.DurationMs,
                CurrentPath = t.CurrentPath,
                ArchivePath = t.ArchivePath,
                DownloadStatus = t.DownloadStatus,
                PlaylistEntityId = t.PlaylistEntityId,
            })
            .ToListAsync(ct);
        var ownedIds = allRows
            .Where(t => t.MelodyId is { Length: > 0 })
            .Select(t => t.MelodyId!)
            .ToHashSet(StringComparer.Ordinal);
        var pathOwners = new Dictionary<string, HashSet<int>>(FileRelocator.PathComparer);
        foreach (var row in allRows)
        {
            foreach (var path in new[] { row.CurrentPath, row.ArchivePath })
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                if (!pathOwners.TryGetValue(path, out var owners))
                    pathOwners[path] = owners = new HashSet<int>();
                owners.Add(row.Id);
            }
        }

        var rewritten = 0;
        var ambiguous = 0;
        foreach (var playlist in await PlaylistsAsync(db, ct))
        {
            ct.ThrowIfCancellationRequested();
            var folder = await FolderAsync(playlist, ct);
            if (string.IsNullOrWhiteSpace(folder)) continue;
            var rows = playlist.Tracks
                .Where(t => t.DownloadStatus == "downloaded" && t.MelodyId is { Length: > 0 })
                .ToList();
            if (rows.Count == 0) continue;

            var index = MelodyFileIndex.Build(
                folder, await ArchiveRootsAsync(db, ct),
                rows.Select(t => t.MelodyId!).ToHashSet(StringComparer.Ordinal));
            foreach (var file in index.Files)
            {
                if (file.MelodyId is { Length: > 0 } id && rows.Any(r =>
                        string.Equals(r.MelodyId, id, StringComparison.Ordinal)))
                    continue;

                var identity = index.Identity(file.Path);
                if (string.IsNullOrWhiteSpace(identity.Title) || string.IsNullOrWhiteSpace(identity.Artist))
                    continue;

                var matches = rows
                    .Where(row => FuzzyMatcher.Confidence(
                            row.Artist, row.Title, identity.Artist, identity.Title)
                        == MatchConfidence.High
                        && FuzzyMatcher.DurationPlausible(DurationOf(row), identity.Duration))
                    .ToList();
                if (matches.Count != 1)
                {
                    if (matches.Count > 1) ambiguous++;
                    continue;
                }

                var matched = matches[0];
                if (file.MelodyId is { Length: > 0 } oldId
                    && ownedIds.Contains(oldId)
                    && !string.Equals(oldId, matched.MelodyId, StringComparison.Ordinal))
                    continue; // another row's id: never steal it
                if (pathOwners.TryGetValue(file.Path, out var owners)
                    && owners.Any(owner => owner != matched.Id))
                    continue; // another row points at this path
                if (string.Equals(file.MelodyId, matched.MelodyId, StringComparison.Ordinal))
                    continue; // already the deterministic id

                TaglibHelper.WriteMelodyId(file.Path, matched.MelodyId!);
                rewritten++;
                _logger.LogInformation(
                    "Library repair: rewrote the MELODY_ID of {Path} to {MelodyId}",
                    file.Path, matched.MelodyId);
            }
        }

        if (linked > 0 || rewritten > 0)
        {
            _logger.LogInformation(
                "Library repair: linked {Linked} track(s), rewrote {Rewritten} legacy tag(s), {Ambiguous} ambiguous file(s)",
                linked, rewritten, ambiguous);
        }
        return new FileLinkRepairReport(linked, rewritten, ambiguous, playlistIds.Count);
    }

    /// <summary>
    /// Duplicate copies of the same track in a playlist folder. A file a
    /// row still points at is the keeper; only unreferenced copies are
    /// offered for removal. Two files with no owning row stay a report.
    /// </summary>
    public async Task<DuplicateFileReport> FindDuplicatesAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var archiveRoots = await ArchiveRootsAsync(db, ct);
        var referenced = (await db.Tracks.AsNoTracking()
                .Where(t => t.CurrentPath != null || t.ArchivePath != null)
                .Select(t => new { t.CurrentPath, t.ArchivePath })
                .ToListAsync(ct))
            .SelectMany(t => new[] { t.CurrentPath, t.ArchivePath })
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToHashSet(FileRelocator.PathComparer);

        var groups = new List<DuplicateFileGroup>();
        foreach (var playlist in await PlaylistsAsync(db, ct))
        {
            ct.ThrowIfCancellationRequested();
            var folder = await FolderAsync(playlist, ct);
            if (string.IsNullOrWhiteSpace(folder)) continue;
            var index = MelodyFileIndex.Build(folder, archiveRoots);
            foreach (var byId in index.Files
                         .Where(f => f.MelodyId is { Length: > 0 })
                         .GroupBy(f => f.MelodyId!, StringComparer.Ordinal)
                         .Where(g => g.Count() > 1))
            {
                var paths = byId.Select(f => f.Path).ToList();
                var rows = playlist.Tracks
                    .Where(t => string.Equals(t.MelodyId, byId.Key, StringComparison.Ordinal))
                    .ToList();
                var keeper = paths.FirstOrDefault(path => rows.Any(row =>
                    FileRelocator.PathsEqual(row.CurrentPath, path)
                    || FileRelocator.PathsEqual(row.ArchivePath, path)));
                var orphans = paths
                    .Where(path => keeper is null || !FileRelocator.PathsEqual(keeper, path))
                    .Where(path => !referenced.Contains(path))
                    .ToList();
                if (orphans.Count == 0 && keeper is not null) continue;

                groups.Add(new DuplicateFileGroup(
                    playlist.Id, playlist.Name, rows.FirstOrDefault()?.Title,
                    byId.Key, keeper, orphans));
            }
        }

        return new DuplicateFileReport(groups);
    }

    /// <summary>
    /// Cleans the duplicate groups from a fresh report. When the keeper
    /// carries a numbered name and the plain name is an unreferenced copy
    /// of the same track, the old copy is removed and the keeper takes the
    /// plain name back, so a re-added playlist does not stay stuck on
    /// " (2)". Every action is re-checked against the live rows.
    /// </summary>
    public async Task<DuplicateCleanupReport> CleanDuplicatesAsync(
        IReadOnlyCollection<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0) return new DuplicateCleanupReport(0, 0);
        var report = await FindDuplicatesAsync(ct);
        var requested = paths.ToHashSet(FileRelocator.PathComparer);
        var removed = 0;
        var renamed = 0;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        foreach (var group in report.Groups)
        {
            ct.ThrowIfCancellationRequested();
            if (group.Keeper is null) continue;
            var groupOrphans = group.Orphans
                .Where(path => requested.Contains(path))
                .ToList();
            if (groupOrphans.Count == 0) continue;

            var plain = PlainNameFor(group.Keeper, group.Orphans);
            if (plain is not null)
            {
                // The plain name is an orphan of the same track. Remove it
                // first, then move the keeper over it and repoint its rows.
                if (!await DeleteSafeAsync(db, plain, ct)) continue;
                removed++;
                try
                {
                    File.Move(group.Keeper, plain);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Library repair: could not restore the plain name {Path}", plain);
                    continue;
                }
                await db.Tracks
                    .Where(t => t.CurrentPath == group.Keeper || t.ArchivePath == group.Keeper)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(t => t.CurrentPath,
                            t => t.CurrentPath == group.Keeper ? plain : t.CurrentPath)
                        .SetProperty(t => t.ArchivePath,
                            t => t.ArchivePath == group.Keeper ? plain : t.ArchivePath), ct);
                renamed++;
                _logger.LogInformation(
                    "Library repair: restored {Plain} from the numbered duplicate {Numbered}",
                    plain, group.Keeper);
            }

            foreach (var orphan in groupOrphans)
            {
                if (plain is not null && FileRelocator.PathsEqual(orphan, plain)) continue;
                if (!await DeleteSafeAsync(db, orphan, ct)) continue;
                removed++;
            }
        }

        return new DuplicateCleanupReport(removed, renamed);
    }

    /// <summary>
    /// The plain name a numbered keeper can take back, when an orphan of
    /// the same track occupies exactly that name. Null when the keeper is
    /// not numbered or the plain path is not one of the orphans.
    /// </summary>
    private static string? PlainNameFor(string keeper, IReadOnlyList<string> orphans)
    {
        var name = Path.GetFileNameWithoutExtension(keeper);
        var match = System.Text.RegularExpressions.Regex.Match(
            name, @"^(.*) \(\d+\)$");
        if (!match.Success) return null;
        var plain = Path.Combine(
            Path.GetDirectoryName(keeper) ?? "", match.Groups[1].Value + Path.GetExtension(keeper));
        return orphans.Any(orphan => FileRelocator.PathsEqual(orphan, plain)) ? plain : null;
    }

    /// <summary>True when the path is still an unreferenced file on disk.</summary>
    private static async Task<bool> DeleteSafeAsync(
        MelodyBridgeDbContext db, string path, CancellationToken ct)
    {
        if (!File.Exists(path)) return false;
        var referenced = await db.Tracks.AsNoTracking()
            .AnyAsync(t => t.CurrentPath == path || t.ArchivePath == path, ct);
        if (referenced) return false;
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<List<PlaylistEntity>> PlaylistsAsync(
        MelodyBridgeDbContext db, CancellationToken ct)
        => await db.Playlists.AsNoTracking()
            .Include(p => p.Tracks)
            .ToListAsync(ct);

    private async Task<string> FolderAsync(PlaylistEntity playlist, CancellationToken ct)
    {
        var musicPath = await _settings.GetAsync("music_path", "/music", ct);
        var playlistFolders = await _settings.GetBoolAsync("playlist_folders", true, ct);
        return PlaylistRules.ResolvePlaylistFolder(
            playlist.TargetDirectory, musicPath, playlist.Name, playlistFolders);
    }

    private async Task<List<string>> ArchiveRootsAsync(
        MelodyBridgeDbContext db, CancellationToken ct)
    {
        var roots = await db.Playlists.AsNoTracking()
            .Where(p => p.ArchiveDirectory != null && p.ArchiveDirectory != "")
            .Select(p => p.ArchiveDirectory!)
            .ToListAsync(ct);
        var global = await _settings.GetAsync("archive_path", "", ct);
        if (!string.IsNullOrWhiteSpace(global)) roots.Add(global);
        roots.AddRange(await db.ArchiveProfiles.AsNoTracking()
            .Where(profile => profile.RootPath != "")
            .Select(profile => profile.RootPath)
            .ToListAsync(ct));
        return roots;
    }

    private static TimeSpan? DurationOf(TrackEntity track)
        => track.DurationMs is > 0
            ? TimeSpan.FromMilliseconds(track.DurationMs.Value)
            : null;
}

/// <summary>What a repair pass changed.</summary>
public sealed record FileLinkRepairReport(
    int Linked,
    int TagsRewritten,
    int Ambiguous,
    int Playlists);

/// <summary>One track id that exists more than once in a playlist folder.</summary>
public sealed record DuplicateFileGroup(
    string PlaylistId,
    string PlaylistName,
    string? TrackTitle,
    string? MelodyId,
    string? Keeper,
    IReadOnlyList<string> Orphans);

/// <summary>What a duplicate cleanup changed.</summary>
public sealed record DuplicateCleanupReport(int Removed, int Renamed);

/// <summary>Every duplicate group the repair pass found.</summary>
public sealed record DuplicateFileReport(IReadOnlyList<DuplicateFileGroup> Groups)
{
    public int OrphanCount => Groups.Sum(group => group.Orphans.Count);
    public bool CanRemove => Groups.Any(g => g.Keeper is not null && g.Orphans.Count > 0);
}
