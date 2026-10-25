namespace MelodyBridge.Core;

public interface ISourceProvider
{
    string Name { get; }
    Platform Platform { get; }
    /// <summary>True when this provider can parse and fetch the given identifier/URL.</summary>
    bool CanHandle(string sourceIdentifier);
    Task<Playlist> GetPlaylistAsync(string sourceIdentifier);
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

    /// <summary>
    /// The server and account behind a working connection, when the server
    /// can name them. Null when it cannot; this is best effort on top of
    /// <see cref="TestConnectionAsync"/>, never a second gate.
    /// </summary>
    Task<MediaServerIdentity?> IdentifyAsync(
        MediaServerConnection connection, CancellationToken ct = default)
        => Task.FromResult<MediaServerIdentity?>(null);
}

/// <summary>One user row of a media server, as the picker shows it.</summary>
public record MediaServerUserOption(string Id, string? Name);

/// <summary>What a media server calls itself and the account behind a connection.</summary>
public record MediaServerIdentity(string? ServerName, string? AccountName);

/// <summary>Honest state of one configured media server connection.</summary>
public enum MediaServerConnectionState
{
    /// <summary>The probe has not answered yet: nothing is claimed.</summary>
    Checking,
    /// <summary>No URL or no credentials: there is nothing to connect to yet.</summary>
    NotConfigured,
    /// <summary>Values are stored but the server did not answer or rejected the login.</summary>
    Unreachable,
    /// <summary>The stored credentials worked.</summary>
    Connected,
}

/// <summary>
/// One media server as the dashboard should show it: the profile name, the
/// state after a real probe, and who answered. Never inferred from a URL
/// alone, because config fallbacks carry localhost defaults.
/// </summary>
public record MediaServerConnectionStatus(
    string ProfileId,
    string Name,
    string Kind,
    string BaseUrl,
    MediaServerConnectionState State,
    string? AccountName = null,
    string? ServerName = null,
    string? Reason = null);

/// <summary>
/// Probes every stored media server and reports which ones actually answer.
/// Results are cached briefly so a dashboard refresh is cheap.
/// </summary>
public interface IMediaServerStatusProbe
{
    /// <summary>Raised when a new verdict lands, so a page that showed the
    /// checking state can redraw itself.</summary>
    event Action? Changed;

    /// <summary>
    /// Probes the servers and waits for the answer. Use this when the
    /// answer is the point of the call.
    /// </summary>
    Task<IReadOnlyList<MediaServerConnectionStatus>> SnapshotAsync(
        bool refresh = false, CancellationToken ct = default);

    /// <summary>
    /// The verdict as it stands, without waiting for a server that does
    /// not answer: the last answer, or the known servers in a checking
    /// state when there is none yet. The probe runs in the background and
    /// raises <see cref="Changed"/> when the answers are in.
    /// </summary>
    Task<IReadOnlyList<MediaServerConnectionStatus>> CurrentAsync(
        CancellationToken ct = default);
}

/// <summary>
/// Resolves the directory that speaks a stored server kind. Three
/// directories are registered (Jellyfin, Plex, Subsonic) and resolving a
/// single <see cref="IMediaServerDirectory"/> silently returns the last
/// registration, so every kind-based lookup goes through here instead.
/// </summary>
public interface IMediaServerDirectoryFactory
{
    /// <summary>Every registered directory, in registration order.</summary>
    IReadOnlyList<IMediaServerDirectory> All { get; }

    /// <summary>
    /// The directory for a kind ("Jellyfin", "Plex", "Subsonic"), with
    /// aliases applied ("Navidrome" is Subsonic). Null when no kind is
    /// given and the composition is not trimmed to exactly one directory,
    /// or when nothing speaks the kind.
    /// </summary>
    IMediaServerDirectory? For(string? kind);
}

/// <summary>
/// Non-strict download policy: when the requested quality cannot be
/// delivered, the manager walks down the quality ladder the enabled
/// plugins declare (QualityLadder). AllowContainerChange says whether a
/// fallback step may leave the requested container: a FLAC request may
/// land on the best lossy file only when it is on. Leaving the policy out
/// of the call is the strict mode: the requested quality or nothing.
/// </summary>
public record DownloadFallback(bool AllowContainerChange);

public interface IDownloadManager
{
    Task<string?> DownloadAsync(string sourceUrl, string outputDirectory, string melodyId, CancellationToken ct = default);
    /// <summary>
    /// Replaces the live progress entry for a manual download after the file
    /// is renamed and tagged, so the overview shows the path that exists and
    /// a failed pick never stays on "downloading".
    /// </summary>
    void ReportProgress(DownloadProgress progress);

    /// <summary>
    /// Updates the displayed path after the pipeline renamed a finished
    /// file, so the overview does not show the plugin's temporary name.
    /// A no-op when the track has no live entry.
    /// </summary>
    void UpdateProgressPath(string melodyId, string filePath) { }
    /// <summary>
    /// Search for the track through the plugin waterfall, download it into
    /// outputDirectory, tag the MELODY_ID, return the path. The query
    /// carries the playlist's advertised duration and the ISRC when known;
    /// both steer candidate ranking, and gross mismatches (a 30s preview
    /// for a 4-minute track) are rejected before any bytes are spent.
    /// The fallback policy decides whether a miss may step down to a lower
    /// quality; without it the request is strict.
    /// <paramref name="avoidPluginId"/> skips one plugin for this attempt
    /// (a retry after a flagged file, so another provider gets its turn).
    /// <paramref name="journal"/> carries the producing plugin back to the
    /// caller instead of manager-internal per-song memory, so two
    /// concurrent downloads of the same song cannot stamp each other's
    /// rows with the other run's provider.
    /// </summary>
    Task<string?> DownloadTrackAsync(TrackQuery query, string outputDirectory, string melodyId,
        DownloadFallback? fallback = null, string? avoidPluginId = null, CancellationToken ct = default,
        DownloadJournal? journal = null);
    /// <summary>
    /// Search every enabled plugin for the track and return all candidates
    /// (best first) without the acceptance gate: the manual source picker
    /// lists them. Plugins that report unavailable or throw are skipped.
    /// </summary>
    Task<IReadOnlyList<DownloadCandidate>> SearchCandidatesAsync(
        TrackQuery query, CancellationToken ct = default);
    /// <summary>
    /// The same list with every plugin searched at the same time:
    /// <paramref name="onCandidate"/> receives each hit the moment its
    /// plugin answers, so a picker fills in while the rest still runs.
    /// One search request per plugin, exactly like the sequential call.
    /// </summary>
    Task<IReadOnlyList<DownloadCandidate>> SearchCandidatesAsync(
        TrackQuery query, Action<DownloadCandidate>? onCandidate,
        CancellationToken ct = default);
    /// <summary>
    /// The same list plus the plugins that could not be searched and why.
    /// Callers that only need candidates use the overload above; the source
    /// picker uses this one so an empty result never hides a timeout, a
    /// thrown search or an unavailable plugin.
    /// </summary>
    async Task<SourceSearchOutcome> SearchCandidatesDetailedAsync(
        TrackQuery query, Action<DownloadCandidate>? onCandidate = null,
        CancellationToken ct = default)
    {
        var candidates = await SearchCandidatesAsync(query, onCandidate, ct);
        return new SourceSearchOutcome(candidates, Array.Empty<SourceSearchProblem>());
    }
    /// <summary>
    /// Download one candidate a human picked by hand. The choice overrides
    /// the ranking, never the sanity: non-audio bytes are still rejected
    /// and the MELODY_ID tag is written.
    /// </summary>
    Task<string?> DownloadCandidateAsync(
        DownloadCandidate candidate, TrackQuery query, string outputDirectory,
        string melodyId, CancellationToken ct = default);
    /// <summary>
    /// The exact audio facts a plugin can read for one candidate without
    /// downloading it, for the picker's quality line. A plugin that cannot
    /// know returns null and the caller falls back to the manifest.
    /// </summary>
    Task<CandidateAudioQuality?> ProbeQualityAsync(
        string pluginId, string sourceUrl, CancellationToken ct = default)
        => Task.FromResult<CandidateAudioQuality?>(null);
    /// <summary>Current in-flight download states (for UI polling).</summary>
    IReadOnlyList<DownloadProgress> SnapshotProgress();
    /// <summary>
    /// Id of the plugin that produced the last successful DownloadTrackAsync
    /// for this melodyId, or null when the last attempt produced nothing.
    /// The store records it on the track row so a later Retry can avoid
    /// the provider that delivered a flagged file.
    /// </summary>
    string? LastPluginId(string melodyId);
    /// <summary>
    /// Id of the last plugin that actually searched for this melodyId,
    /// whether it delivered or not. A bulk "retry elsewhere" uses it to
    /// leave out the provider that just failed.
    /// </summary>
    string? LastAttemptedPluginId(string melodyId);
    /// <summary>
    /// Why the last DownloadTrackAsync for this melodyId produced nothing:
    /// files outside the quality filters, or no hit at all. Null when the
    /// last call succeeded or never ran.
    /// </summary>
    string? LastFailure(string melodyId);
}

/// <summary>
/// Outcome of deleting one track's local files without removing the row:
/// <paramref name="Deleted"/> is true when at least one file was removed
/// and everything this row owned is gone; <paramref name="Error"/> is set
/// when a file could not be deleted, in which case the row is untouched.
/// </summary>
public readonly record struct TrackFileDeleteResult(bool Deleted, string? Error);

/// <summary>
/// One candidate a plugin offered for a track: what the "choose a source"
/// dialog lists. The list deliberately skips the acceptance gate, so a
/// human can pick what the automatic match refused.
/// <paramref name="CanServeRequested"/> says whether the plugin can
/// deliver the requested quality; false means the pick is fetched at its
/// source quality instead of re-encoded. <paramref name="DeclaredQuality"/>
/// names what the plugin can deliver, for candidates no exact probe covers.
/// <paramref name="BitDepth"/> and <paramref name="SampleRateHz"/> carry
/// per-hit facts the search already knew (Qobuz's track maximum).
/// </summary>
public record DownloadCandidate(
    string PluginId,
    string PluginName,
    string Title,
    string? Artist,
    TimeSpan? Duration,
    int? BitrateKbps,
    string SourceUrl,
    MatchConfidence Confidence,
    bool CanServeRequested = true,
    string? DeclaredQuality = null,
    int? BitDepth = null,
    int? SampleRateHz = null);

/// <summary>
/// One enabled plugin that the source picker could not search, with the
/// short reason it gave. A plugin that searched and simply found nothing is
/// not a problem; it was searched.
/// </summary>
public record SourceSearchProblem(
    string PluginId,
    string PluginName,
    string Reason);

/// <summary>
/// What a manual source search produced: every candidate, best first, and
/// the plugins that were skipped before their search could run.
/// </summary>
public record SourceSearchOutcome(
    IReadOnlyList<DownloadCandidate> Candidates,
    IReadOnlyList<SourceSearchProblem> Problems);

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
    MediaServerConnection? MediaServerConnection = null,
    Dictionary<string, string>? ExtensionRemap = null
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
    /// True when both paths name the same inode, so one is already the
    /// hard link the other would create. False when either file is
    /// missing or cannot be read.
    /// </summary>
    bool SameFile(string pathA, string pathB);

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
    /// <summary>Whether the candidate satisfies the requested profile.
    /// FindCandidateAsync cannot know the target; TryLinkAsync replaces the
    /// placeholder with the real verdict.</summary>
    bool QualityMatches,
    /// <summary>
    /// Archive copy that belongs to the existing row, when one was made.
    /// A deduplicated track reuses it instead of writing duplicate bytes.
    /// </summary>
    string? ExistingArchivePath = null)
{
    /// <summary>Human summary of the mismatch, for the UI warning.</summary>
    public string Describe()
    {
        var what = ExistingBitrateKbps is { } kbps
            ? $"{ExistingFormat} {kbps} kbps"
            : $"{ExistingFormat ?? "unknown format"} (bitrate unknown)";
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
        if (string.IsNullOrWhiteSpace(existingFormat))
            return false;

        var requested = RequestedContainer(quality);
        if (requested is not null && !FormatEquals(existingFormat, requested))
            return false;

        // Lossless has no meaningful bitrate target: the container is the
        // promise. A missing measurement must not pause a FLAC link.
        if (requested == "flac" && FormatEquals(existingFormat, "flac"))
            return true;

        // Without a bitrate band there is nothing to check. A known format
        // is enough, even when the probe could not measure the bitrate.
        if (quality.MinKbps is null && quality.MaxKbps is null)
            return true;

        // A band needs a measurement. Unknown facts stay a human decision
        // rather than a silent hardlink of an unverified lossy file.
        if (existingBitrateKbps is not > 0)
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
