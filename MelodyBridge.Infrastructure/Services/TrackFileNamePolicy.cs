using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>What a path means for one track's archive copy.</summary>
internal enum PathVerdict
{
    /// <summary>Nothing claims the name: the copy may take it.</summary>
    Free,
    /// <summary>The name already holds this song, from another row or an
    /// earlier run: share it instead of writing a second copy.</summary>
    Shared,
    /// <summary>Another song, or another track's primary, owns the name.</summary>
    Blocked,
}

/// <summary>
/// Decides whether a track may use a file name another row already points
/// at. The same song may share one archive copy, and a hardlink twin is
/// the same bytes even when the source ids differ. A different song keeps
/// its file, and an archive copy never takes another track's primary.
/// <para>
/// "The same song" is wider than one MELODY_ID: the same track fetched
/// from Spotify, Apple Music and Tidal has a different id on every
/// platform. ISRC and, when that is missing, a high-confidence metadata
/// match with a plausible length decide. Two genuinely different songs
/// that sanitize to one name get a numbered variant instead.
/// </para>
/// </summary>
internal static class TrackFileNamePolicy
{
    /// <summary>One path an archive copy can land on.</summary>
    public sealed record Target(string Path, PathVerdict Verdict);

    public static async Task<bool> IsFreeAsync(
        MelodyBridgeDbContext db,
        TrackEntity track,
        string path,
        IHardLinkService hardLinks,
        bool allowPrimaryOwner,
        CancellationToken ct)
    {
        var verdict = await VerdictAsync(db, track, path, hardLinks, allowPrimaryOwner, ct);
        return verdict is not PathVerdict.Blocked;
    }

    /// <summary>
    /// The first name this archive copy may use: the preferred name when
    /// it is free or already holds this song, otherwise the next numbered
    /// variant. A flat archive folder gives two different songs the same
    /// name; the second one takes "Song (2).opus" instead of being refused
    /// on every pass. Null only when every variant up to the limit is
    /// taken, which a real library does not reach.
    /// </summary>
    public static async Task<Target?> ResolveArchiveAsync(
        MelodyBridgeDbContext db,
        TrackEntity track,
        string preferredPath,
        IHardLinkService hardLinks,
        CancellationToken ct)
    {
        // A copy this row already has in the same folder stays its copy.
        // Without this, a row that once landed on "Song (2).opus" would
        // pick a new variant on every pass and fill the folder.
        if (track.ArchivePath is { Length: > 0 } own
            && File.Exists(own)
            && !PlaylistRules.PathEquals(own, preferredPath)
            && PlaylistRules.PathEquals(Path.GetDirectoryName(own) ?? "",
                Path.GetDirectoryName(preferredPath) ?? ""))
        {
            var verdict = await VerdictAsync(db, track, own, hardLinks, false, ct);
            if (verdict is not PathVerdict.Blocked) return new Target(own, verdict);
        }

        foreach (var candidate in ArchiveCopier.TargetVariants(preferredPath))
        {
            var verdict = await VerdictAsync(
                db, track, candidate, hardLinks, allowPrimaryOwner: false, ct);
            if (verdict is not PathVerdict.Blocked)
                return new Target(candidate, verdict);
        }
        return null;
    }

    /// <summary>What this path means for this track.</summary>
    public static async Task<PathVerdict> VerdictAsync(
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
            .Select(other => new OwnerRow(
                other.CurrentPath, other.ArchivePath, other.MelodyId, other.Isrc,
                other.Artist, other.Title, other.DurationMs))
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
                .Select(other => new OwnerRow(
                    other.CurrentPath, other.ArchivePath, other.MelodyId, other.Isrc,
                    other.Artist, other.Title, other.DurationMs))
                .ToListAsync(ct);
            owners.AddRange(copyOwners);
        }

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
            return PathVerdict.Blocked;
        }

        // The file on disk is the strongest witness. The same MELODY_ID
        // means the same song, even when several rows own the name. A
        // different id may still be another platform's copy of this song:
        // its row joins the owners, so ISRC, the hardlink check and the
        // metadata match can recognize it.
        var exists = File.Exists(path);
        var fileOwnerId = exists ? TaglibHelper.ReadMelodyId(path) : null;
        if (!string.IsNullOrEmpty(fileOwnerId))
        {
            if (string.Equals(fileOwnerId, track.MelodyId, StringComparison.Ordinal))
                return PathVerdict.Shared;

            var fileOwner = await db.Tracks.AsNoTracking()
                .Where(other => other.Id != track.Id && other.MelodyId == fileOwnerId)
                .Select(other => new OwnerRow(
                    other.CurrentPath, other.ArchivePath, other.MelodyId, other.Isrc,
                    other.Artist, other.Title, other.DurationMs))
                .FirstOrDefaultAsync(ct);
            if (fileOwner is null) return PathVerdict.Free; // a leftover of a removed song
            if (!owners.Any(owner => SameOwner(owner, fileOwner))) owners.Add(fileOwner);
        }

        if (owners.Count == 0) return PathVerdict.Free;

        foreach (var owner in owners)
        {
            if (!SameSong(track, owner, hardLinks))
                return PathVerdict.Blocked;
        }
        return exists || owners.Any(owner => SamePath(owner.ArchivePath, wanted)
                || SamePath(owner.CurrentPath, wanted))
            ? PathVerdict.Shared
            : PathVerdict.Free;
    }

    /// <summary>
    /// True when the MELODY_ID on an existing archive file belongs to this
    /// song on another platform or row: the first copy is the right audio,
    /// so the second row adopts it instead of rewriting the tags.
    /// </summary>
    public static async Task<bool> IsSameSongCopyAsync(
        MelodyBridgeDbContext db,
        TrackEntity track,
        string fileMelodyId,
        IHardLinkService hardLinks,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(fileMelodyId)
            || string.Equals(fileMelodyId, track.MelodyId, StringComparison.Ordinal))
            return false;

        var owners = await db.Tracks.AsNoTracking()
            .Where(other => other.Id != track.Id && other.MelodyId == fileMelodyId)
            .Select(other => new OwnerRow(
                other.CurrentPath, other.ArchivePath, other.MelodyId, other.Isrc,
                other.Artist, other.Title, other.DurationMs))
            .ToListAsync(ct);
        foreach (var owner in owners)
        {
            if (SameSong(track, owner, hardLinks)) return true;
        }
        return false;
    }

    /// <summary>
    /// One shared definition of "the same song": the same source id, the
    /// same ISRC, the same inode (a hardlink twin), or a high-confidence
    /// artist and title match with a plausible length and no recording
    /// marker that the other title lacks.
    /// </summary>
    private static bool SameSong(
        TrackEntity track, OwnerRow owner, IHardLinkService hardLinks)
    {
        if (!string.IsNullOrEmpty(track.MelodyId)
            && string.Equals(owner.MelodyId, track.MelodyId, StringComparison.Ordinal))
            return true;

        if (SameIsrc(track.Isrc, owner.Isrc)) return true;

        if (owner.CurrentPath is { Length: > 0 } other
            && track.CurrentPath is { Length: > 0 } primary
            && hardLinks.SameFile(primary, other))
            return true;

        return MetadataMatches(track, owner);
    }

    /// <summary>
    /// Two spellings of one recording. Kept deliberately strict: a clear
    /// artist and title match, a plausible length, and no recording marker
    /// (live, remix, karaoke, ...) that only one of the two titles names.
    /// </summary>
    private static bool MetadataMatches(TrackEntity track, OwnerRow owner)
    {
        if (string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title)
            || string.IsNullOrWhiteSpace(owner.Artist) || string.IsNullOrWhiteSpace(owner.Title))
            return false;

        if (FuzzyMatcher.Confidence(track.Artist, track.Title, owner.Artist, owner.Title)
            != MatchConfidence.High)
            return false;

        var expected = track.DurationMs is > 0
            ? TimeSpan.FromMilliseconds(track.DurationMs.Value)
            : (TimeSpan?)null;
        var hit = owner.DurationMs is > 0
            ? TimeSpan.FromMilliseconds(owner.DurationMs.Value)
            : (TimeSpan?)null;
        if (!FuzzyMatcher.DurationPlausible(expected, hit)) return false;

        // A marker one title names and the other drops is a different
        // recording, not another platform's spelling.
        if (FuzzyMatcher.DropsRequestedMarker(track.Title, owner.Title)
            || FuzzyMatcher.DropsRequestedMarker(owner.Title, track.Title)
            || FuzzyMatcher.AddsUnrequestedModifier(track.Title, owner.Title)
            || FuzzyMatcher.AddsUnrequestedModifier(owner.Title, track.Title))
            return false;

        return true;
    }

    /// <summary>ISRCs without their dashes, uppercase; both must be real.</summary>
    private static bool SameIsrc(string? first, string? second)
    {
        var a = NormalizeIsrc(first);
        var b = NormalizeIsrc(second);
        return a is { Length: 12 } && b is { Length: 12 }
            && string.Equals(a, b, StringComparison.Ordinal);
    }

    private static string? NormalizeIsrc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var builder = new System.Text.StringBuilder(12);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(char.ToUpperInvariant(ch));
        }
        return builder.ToString();
    }

    private static bool SameOwner(OwnerRow first, OwnerRow second)
        => string.Equals(first.CurrentPath, second.CurrentPath, StringComparison.Ordinal)
            && string.Equals(first.ArchivePath, second.ArchivePath, StringComparison.Ordinal);

    /// <summary>The ownership facts of one row, without loading the row.</summary>
    private sealed record OwnerRow(
        string? CurrentPath, string? ArchivePath, string? MelodyId, string? Isrc,
        string? Artist, string? Title, long? DurationMs);
}
