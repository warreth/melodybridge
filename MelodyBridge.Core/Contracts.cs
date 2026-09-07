namespace MelodyBridge.Core;

public interface ISourceProvider
{
    string Name { get; }
    Platform Platform { get; }
    /// <summary>True when this provider can parse and fetch the given identifier/URL.</summary>
    bool CanHandle(string sourceIdentifier);
    Task<Playlist> GetPlaylistAsync(string sourceIdentifier);
    Task<string?> ResolveTrackUrlAsync(string query);
}

public interface ILibraryScanner
{
    /// <summary>Scans the paths and reports what it actually saw,
    /// including paths that do not exist from the app's point of view.</summary>
    Task<ScanReport> ScanAsync(IEnumerable<ScanLocation> paths, CancellationToken ct = default);
}

/// <summary>Honest numbers from one scan run, shown on the Library page.</summary>
public record ScanReport(
    int Locations,
    int TaggedFiles,
    int UntaggedFiles,
    IReadOnlyList<string> MissingPaths)
{
    public static readonly ScanReport Empty = new(0, 0, 0, Array.Empty<string>());
}

public interface IPlaylistComposer
{
    Task ComposeAsync(Playlist playlist, IEnumerable<ScanLocation> searchLocations, PlaylistOutputOptions options, CancellationToken ct = default);
}

public interface IMediaServerSync
{
    string Name { get; }
    Task SyncPlaylistAsync(Playlist playlist, PlaylistOutputOptions options, CancellationToken ct = default);
    /// <summary>Report of the last SyncPlaylistAsync call; null before the first one.</summary>
    MediaServerSyncReport? LastReport { get; }
}

public interface ISyncJobRunner
{
    Task<SyncJobRunLog> RunJobAsync(SyncJob job, CancellationToken ct = default);
}

/// <summary>
/// Reads users and reachability of an arbitrary media server (Jellyfin,
/// Plex, Navidrome). The connection values travel per call, so nothing
/// mutable is shared with the sync clients.
/// </summary>
public interface IMediaServerDirectory
{
    /// <summary>Server kind this directory speaks ("Jellyfin", "Plex", "Navidrome").</summary>
    string Kind { get; }
    /// <summary>All users the given server reports; empty when the server has no user list.</summary>
    Task<List<MediaServerUserOption>> GetUsersAsync(MediaServerConnection connection, CancellationToken ct = default);
    /// <summary>True when the server answers an authenticated lightweight request.</summary>
    Task<bool> TestConnectionAsync(MediaServerConnection connection, CancellationToken ct = default);
}

/// <summary>One user row of a media server, as the picker shows it.</summary>
public record MediaServerUserOption(string Id, string? Name);

public interface IDownloadManager
{
    Task<string?> DownloadAsync(string sourceUrl, string outputDirectory, string melodyId, CancellationToken ct = default);
    /// <summary>
    /// Search for the track by metadata through the plugin waterfall,
    /// download it into outputDirectory, tag the MELODY_ID, return the path.
    /// </summary>
    Task<string?> DownloadTrackAsync(string artist, string title, string outputDirectory, string melodyId, DownloadQuality? quality = null, CancellationToken ct = default);
    /// <summary>Current in-flight download states (for UI polling).</summary>
    IReadOnlyList<DownloadProgress> SnapshotProgress();
    /// <summary>
    /// Why the last DownloadTrackAsync for this melodyId produced nothing:
    /// files outside the quality filters, or no hit at all. Null when the
    /// last call succeeded or never ran.
    /// </summary>
    string? LastFailure(string melodyId);
}

/// <summary>
/// Per-call media-server connection override (Jellyfin, Plex, Navidrome).
/// When set, these values win over the global settings (the sync-job
/// wizard stores them per job).
/// </summary>
/// <summary>UserId carries the Jellyfin user id or the Navidrome username;
/// null for servers that need none (Plex) or the server default.</summary>
public record MediaServerConnection(string BaseUrl, string ApiKey, string? UserId = null);

public record PlaylistOutputOptions(
    string OutputPath,
    bool UseRelativePaths,
    Dictionary<string, string>? PathRemap,
    MediaServerConnection? MediaServerConnection = null
);

/// <summary>
/// Creates native OS hard links between two paths. Implemented once in
/// Infrastructure with the platform interop; the interface lives here so
/// the store and tests can swap a fake without referencing the platform
/// layer.
/// </summary>
public interface IHardLinkService
{
    /// <summary>True when the filesystem supports hard links at all.</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Creates a hard link at <paramref name="linkPath"/> pointing at the
    /// same inode as <paramref name="targetPath"/>. Throws IOException
    /// (cross-device, permissions, unsupported filesystem) on failure;
    /// the caller decides the fallback.
    /// </summary>
    void Create(string linkPath, string targetPath);

    /// <summary>
    /// True when the two paths sit on the same filesystem/partition: the
    /// only case where a hard link between them can succeed. A cheap
    /// pre-check so callers can skip straight to the fallback.
    /// </summary>
    bool SameFileSystem(string pathA, string pathB);

    /// <summary>
    /// Number of hard links to the file (1 = no other names). Unix reports
    /// the count from stat; Windows needs GetFileInformationByHandle.
    /// Null when the count cannot be read: the caller must then assume
    /// other links may exist.
    /// </summary>
    int? LinkCount(string path);
}

/// <summary>
/// Verdict of comparing an existing local file against the quality a
/// playlist asks for, and the file that was compared.
/// </summary>
public record DedupMatch(
    /// <summary>Path of the existing downloaded file.</summary>
    string ExistingPath,
    /// <summary>Container of the existing file, lower-cased extension.</summary>
    string? ExistingFormat,
    /// <summary>Measured bitrate of the existing file in kbps, null when unknown.</summary>
    int? ExistingBitrateKbps,
    /// <summary>Playlist row id the existing file belongs to (any one of them).</summary>
    string? SourcePlaylistId,
    /// <summary>True when container and bitrate satisfy the requested profile exactly.</summary>
    bool QualityMatches)
{
    /// <summary>Human summary of the mismatch, for the UI warning.</summary>
    public string Describe()
    {
        var what = ExistingBitrateKbps is { } kbps ? $"{ExistingFormat} {kbps} kbps" : ExistingFormat ?? "unknown format";
        return QualityMatches ? what : $"{what}, different from this playlist's target";
    }
}

/// <summary>
/// Compares an existing file's container and measured bitrate against a
/// playlist's requested download quality. Exact container match plus a
/// bitrate inside the requested band counts as a match; anything else is
/// a mismatch the user must resolve. Lossless targets only accept the
/// lossless container; lossy targets accept their own container.
/// </summary>
public static class DedupQuality
{
    /// <summary>
    /// Container name for a quality request: the requested AudioFormat, or
    /// null for Auto (any container is acceptable).
    /// </summary>
    public static string? RequestedContainer(DownloadQuality quality)
        => quality.Format switch
        {
            AudioFormat.Mp3 => "mp3",
            AudioFormat.Flac => "flac",
            AudioFormat.Opus => "opus",
            AudioFormat.Aac => "m4a",
            _ => null,
        };

    /// <summary>True when the existing file satisfies the quality request.</summary>
    public static bool Matches(string? existingFormat, int? existingBitrateKbps, DownloadQuality quality)
    {
        // Unknown facts cannot be verified: treat them as a mismatch so a
        // human decides, never a silent hardlink of a dubious file.
        if (string.IsNullOrWhiteSpace(existingFormat) || existingBitrateKbps is not > 0)
            return false;

        var requested = RequestedContainer(quality);
        if (requested is not null && !FormatEquals(existingFormat, requested))
            return false;

        return quality.IsWithinBand(existingBitrateKbps);
    }

    /// <summary>
    /// Container comparison forgiving about codec/container spelling:
    /// m4a vs aac, ogg vs opus, any casing.
    /// </summary>
    public static bool FormatEquals(string existing, string requested)
    {
        static string Canonical(string f) => f.Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "m4a" or "aac" or "mp4a" => "aac",
            "ogg" or "opus" => "opus",
            "mpeg3" or "mpeg" => "mp3",
            var other => other,
        };
        return Canonical(existing) == Canonical(requested);
    }
}
