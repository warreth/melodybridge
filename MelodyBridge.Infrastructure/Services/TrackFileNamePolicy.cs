using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Decides whether a track may use a file name another row already points
/// at. The same song may share one archive copy, and a hardlink twin is
/// the same bytes even when the source ids differ. A different song keeps
/// its file, and an archive copy never takes another track's primary.
/// </summary>
internal static class TrackFileNamePolicy
{
    public static async Task<bool> IsFreeAsync(
        MelodyBridgeDbContext db,
        TrackEntity track,
        string path,
        IHardLinkService hardLinks,
        bool allowPrimaryOwner,
        CancellationToken ct)
    {
        var wanted = path.Trim().TrimEnd('/', '\\');
        var folder = Path.GetDirectoryName(wanted) ?? wanted;
        var caseInsensitive = FileSystemCase.FoldsCase(folder);
        var trackQuery = db.Tracks.AsNoTracking()
            .Where(other => other.Id != track.Id);
        trackQuery = caseInsensitive
            ? trackQuery.Where(other =>
                EF.Functions.Collate(other.CurrentPath, "NOCASE") == wanted
                || EF.Functions.Collate(other.ArchivePath, "NOCASE") == wanted)
            : trackQuery.Where(other =>
                other.CurrentPath == wanted || other.ArchivePath == wanted);
        var owners = await trackQuery
            .Select(other => new { other.CurrentPath, other.ArchivePath, other.MelodyId })
            .ToListAsync(ct);

        var copyQuery = db.TrackArchiveCopies.AsNoTracking()
            .Where(copy => copy.TrackId != track.Id);
        copyQuery = caseInsensitive
            ? copyQuery.Where(copy => EF.Functions.Collate(copy.Path, "NOCASE") == wanted)
            : copyQuery.Where(copy => copy.Path == wanted);
        var copyOwnerIds = await copyQuery
            .Select(copy => copy.TrackId)
            .ToListAsync(ct);
        if (copyOwnerIds.Count > 0)
        {
            var copyOwners = await db.Tracks.AsNoTracking()
                .Where(other => copyOwnerIds.Contains(other.Id))
                .Select(other => new { other.CurrentPath, other.ArchivePath, other.MelodyId })
                .ToListAsync(ct);
            owners.AddRange(copyOwners);
        }

        // The file on disk is the strongest witness. The same MELODY_ID
        // means the same song, even when several rows own the name. A
        // different id belongs to another song: its row joins the owners,
        // so the hardlink check below can still recognize a twin.
        bool SamePath(string? first, string? second)
        {
            if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
                return false;
            return string.Equals(
                first.Trim().TrimEnd('/', '\\'),
                second.Trim().TrimEnd('/', '\\'),
                caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        // An archive copy must never overwrite another track's primary,
        // even when the song is the same: the archive format may differ.
        // This runs before the file-owner shortcut below, because a file
        // that carries this track's id can still be another row's primary.
        if (!allowPrimaryOwner
            && owners.Any(owner => SamePath(owner.CurrentPath, wanted)))
        {
            return false;
        }

        var fileOwnerId = File.Exists(path) ? TaglibHelper.ReadMelodyId(path) : null;
        if (!string.IsNullOrEmpty(fileOwnerId))
        {
            if (string.Equals(fileOwnerId, track.MelodyId, StringComparison.Ordinal))
                return true;

            var fileOwner = await db.Tracks.AsNoTracking()
                .Where(other => other.Id != track.Id && other.MelodyId == fileOwnerId)
                .Select(other => new { other.CurrentPath, other.ArchivePath, other.MelodyId })
                .FirstOrDefaultAsync(ct);
            if (fileOwner is null) return true; // a leftover of a removed song
            owners.Add(fileOwner);
        }

        if (owners.Count == 0) return true;

        return owners.All(owner =>
            (!string.IsNullOrEmpty(track.MelodyId)
                && string.Equals(owner.MelodyId, track.MelodyId, StringComparison.Ordinal))
            || (owner.CurrentPath is { Length: > 0 } other
                && track.CurrentPath is { Length: > 0 } primary
                && hardLinks.SameFile(primary, other)));
    }

    /// <summary>
    /// True when the MELODY_ID on an existing archive file belongs to a
    /// track whose primary is the same bytes as this track's primary: a
    /// hardlink twin. The first twin's copy is already the right audio,
    /// so the second adopts it instead of rewriting the tags.
    /// </summary>
    public static async Task<bool> IsTwinCopyAsync(
        MelodyBridgeDbContext db,
        TrackEntity track,
        string fileMelodyId,
        IHardLinkService hardLinks,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(fileMelodyId)
            || string.IsNullOrEmpty(track.MelodyId)
            || string.Equals(fileMelodyId, track.MelodyId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(track.CurrentPath))
            return false;

        var ownerPrimaries = await db.Tracks.AsNoTracking()
            .Where(other => other.Id != track.Id && other.MelodyId == fileMelodyId)
            .Select(other => other.CurrentPath)
            .ToListAsync(ct);
        return ownerPrimaries.Any(primary => primary is { Length: > 0 }
            && hardLinks.SameFile(track.CurrentPath, primary));
    }
}
