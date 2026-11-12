using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// One track that needs a decision, with the playlist it belongs to and a
/// stable kind the page can group by. The raw error stays in Detail so the
/// user never loses the plugin's own wording.
/// </summary>
public sealed record AttentionTrack(
    int TrackId,
    string PlaylistId,
    string PlaylistName,
    string Title,
    string? Artist,
    string? Album,
    string Status,
    string Kind,
    string Detail,
    string? Warning,
    string? CurrentPath,
    string? ArchivePath,
    string? SourcePluginId);

/// <summary>A playlist whose last refresh was incomplete or failed.</summary>
public sealed record AttentionPlaylist(
    string PlaylistId,
    string Name,
    SyncStatus Status,
    string? Warning,
    DateTime? LastSyncAt,
    string? SourceUrl);

public sealed record AttentionGroup(
    string Kind,
    string Label,
    string Description,
    IReadOnlyList<AttentionTrack> Tracks);

public sealed record AttentionSnapshot(
    IReadOnlyList<AttentionGroup> Groups,
    IReadOnlyList<AttentionPlaylist> Playlists)
{
    public int TrackCount => Groups.Sum(group => group.Tracks.Count);
    public int Total => TrackCount + Playlists.Count;
    public int FailedCount => Groups
        .Where(group => group.Kind.StartsWith("failed-", StringComparison.Ordinal))
        .Sum(group => group.Tracks.Count);
    public int ReviewCount => Groups
        .Where(group => group.Kind.StartsWith("review-", StringComparison.Ordinal))
        .Sum(group => group.Tracks.Count);
    public int DedupCount => Groups
        .Where(group => group.Kind == AttentionKinds.Dedup)
        .Sum(group => group.Tracks.Count);
    public int ArchiveCount => Groups
        .Where(group => group.Kind is AttentionKinds.Archive
            or AttentionKinds.FileMissing or AttentionKinds.Unverified)
        .Sum(group => group.Tracks.Count);
}

public static class AttentionKinds
{
    public const string FailedNoSource = "failed-no-source";
    public const string FailedQualityFilter = "failed-quality-filter";
    public const string FailedFormat = "failed-format";
    public const string FailedDuration = "failed-duration";
    public const string FailedInvalidAudio = "failed-invalid-audio";
    public const string FailedCorrupt = "failed-corrupt";
    public const string FailedAuth = "failed-auth";
    public const string FailedTimeout = "failed-timeout";
    public const string FailedCancelled = "failed-cancelled";
    public const string FailedMetadata = "failed-metadata";
    public const string FailedOther = "failed-other";
    public const string Dedup = "dedup";
    public const string ReviewLength = "review-length";
    public const string ReviewSpectrum = "review-spectrum";
    public const string ReviewBoth = "review-both";
    public const string Archive = "archive";
    public const string FileMissing = "file-missing";
    public const string Unverified = "unverified";

    public static readonly string[] Order =
    [
        FailedNoSource, FailedQualityFilter, FailedFormat, FailedDuration,
        FailedInvalidAudio, FailedCorrupt, FailedAuth, FailedTimeout,
        FailedMetadata, FailedCancelled, FailedOther, Dedup,
        ReviewLength, ReviewSpectrum, ReviewBoth, Archive, FileMissing, Unverified,
    ];

    public static string Label(string kind) => kind switch
    {
        FailedNoSource => "No source found",
        FailedQualityFilter => "Quality filters",
        FailedFormat => "Wrong format",
        FailedDuration => "Wrong length",
        FailedInvalidAudio => "Not audio",
        FailedCorrupt => "Corrupt files",
        FailedAuth => "Plugin login",
        FailedTimeout => "Timeouts",
        FailedCancelled => "Cancelled",
        FailedMetadata => "Missing metadata",
        FailedOther => "Other download errors",
        Dedup => "Hardlink decisions",
        ReviewLength => "Length reviews",
        ReviewSpectrum => "Quality reviews",
        ReviewBoth => "Length and quality reviews",
        Archive => "Archive copies",
        FileMissing => "Missing files",
        Unverified => "Unverified files",
        _ => "Other",
    };

    public static string Description(string kind) => kind switch
    {
        FailedNoSource =>
            "No enabled source had a matching track. Add another source, or retry after a provider recovers.",
        FailedQualityFilter =>
            "The requested quality excludes every source that had the track. Loosen the playlist quality or pick another format.",
        FailedFormat =>
            "Sources had the track, but not in the container this playlist asks for.",
        FailedDuration =>
            "Every candidate had a different length than the playlist expects.",
        FailedInvalidAudio =>
            "A source answered with bytes that are not a playable audio file.",
        FailedCorrupt =>
            "The downloaded file was unreadable and was removed.",
        FailedAuth =>
            "A stored plugin login stopped working. Fix the plugin first, then retry.",
        FailedTimeout =>
            "A source or the whole track ran past its time budget.",
        FailedCancelled =>
            "The run was cancelled before this track finished.",
        FailedMetadata =>
            "The track has no usable artist or title, so it could not be searched.",
        FailedOther =>
            "The source returned an error that does not fit another group.",
        Dedup =>
            "The same recording already exists at another quality. Hardlink it or download a fresh copy.",
        ReviewLength =>
            "The file is playable, but its length differs from the playlist's expected track.",
        ReviewSpectrum =>
            "The file claims a higher quality than its spectrum supports.",
        ReviewBoth =>
            "The file failed both the length and the spectrum check.",
        Archive =>
            "An archive copy could not be written or another track owns the target name.",
        FileMissing =>
            "The database still points at a file that is no longer on disk.",
        Unverified =>
            "The audio check could not vouch for this file. Recheck it, or download it again from the playlist.",
        _ => "This track needs a decision.",
    };

    public static bool IsFailure(string kind)
        => kind.StartsWith("failed-", StringComparison.Ordinal);
}

/// <summary>
/// Turns stored status, error and warning strings into one stable kind.
/// The classifier is pure and tested, so wording changes in a plugin do
/// not silently move a whole group into "other".
/// </summary>
public static class AttentionClassifier
{
    public static string? Classify(
        string? status, string? error, string? reviewKind, string? warning)
    {
        if (status == "needs-review") return AttentionKinds.Dedup;

        if (status == "failed")
            return ClassifyFailure(error);

        if (status != "downloaded") return null;

        if (!string.IsNullOrWhiteSpace(reviewKind))
        {
            var length = reviewKind.Contains("length", StringComparison.OrdinalIgnoreCase);
            var spectrum = reviewKind.Contains("spectrum", StringComparison.OrdinalIgnoreCase);
            return (length, spectrum) switch
            {
                (true, true) => AttentionKinds.ReviewBoth,
                (true, false) => AttentionKinds.ReviewLength,
                (false, true) => AttentionKinds.ReviewSpectrum,
                _ => null,
            };
        }

        var text = warning ?? "";
        if (text.Contains("archive copy failed", StringComparison.OrdinalIgnoreCase)
            || text.Contains("archive copy unverified", StringComparison.OrdinalIgnoreCase)
            || text.Contains("already owns the archive file", StringComparison.OrdinalIgnoreCase))
            return AttentionKinds.Archive;
        if (text.Contains("file missing on disk", StringComparison.OrdinalIgnoreCase)
            || text.Contains("the file lived in the archive folder", StringComparison.OrdinalIgnoreCase))
            return AttentionKinds.FileMissing;
        if (text.Contains("could not verify the download", StringComparison.OrdinalIgnoreCase))
            return AttentionKinds.Unverified;
        return null;
    }

    public static string ClassifyFailure(string? error)
    {
        var text = error ?? "";
        if (text.Length == 0) return AttentionKinds.FailedOther;

        if (Contains(text, "no source had", "no enabled plugin", "no plugin could download"))
            return AttentionKinds.FailedNoSource;
        if (Contains(text, "quality filters", "outside your quality filters"))
            return AttentionKinds.FailedQualityFilter;
        if (Contains(text, "another format", "requested container", "container change"))
            return AttentionKinds.FailedFormat;
        if (Contains(text, "wrong length", "duration mismatch"))
            return AttentionKinds.FailedDuration;
        if (Contains(text, "not audio", "not a playable audio"))
            return AttentionKinds.FailedInvalidAudio;
        if (Contains(text, "corrupt"))
            return AttentionKinds.FailedCorrupt;
        if (Contains(text, "token", "login", "signed in", "arl", "plan restric",
                "account blocked", "subscription", "unauthorized", "authentication"))
            return AttentionKinds.FailedAuth;
        if (Contains(text, "timed out", "timeout", "took longer", "budget"))
            return AttentionKinds.FailedTimeout;
        if (Contains(text, "cancel"))
            return AttentionKinds.FailedCancelled;
        if (Contains(text, "metadata", "missing artist", "missing title"))
            return AttentionKinds.FailedMetadata;
        return AttentionKinds.FailedOther;
    }

    private static bool Contains(string text, params string[] needles)
        => needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reads every track and playlist that still needs a decision.</summary>
public sealed class AttentionService
{
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;

    public AttentionService(IDbContextFactory<MelodyBridgeDbContext> dbFactory)
        => _dbFactory = dbFactory;

    public async Task<AttentionSnapshot> GetAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Tracks.AsNoTracking()
            .Where(track => track.PlaylistEntityId != null
                && (track.DownloadStatus == "failed"
                    || track.DownloadStatus == "needs-review"
                    || (track.DownloadStatus == "downloaded"
                        && (track.ReviewKind != null
                            || (track.Warning != null
                                && (track.Warning.Contains("archive copy failed")
                                    || track.Warning.Contains("archive copy unverified")
                                    || track.Warning.Contains("already owns the archive file")
                                    || track.Warning.Contains("file missing on disk")
                                    || track.Warning.Contains("the file lived in the archive folder")
                                    || track.Warning.Contains("could not verify the download")))))))
            .Join(db.Playlists.AsNoTracking(),
                track => track.PlaylistEntityId,
                playlist => playlist.Id,
                (track, playlist) => new
                {
                    track.Id,
                    track.Title,
                    track.Artist,
                    track.Album,
                    track.DownloadStatus,
                    track.DownloadError,
                    track.ReviewKind,
                    track.Warning,
                    track.CurrentPath,
                    track.ArchivePath,
                    track.SourcePluginId,
                    PlaylistId = playlist.Id,
                    PlaylistName = playlist.Name,
                })
            .ToListAsync(ct);

        var tracks = new List<AttentionTrack>();
        foreach (var row in rows)
        {
            var kind = AttentionClassifier.Classify(
                row.DownloadStatus, row.DownloadError, row.ReviewKind, row.Warning);
            if (kind is null) continue;

            var detail = kind switch
            {
                AttentionKinds.Dedup => row.Warning ?? "The existing copy has another quality.",
                var k when k.StartsWith("review-", StringComparison.Ordinal) =>
                    row.ReviewKind is { Length: > 0 } review
                        ? $"Review: {review}" + (row.Warning is { Length: > 0 } ? $" · {row.Warning}" : "")
                        : "The quality check wants a decision.",
                AttentionKinds.Archive or AttentionKinds.FileMissing or AttentionKinds.Unverified =>
                    row.Warning ?? "The archive or file state needs a repair.",
                _ => row.DownloadError ?? "The download failed.",
            };

            tracks.Add(new AttentionTrack(
                row.Id,
                row.PlaylistId,
                row.PlaylistName,
                row.Title,
                row.Artist,
                row.Album,
                row.DownloadStatus ?? "",
                kind,
                detail,
                row.Warning,
                row.CurrentPath,
                row.ArchivePath,
                row.SourcePluginId));
        }

        var byKind = tracks
            .GroupBy(track => track.Kind, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var groups = AttentionKinds.Order
            .Where(kind => byKind.ContainsKey(kind))
            .Select(kind => new AttentionGroup(
                kind,
                AttentionKinds.Label(kind),
                AttentionKinds.Description(kind),
                byKind[kind]))
            .ToList();

        var playlists = await db.Playlists.AsNoTracking()
            .Where(playlist => playlist.LastSyncStatus == SyncStatus.Failed
                || (playlist.SyncWarning != null && playlist.SyncWarning != ""))
            .OrderByDescending(playlist => playlist.LastSyncAt ?? DateTime.MinValue)
            .Select(playlist => new AttentionPlaylist(
                playlist.Id,
                playlist.Name,
                playlist.LastSyncStatus,
                playlist.SyncWarning,
                playlist.LastSyncAt,
                playlist.SourceUrl))
            .ToListAsync(ct);

        return new AttentionSnapshot(groups, playlists);
    }
}
