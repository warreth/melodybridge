using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
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
    private readonly DataChangeTracker? _changes;

    public TrackLinker(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        SettingsStore settings,
        HardLinkService hardLinks,
        ILogger<TrackLinker> logger,
        DataChangeTracker? changes = null)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _hardLinks = hardLinks;
        _logger = logger;
        _changes = changes;
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
    public async Task<TrackLinkOutcome> TryLinkAsync(
        TrackEntity track, MelodyBridgeDbContext db, DownloadQuality requested,
        bool pauseOnQualityMismatch, CancellationToken ct,
        DedupMatch? candidate = null, bool acceptQualityDifference = false,
        string? warning = null)
    {
        if (track.MelodyId is null) return TrackLinkOutcome.NoCandidate;
        if (!await _settings.GetBoolAsync(PlaylistStore.DedupSettingKey, true, ct))
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

        var folder = await ResolvePlaylistFolderAsync(track.PlaylistEntityId, db, ct);
        if (string.IsNullOrWhiteSpace(folder)) return TrackLinkOutcome.LinkFailed;

        // Link the existing file under its own name into this playlist's
        // folder. A link failure (cross-device EXDEV, read-only target,
        // ...) leaves the row for the caller: a real download or a user
        // decision, never a silent copy.
        var linkPath = Path.Combine(folder, Path.GetFileName(candidate.ExistingPath));
        var alreadyOwnName = PlaylistRules.PathEquals(linkPath, candidate.ExistingPath);
        if (!alreadyOwnName && !TryHardLink(linkPath, candidate.ExistingPath))
        {
            _logger.LogInformation(
                "Hard link for '{Title}' failed ({Source} -> {Target}); leaving the track for a real download",
                track.Title, candidate.ExistingPath, linkPath);
            return TrackLinkOutcome.LinkFailed;
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
            // the next restart.
            var previousStatus = track.DownloadStatus;
            var claimed = await db.Tracks
                .Where(t => t.Id == track.Id
                    && (t.DownloadStatus == null || t.DownloadStatus == "pending" || t.DownloadStatus == "failed"))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.DownloadStatus, "in_progress"), ct);
            if (claimed != 1) continue;

            var linkedThisTrack = false;
            try
            {
                var outcome = await TryLinkAsync(track, db, requested,
                    pauseOnQualityMismatch: false, ct, candidate: candidate.Match);
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
            if (PlaylistRules.PathEquals(archiveLink, existingArchive)
                || TryHardLink(archiveLink, existingArchive))
            {
                track.ArchivePath = archiveLink;
                return;
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

    /// <summary>One hard link, best effort: false on any link failure.
    /// The failure family is wider than IOException: read-only targets and
    /// unsupported filesystems surface as UnauthorizedAccessException or
    /// NotSupportedException, and none of them may take the caller down.</summary>
    public bool TryHardLink(string linkPath, string targetPath)
    {
        try
        {
            if (File.Exists(linkPath) && _hardLinks.LinkCount(linkPath) is not > 1)
                File.Delete(linkPath); // stale unlinked file from an earlier run
            var folder = Path.GetDirectoryName(linkPath);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            _hardLinks.Create(linkPath, targetPath);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
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
