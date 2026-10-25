using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Links a playlist row to a copy of the same track that already exists
/// elsewhere in the library: the bytes are hardlinked into the playlist's
/// own folder and into its archive folder, so a shared track is never
/// downloaded twice. The download claim uses it before any network call
/// (a quality mismatch pauses the track for a human decision); the
/// playlist add/refresh and the library reconciliation passes run it
/// unprompted, where a mismatch is simply left for a later download.
/// Copying is never a fallback here: when a hard link is impossible the
/// honest options are a real download or a user decision.
/// </summary>
/// <summary>What one archive attempt produced: no configured target,
/// a written copy, or a failure recorded as a track warning.</summary>
public enum ArchiveOutcome
{
    Skipped,
    Written,
    Failed,
}

public sealed class TrackLinker
{
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly SettingsStore _settings;
    private readonly HardLinkService _hardLinks;
    private readonly ILogger<TrackLinker> _logger;
    private readonly ITrackClaimRegistry _claims;
    private readonly DataChangeTracker? _changes;
    private readonly PathLockTable _pathLocks;

    public TrackLinker(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        SettingsStore settings,
        HardLinkService hardLinks,
        ILogger<TrackLinker> logger,
        DataChangeTracker? changes = null,
        ITrackClaimRegistry? claims = null,
        PathLockTable? pathLocks = null)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _hardLinks = hardLinks;
        _logger = logger;
        _changes = changes;
        // The claims that are live in this process: a linking pass holds
        // its row, so a batch that starts now leaves it alone.
        _claims = claims ?? new LiveTrackClaims();
        // One writer per file name, shared by every worker in the process.
        _pathLocks = pathLocks ?? PathLockTable.Shared;
    }

    /// <summary>
    /// Best existing file for every melody id of a set of tracks, found in
    /// one query. The reconciliation asks this for every pending track of
    /// every playlist, and a query per track turned a startup pass over a
    /// big library into minutes.
    /// </summary>
    public async Task<Dictionary<string, DedupMatch>> FindCandidatesAsync(
        IReadOnlyCollection<TrackEntity> tracks, CancellationToken ct = default)
    {
        var result = new Dictionary<string, DedupMatch>(StringComparer.Ordinal);
        var ids = tracks
            .Where(t => t.MelodyId is { Length: > 0 })
            .Select(t => t.MelodyId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0) return result;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Tracks.AsNoTracking()
            .Where(t => ids.Contains(t.MelodyId!)
                && t.DownloadStatus == "downloaded"
                && t.ReviewKind == null
                && t.CurrentPath != null)
            .OrderByDescending(t => t.IsHardLink == false)
            .ThenByDescending(t => t.LastSeenAt ?? DateTime.MinValue)
            .ToListAsync(ct);
        if (rows.Count == 0) return result;

        var archiveRoots = await ArchiveRootsAsync(db, ct);
        var ownIds = tracks.Select(t => t.Id).ToHashSet();
        foreach (var row in rows)
        {
            // The first row per id wins: the order above already put the
            // original before the hardlink and the newest first.
            if (row.MelodyId is not { Length: > 0 } || result.ContainsKey(row.MelodyId)) continue;
            if (ownIds.Contains(row.Id)) continue;
            if (!File.Exists(row.CurrentPath)) continue;
            // A row whose "primary" actually sits in an archive folder is
            // not a primary: linking it would turn an archive copy into
            // another playlist's download.
            if (PlaylistRules.PathIsUnder(row.CurrentPath, archiveRoots)) continue;
            result[row.MelodyId] = ToMatch(row);
        }
        return result;
    }

    /// <summary>
    /// Best existing file for the melody id: a downloaded row in another
    /// playlist (or another row of this one), whose file is still on disk.
    /// Originals win over hardlinks so the link target is the primary copy,
    /// and newer rows win over stale ones.
    /// </summary>
    public async Task<DedupMatch?> FindCandidateAsync(
        string melodyId, int excludingTrackId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.Tracks.AsNoTracking()
            .Where(t => t.MelodyId == melodyId
                && t.Id != excludingTrackId
                && t.DownloadStatus == "downloaded"
                && t.ReviewKind == null
                && t.CurrentPath != null)
            .OrderByDescending(t => t.IsHardLink == false)
            .ThenByDescending(t => t.LastSeenAt ?? DateTime.MinValue)
            .ToListAsync(ct);

        var archiveRoots = await ArchiveRootsAsync(db, ct);
        foreach (var row in existing)
        {
            if (!File.Exists(row.CurrentPath)) continue;
            // A row whose "primary" actually sits in an archive folder is
            // not a primary: linking it would turn an archive copy into
            // another playlist's download.
            if (PlaylistRules.PathIsUnder(row.CurrentPath, archiveRoots)) continue;
            return ToMatch(row);
        }
        return null;
    }

    /// <summary>Every configured archive root: the app-wide archive path
    /// plus each playlist's own archive folder.</summary>
    private async Task<List<string>> ArchiveRootsAsync(MelodyBridgeDbContext db, CancellationToken ct)
    {
        var roots = await db.Playlists.AsNoTracking()
            .Where(p => p.ArchiveDirectory != null && p.ArchiveDirectory != "")
            .Select(p => p.ArchiveDirectory!)
            .ToListAsync(ct);
        var global = await _settings.GetAsync("archive_path", "", ct);
        if (!string.IsNullOrWhiteSpace(global)) roots.Add(global);
        return roots;
    }

    /// <summary>
    /// Links this row to an existing copy when the quality matches. The
    /// row's playlist folder and its configured archive format decide
    /// where the names land. Every caller saves the context afterwards.
    /// </summary>
    /// <param name="pauseOnQualityMismatch">
    /// true for a download claim: a mismatch pauses the row as
    /// "needs-review" with the conflict spelled out. false for the
    /// unprompted passes: the row is left untouched for a later download.
    /// </param>
    /// <param name="candidate">
    /// A pre-fetched candidate (batch passes); null looks it up.
    /// </param>
    /// <param name="acceptQualityDifference">
    /// true only for the user's explicit "Hardlink existing" choice:
    /// the different quality is linked anyway.
    /// </param>
    /// <param name="warning">Row warning text for the linked state.</param>
    /// <param name="resolvedFolder">
    /// The playlist folder, when the caller already knows it. Reading it
    /// again costs a query per track.
    /// </param>
    /// <param name="dedupEnabled">
    /// The dedup setting, when the caller already read it.
    /// </param>
    public async Task<TrackLinkOutcome> TryLinkAsync(
        TrackEntity track, MelodyBridgeDbContext db, DownloadQuality requested,
        bool pauseOnQualityMismatch, CancellationToken ct,
        DedupMatch? candidate = null, bool acceptQualityDifference = false,
        string? warning = null, string? resolvedFolder = null, bool? dedupEnabled = null)
    {
        if (track.MelodyId is null) return TrackLinkOutcome.NoCandidate;
        if (!(dedupEnabled ?? await _settings.GetBoolAsync(PlaylistStore.DedupSettingKey, true, ct)))
            return TrackLinkOutcome.NoCandidate;

        candidate ??= await FindCandidateAsync(track.MelodyId, track.Id, ct);
        if (candidate is null) return TrackLinkOutcome.NoCandidate;

        if (!acceptQualityDifference
            && !DedupQuality.Matches(candidate.ExistingFormat, candidate.ExistingBitrateKbps, requested))
        {
            if (pauseOnQualityMismatch)
            {
                // Pause for a human: the requested quality differs from the
                // file another playlist owns. Both options are spelled out
                // for the UI; nothing is guessed, nothing is overwritten.
                track.DownloadStatus = "needs-review";
                track.DownloadError = null;
                track.Warning = $"dedup conflict: playlist wants {PlaylistRules.DescribeQuality(requested)}, "
                    + $"existing file is {candidate.Describe()}. Choose hardlink existing or download new.";
                _logger.LogInformation(
                    "Dedup conflict for '{Title}': existing {Existing} vs requested {Requested}; paused for review",
                    track.Title, candidate.Describe(), PlaylistRules.DescribeQuality(requested));
            }
            return TrackLinkOutcome.QualityMismatch;
        }

        var folder = resolvedFolder ?? await ResolvePlaylistFolderAsync(track.PlaylistEntityId, db, ct);
        if (string.IsNullOrWhiteSpace(folder)) return TrackLinkOutcome.LinkFailed;

        // Link the existing file under its own name into this playlist's
        // folder. A link failure (cross-device EXDEV, read-only target,
        // ...) leaves the row for the caller: a real download or a user
        // decision, never a silent copy.
        var linkPath = Path.Combine(folder, Path.GetFileName(candidate.ExistingPath));
        var alreadyOwnName = PlaylistRules.PathEquals(linkPath, candidate.ExistingPath);
        if (!alreadyOwnName)
        {
            // One writer per name: the decision (is the name free, may I
            // replace what is there) and the link itself happen under the
            // same lock the writers take. Without it, two linking passes
            // that picked the same free name in the same moment link over
            // each other and a row ends on the other song's bytes.
            using (var scope = await _pathLocks.AcquireAsync(linkPath, ct))
            {
                var replaceExisting = File.Exists(linkPath)
                    && !_hardLinks.SameFile(linkPath, candidate.ExistingPath)
                    && await NameIsFreeAsync(db, track, linkPath, ct);
                if (!TryHardLink(linkPath, candidate.ExistingPath, replaceExisting, out var linkFailure))
                {
                    _logger.LogInformation(
                        "Hard link for '{Title}' failed ({Source} -> {Target}): {Reason}; leaving the track for a real download",
                        track.Title, candidate.ExistingPath, linkPath, linkFailure ?? "unknown error");
                    return TrackLinkOutcome.LinkFailed;
                }
            }
        }

        track.DownloadStatus = "downloaded";
        track.IsHardLink = !alreadyOwnName;
        track.CurrentPath = linkPath;
        track.LastSeenAt = DateTime.UtcNow;
        track.Bitrate = candidate.ExistingBitrateKbps;
        track.MediaType = candidate.ExistingFormat;
        AudioProbe.Fill(track, linkPath);
        // The linked file was verified where it came from; no review
        // carries over, and there is no provider to avoid.
        track.ReviewKind = null;
        track.ReviewText = null;
        track.SpectrumAccepted = false;
        track.SourcePluginId = null;
        track.Warning = warning;

        await LinkArchiveAsync(track, candidate, db, ct);

        _logger.LogInformation(
            alreadyOwnName
                ? "Linked '{Title}' to its existing file name in {Folder} (zero new bytes)"
                : "Dedup: hardlinked '{Title}' from {Source} into {Target} (zero new bytes)",
            track.Title, candidate.ExistingPath, linkPath);
        return TrackLinkOutcome.Linked;
    }

    /// <summary>
    /// Links the tracks of a playlist that already exist elsewhere in the
    /// library, without a network call and without writing an unsolicited
    /// review. Called right after a playlist is added or refreshed, so a
    /// playlist whose tracks the library already holds owns its own file
    /// names (and archive copies) from the first moment; the per-track
    /// download claim then has nothing left to do for them.
    /// </summary>
    public async Task<int> LinkSharedTracksAsync(string playlistId, CancellationToken ct = default)
    {
        if (!await _settings.GetBoolAsync(PlaylistStore.DedupSettingKey, true, ct)) return 0;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var playlist = await db.Playlists
            .Include(p => p.Tracks)
            .FirstOrDefaultAsync(p => p.Id == playlistId, ct);
        if (playlist is null) return 0;

        var linked = await LinkPendingTracksAsync(playlist, db, ct);
        if (linked > 0)
        {
            await db.SaveChangesAsync(ct);
            _changes?.Bump();
            _logger.LogInformation(
                "Linked {Count} shared {Word} of '{Playlist}' to existing files (zero new bytes)",
                linked, linked == 1 ? "track" : "tracks", playlist.Name);
        }
        return linked;
    }

    /// <summary>
    /// The linking pass over an already loaded playlist, in the caller's
    /// context: used by the add/refresh path and by library
    /// reconciliation, which both save the context themselves. One
    /// candidate query covers the whole playlist; only exact matches are
    /// linked, everything else stays exactly as it was.
    /// </summary>
    public async Task<int> LinkPendingTracksAsync(
        PlaylistEntity playlist, MelodyBridgeDbContext db, CancellationToken ct = default)
    {
        var pending = playlist.Tracks
            .Where(t => t.MelodyId is not null
                && !t.SkipDedup
                && t.DownloadStatus is null or "pending" or "failed")
            .ToList();
        if (pending.Count == 0) return 0;

        if (!await _settings.GetBoolAsync(PlaylistStore.DedupSettingKey, true, ct)) return 0;

        var musicPath = await _settings.GetAsync("music_path", "/music", ct);
        var playlistFolders = await _settings.GetBoolAsync("playlist_folders", true, ct);
        var folder = PlaylistRules.ResolvePlaylistFolder(
            playlist.TargetDirectory, musicPath, playlist.Name, playlistFolders);
        if (string.IsNullOrWhiteSpace(folder)) return 0;

        var preferredFormat = playlist.PreferredFormat;
        if (string.IsNullOrWhiteSpace(preferredFormat))
            preferredFormat = await _settings.GetAsync("default_quality", "auto", ct);
        var requested = PlaylistRules.ParseQuality(preferredFormat);

        var byMelodyId = await LoadCandidatesAsync(pending, ct);

        var linked = 0;
        foreach (var track in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (!byMelodyId.TryGetValue(track.MelodyId!, out var rows)) continue;

            var candidate = PickFor(track, rows);
            if (candidate is null) continue;

            // The same claim the download pipeline uses: a concurrent run
            // and this pass never link (or download) the same row twice.
            // Whatever happens next, the claim must be released: a row left
            // in_progress would be skipped by every later download until
            // the next restart. The claim is registered before the row is
            // claimed, so a batch that starts now sees a live pass.
            var previousStatus = track.DownloadStatus;
            using var claim = _claims.Claim(track.Id);
            var claimed = await db.Tracks
                .Where(t => t.Id == track.Id
                    && (t.DownloadStatus == null || t.DownloadStatus == "pending" || t.DownloadStatus == "failed"))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.DownloadStatus, "in_progress"), ct);
            if (claimed != 1) continue;

            var linkedThisTrack = false;
            try
            {
                var outcome = await TryLinkAsync(track, db, requested,
                    pauseOnQualityMismatch: false, ct, candidate: candidate.Match,
                    resolvedFolder: folder, dedupEnabled: true);
                linkedThisTrack = outcome == TrackLinkOutcome.Linked;
                if (linkedThisTrack) linked++;
            }
            finally
            {
                if (!linkedThisTrack)
                {
                    // Leave the row exactly as it was: no review, no error.
                    await RestoreStatusAsync(db, track.Id, previousStatus, ct);
                }
            }
        }
        return linked;
    }

    /// <summary>The claim release: back to the status the row had before.</summary>
    private static async Task RestoreStatusAsync(
        MelodyBridgeDbContext db, int trackId, string? previousStatus, CancellationToken ct)
    {
        try
        {
            await db.Tracks.Where(t => t.Id == trackId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.DownloadStatus, previousStatus), ct);
        }
        catch (Exception)
        {
            // Best effort: the startup stale-claim recovery is the safety
            // net, and swallowing here keeps the original failure visible.
        }
    }

    /// <summary>One query for every candidate row of the pending tracks.</summary>
    private async Task<Dictionary<string, List<CandidateRow>>> LoadCandidatesAsync(
        List<TrackEntity> pending, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var melodyIds = pending.Select(t => t.MelodyId!).Distinct().ToList();
        var rows = await db.Tracks.AsNoTracking()
            .Where(t => t.MelodyId != null && melodyIds.Contains(t.MelodyId)
                && t.DownloadStatus == "downloaded"
                && t.ReviewKind == null
                && t.CurrentPath != null)
            .OrderByDescending(t => t.IsHardLink == false)
            .ThenByDescending(t => t.LastSeenAt ?? DateTime.MinValue)
            .ToListAsync(ct);

        // The eager add/refresh pass must skip archive-sitting rows just
        // like FindCandidateAsync does, or an archive copy becomes the
        // linked primary before the next reconcile can clear the pointer.
        var archiveRoots = await ArchiveRootsAsync(db, ct);
        var map = new Dictionary<string, List<CandidateRow>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (PlaylistRules.PathIsUnder(row.CurrentPath, archiveRoots)) continue;
            if (!map.TryGetValue(row.MelodyId!, out var list))
                map[row.MelodyId!] = list = new List<CandidateRow>();
            list.Add(new CandidateRow(row.Id, ToMatch(row)));
        }
        return map;
    }

    /// <summary>A candidate row: the row id lets a pass skip the track's own row.</summary>
    private sealed record CandidateRow(int TrackId, DedupMatch Match);

    /// <summary>
    /// The first candidate that is not this row and whose file still
    /// exists. Candidates are ordered originals-first by the query.
    /// </summary>
    private static CandidateRow? PickFor(TrackEntity track, List<CandidateRow> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.TrackId == track.Id) continue;
            if (!File.Exists(candidate.Match.ExistingPath)) continue;
            return candidate;
        }
        return null;
    }

    private static DedupMatch ToMatch(TrackEntity row) => new(
        row.CurrentPath!,
        Path.GetExtension(row.CurrentPath).TrimStart('.').ToLowerInvariant(),
        row.Bitrate,
        row.PlaylistEntityId,
        // Quality facts live on the row; a missing bitrate is treated as
        // unverifiable (mismatch) by the verdict.
        row.Bitrate is > 0,
        row.ArchivePath);

    private async Task<string> ResolvePlaylistFolderAsync(
        string? playlistId, MelodyBridgeDbContext db, CancellationToken ct)
    {
        PlaylistEntity? playlist = null;
        if (playlistId is { Length: > 0 })
        {
            playlist = await db.Playlists.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == playlistId, ct);
        }
        var musicPath = await _settings.GetAsync("music_path", "/music", ct);
        var playlistFolders = await _settings.GetBoolAsync("playlist_folders", true, ct);
        return PlaylistRules.ResolvePlaylistFolder(
            playlist?.TargetDirectory, musicPath, playlist?.Name, playlistFolders);
    }

    /// <summary>
    /// The archive half of a link: the copy belongs to the track, but each
    /// playlist keeps its own archive folder. When the original already
    /// has one in the format this playlist wants, its bytes are linked
    /// under this playlist's archive name instead of writing a second
    /// copy. A different format (or no copy yet) gets this playlist's own
    /// copy from its primary, so the configured format is never silently
    /// bypassed.
    /// </summary>
    private async Task LinkArchiveAsync(
        TrackEntity track, DedupMatch candidate, MelodyBridgeDbContext db, CancellationToken ct)
    {
        var archiveTarget = await ResolveArchiveTargetAsync(track.PlaylistEntityId, db, ct);
        if (!archiveTarget.Enabled || archiveTarget.Folder is null)
        {
            track.ArchivePath = null;
            track.ArchiveSizeBytes = null;
            return;
        }

        if (candidate.ExistingArchivePath is { Length: > 0 } existingArchive
            && File.Exists(existingArchive)
            && Path.GetExtension(existingArchive).Equals(
                ArchiveCopier.TargetExtension(track.CurrentPath ?? existingArchive, archiveTarget.Format),
                StringComparison.OrdinalIgnoreCase))
        {
            var archiveFileName = Path.GetFileName(existingArchive);
            var archiveLink = Path.Combine(archiveTarget.Folder, archiveFileName);
            // One writer per name, the same rule the archive copy follows:
            // the second worker waits and then sees what the first one
            // left there.
            using (var scope = await _pathLocks.AcquireAsync(archiveLink, ct))
            {
                var replaceExisting = !PlaylistRules.PathEquals(archiveLink, existingArchive)
                    && File.Exists(archiveLink)
                    && !_hardLinks.SameFile(archiveLink, existingArchive)
                    && await NameIsFreeAsync(db, track, archiveLink, ct);
                if (PlaylistRules.PathEquals(archiveLink, existingArchive)
                    || TryHardLink(archiveLink, existingArchive, replaceExisting, out _))
                {
                    track.ArchivePath = archiveLink;
                    track.ArchiveSizeBytes = FileSizeOf(archiveLink);
                    return;
                }
            }
        }
        await ArchiveDownloadedTrackAsync(track, db, ct);
    }

    /// <summary>
    /// Produces the secondary archive copy of a just-downloaded track:
    /// the playlist's archive folder (its own override or the app-wide
    /// default plus a folder named after the playlist), a plain copy for
    /// format auto, a local ffmpeg transcode otherwise. A playlist with
    /// the archive switched off, or no archive path anywhere, gets no
    /// copy and no archive path. Never throws and never touches the
    /// primary: a failed archive becomes a track warning the next run can
    /// repair, while the download stays "downloaded". Runs after the
    /// integrity gate, so the archive path persists with the track row in
    /// the same SaveChanges.
    /// </summary>
    public async Task<ArchiveOutcome> ArchiveDownloadedTrackAsync(
        TrackEntity track, MelodyBridgeDbContext db, CancellationToken ct)
    {
        var target = await ResolveArchiveTargetAsync(track.PlaylistEntityId, db, ct);
        if (!target.Enabled || target.Folder is null)
        {
            // No archive target (or the playlist switched it off): clear
            // a stale archive path from an earlier configuration so the
            // UI never shows a file that no longer belongs to this setup.
            track.ArchivePath = null;
            return ArchiveOutcome.Skipped;
        }

        // The copy lands on a fixed name. Another row can own that name:
        // replacing it would take that track's file away, so the copy is
        // refused instead. The link path checks the same thing.
        var targetPath = ArchiveCopier.TargetPath(track.CurrentPath!, target.Folder, target.Format);

        // One writer per name. Two tracks can resolve to the same archive
        // file, and two workers can pick them up at the same time. The
        // second one waits here and then sees what the first one wrote,
        // instead of writing over it.
        using var scope = await _pathLocks.AcquireAsync(targetPath, ct);

        if (!await NameIsFreeAsync(db, track, targetPath, ct))
        {
            track.Warning = string.IsNullOrEmpty(track.Warning)
                ? $"another track already owns the archive file {Path.GetFileName(targetPath)}; no archive copy was made"
                : $"{track.Warning}; another track already owns the archive file {Path.GetFileName(targetPath)}";
            _logger.LogInformation(
                "Archive copy for '{Title}' skipped: {Path} belongs to another track",
                track.Title, targetPath);
            return ArchiveOutcome.Failed;
        }

        var result = ArchiveCopier.Copy(
            track.CurrentPath!, target.Folder, target.Format,
            melodyId: track.MelodyId,
            title: track.Title, artist: track.Artist, album: track.Album,
            trackNumber: (uint?)(track.Position + 1),
            expectedDuration: track.DurationMs is > 0
                ? TimeSpan.FromMilliseconds(track.DurationMs.Value)
                : null);

        if (result.Ok)
        {
            track.ArchivePath = result.ArchivePath;
            // The app just wrote that file, so it knows its size: the
            // Library page does not have to stat every archive copy again.
            track.ArchiveSizeBytes = FileSizeOf(result.ArchivePath);
            return ArchiveOutcome.Written;
        }

        // Failure isolation: the primary download stands. The copier writes
        // through a temp file, so a failed rebuild leaves an earlier archive
        // (same target or the previous format's file) untouched. Keep the
        // row pointing at it when it is still on disk: the UI, the delete
        // impact and the delete-files action must still see that file.
        var keptPrevious = new[] { result.ArchivePath, track.ArchivePath }
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)
                && File.Exists(path)
                && !PlaylistRules.PathEquals(path, track.CurrentPath));
        track.ArchivePath = keptPrevious;
        var failure = keptPrevious is null
            ? $"archive copy failed: {result.Reason}"
            : $"archive copy failed: {result.Reason} (the previous copy was kept)";
        track.Warning = string.IsNullOrEmpty(track.Warning)
            ? failure
            : $"{track.Warning}; {failure}";
        return ArchiveOutcome.Failed;
    }

    /// <summary>
    /// The playlist's effective archive target: whether the archive is on,
    /// which folder (own override or the app-wide path plus a folder named
    /// after the playlist) and which format (own override or the app-wide
    /// default).
    /// </summary>
    private sealed record ArchiveTarget(bool Enabled, string? Folder, string? Format);

    private async Task<ArchiveTarget> ResolveArchiveTargetAsync(
        string? playlistId, MelodyBridgeDbContext db, CancellationToken ct)
    {
        PlaylistEntity? playlist = null;
        if (playlistId is { Length: > 0 })
        {
            playlist = await db.Playlists.AsNoTracking()
                .FirstOrDefaultAsync(p => p.Id == playlistId, ct);
        }
        if (playlist is { ArchiveEnabled: false })
            return new ArchiveTarget(false, null, null);

        var globalPath = await _settings.GetAsync("archive_path", "", ct);
        var playlistFolders = await _settings.GetBoolAsync("playlist_folders", true, ct);
        var folder = PlaylistRules.ResolveArchiveFolder(
            playlist?.ArchiveDirectory, globalPath, playlist?.Name, playlistFolders);
        var format = !string.IsNullOrWhiteSpace(playlist?.ArchiveFormat)
            ? playlist!.ArchiveFormat!
            : await _settings.GetAsync("archive_format", "auto", ct);
        return new ArchiveTarget(true, folder.Length == 0 ? null : folder, format);
    }

    /// <summary>
    /// True when no other row points at this path, so replacing the name
    /// cannot take another track's file away. The comparison follows the
    /// filesystem: where the volume folds case, two spellings of the same
    /// name are the same file, so a differently cased path counts as
    /// taken. A volume that keeps the spellings apart is asked, not
    /// assumed, Windows and macOS included.
    /// </summary>
    private static async Task<bool> PathIsFreeAsync(
        MelodyBridgeDbContext db, int trackId, string path, CancellationToken ct)
    {
        var wanted = path.Trim().TrimEnd('/', '\\');
        var folder = Path.GetDirectoryName(wanted) ?? wanted;
        var caseInsensitive = FileSystemCase.FoldsCase(folder);
        var owners = await db.Tracks.AsNoTracking()
            .Where(t => t.Id != trackId)
            .Where(t => caseInsensitive
                ? EF.Functions.Collate(t.CurrentPath, "NOCASE") == wanted
                  || EF.Functions.Collate(t.ArchivePath, "NOCASE") == wanted
                : t.CurrentPath == wanted || t.ArchivePath == wanted)
            .Select(t => t.Id)
            .ToListAsync(ct);
        return owners.Count == 0;
    }

    /// <summary>
    /// True when a copy or a link may take this name. The database says
    /// which rows claim the name. The file that is on the name right now
    /// says who owns the bytes: it carries the MELODY_ID of the song it
    /// belongs to, and a song that is still in the library keeps its
    /// file, even when the name is spelled with another case than the one
    /// we would write. A file of a song that was removed from the library
    /// is a leftover and does not block the copy.
    /// </summary>
    private async Task<bool> NameIsFreeAsync(
        MelodyBridgeDbContext db, TrackEntity track, string path, CancellationToken ct)
    {
        if (!await PathIsFreeAsync(db, track.Id, path, ct)) return false;
        if (!File.Exists(path)) return true;

        var existingId = ReadMelodyId(path);
        if (string.IsNullOrEmpty(existingId)) return true;
        if (string.Equals(existingId, track.MelodyId, StringComparison.Ordinal)) return true;

        // Another song's file. It counts while that song is in the
        // library; a leftover of a song that was removed does not.
        var ownerExists = await db.Tracks.AsNoTracking()
            .AnyAsync(t => t.Id != track.Id && t.MelodyId == existingId, ct);
        return !ownerExists;
    }

    /// <summary>Size of a file the linker just made or found, or null when it
    /// cannot be read. The archive row keeps it so the Library page does not
    /// stat every copy on every visit.</summary>
    private static long? FileSizeOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The MELODY_ID in a file, or null when it has none.</summary>
    private static string? ReadMelodyId(string path)
    {
        try
        {
            return TaglibHelper.ReadMelodyId(path);
        }
        catch
        {
            // A file that cannot be read carries no id we can judge by.
            return null;
        }
    }

    /// <summary>One hard link, best effort: false on any link failure.
    /// The failure family is wider than IOException: read-only targets and
    /// unsupported filesystems surface as UnauthorizedAccessException or
    /// NotSupportedException, and none of them may take the caller down.</summary>
    public bool TryHardLink(string linkPath, string targetPath)
        => TryHardLink(linkPath, targetPath, replaceExisting: false, out _);

    /// <summary>
    /// The same link attempt, with the OS failure spelled out. A pending
    /// row often finds its own file already in place, or an earlier pass
    /// already made this exact link; that is success, not EEXIST. The
    /// caller decides whether an existing name may be replaced, after
    /// checking that no other row still points at it.
    /// </summary>
    public bool TryHardLink(
        string linkPath, string targetPath, bool replaceExisting, out string? failure)
    {
        failure = null;
        try
        {
            if (File.Exists(linkPath))
            {
                if (_hardLinks.SameFile(linkPath, targetPath))
                    return true;
                if (!replaceExisting)
                    throw new IOException($"destination already exists: {linkPath}");
                File.Delete(linkPath);
            }
            var folder = Path.GetDirectoryName(linkPath);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            _hardLinks.Create(linkPath, targetPath);
            return true;
        }
        catch (Exception ex)
        {
            // A concurrent attempt may have made the link between the
            // check and Create; accept the desired end state.
            if (File.Exists(linkPath) && _hardLinks.SameFile(linkPath, targetPath))
                return true;
            failure = ex.Message;
            return false;
        }
    }

    /// <summary>The same attempt without the replacement decision.</summary>
    public bool TryHardLink(string linkPath, string targetPath, out string? failure)
        => TryHardLink(linkPath, targetPath, replaceExisting: false, out failure);
}

/// <summary>What a linking attempt did with the row.</summary>
public enum TrackLinkOutcome
{
    /// <summary>The row now points at its own file name in its folder.</summary>
    Linked,
    /// <summary>No copy elsewhere, or dedup is off, or no melody id.</summary>
    NoCandidate,
    /// <summary>A copy exists, but its quality does not satisfy the request.</summary>
    QualityMismatch,
    /// <summary>A copy exists, but its bytes could not be linked here.</summary>
    LinkFailed,
}
