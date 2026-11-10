namespace MelodyBridge.Core;

public class Track
{
    public string? Title { get; set; }
    public string? Artist { get; set; }
    /// <summary>Album name, when the source knows it (used for tags).</summary>
    public string? Album { get; set; }
    /// <summary>Album artist (the release-level credit), when the source knows it.</summary>
    public string? AlbumArtist { get; set; }
    /// <summary>Cover art URL, when the source knows one; embedded after download.</summary>
    public string? CoverUrl { get; set; }
    /// <summary>Plain lyrics, filled by the lyrics resolver when one is configured.</summary>
    public string? Lyrics { get; set; }
    /// <summary>
    /// International Standard Recording Code, when the source or the
    /// resolver knows it. Plugins that serve ISRC-indexed catalogs
    /// (Qobuz-backed ones) search by it first for exact matches.
    /// </summary>
    public string? Isrc { get; set; }
    public TimeSpan? Duration { get; set; }
    public SongID? SongID { get; set; }
    public SongID? PlatformSongID { get; set; }
    public TrackQuality? Quality { get; set; }
    public Platform SourcePlatform { get; set; }
    public SyncStatus SyncStatus { get; set; }
    public MediaType MediaType { get; set; }
    public FileLocation? CurrentTrackLocation { get; set; }

    /// <summary>True when the track is one of the user's liked songs.</summary>
    public bool IsLiked { get; set; }
}

public class Playlist
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Owner { get; set; }
    public string? Description { get; set; }
    public string? SourceUrl { get; set; }
    public string? CoverImageUrl { get; set; }
    public int? TrackCount { get; set; }
    public TimeSpan? Duration { get; set; }
    public List<Track>? Tracks { get; set; }
    /// <summary>
    /// Non-fatal sync problem worth surfacing to the user: partial fetch
    /// (fewer tracks than the source declares), rate limiting, and so on.
    /// Null when the sync was clean. The sync status stays honest: a
    /// playlist with a warning is not a clean Completed.
    /// </summary>
    public string? Warning { get; set; }
}

public class SyncPlaylistJob
{
    public Playlist? PlaylistToSync { get; set; }
    public SyncStatus Status { get; set; }
    public DownloadLocation? DownloadLocation { get; set; }
}

/// <summary>
/// One place a sync job publishes to: an M3U file (with its path) or a
/// media server profile. A job may carry several, so one run can write
/// the M3U and push to Jellyfin and Plex at once. A server target with a
/// null profile is a legacy job's own inline connection.
/// </summary>
public record SyncTarget(
    OutputTargetType Kind,
    string? ProfileId = null,
    string? M3uPath = null,
    string? RuleProfileId = null,
    string? RuleProfileName = null,
    string? SourceArchiveProfileId = null,
    string? PlaylistName = null,
    Dictionary<string, string>? PathRemapRules = null,
    Dictionary<string, string>? ExtensionRemapRules = null,
    bool M3uRelativePaths = false,
    bool M3uCrlf = false,
    bool M3uBackslashes = false,
    bool M3uBom = false,
    bool AllowMetadataFallback = true,
    /// <summary>Per-job unique M3U file name, as quick publish creates when
    /// two playlist names sanitize to the same file. The profile still owns
    /// the folder, so a folder edit reaches the job.</summary>
    string? M3uFileNameOverride = null);

public class SyncJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string? SourceId { get; set; }
    /// <summary>Named output/rule profile this job follows. When set, the
    /// profile supplies the M3U folder, format and path rules; null uses
    /// the values stored on the job.</summary>
    public string? RuleProfileId { get; set; }
    /// <summary>Profile name for the UI; filled when the job is mapped.</summary>
    public string? RuleProfileName { get; set; }
    public List<string> SearchLocationPaths { get; set; } = new();
    /// <summary>Every place this job publishes to. Empty means the legacy
    /// single target described by OutputTarget/M3uOutputPath unless
    /// <see cref="HasExplicitTargets"/> is true.</summary>
    public List<SyncTarget> Targets { get; set; } = new();
    /// <summary>True when the stored row carried a target list, even an
    /// empty one. False for legacy rows that only carry OutputTarget.</summary>
    public bool HasExplicitTargets { get; set; }
    public OutputTargetType OutputTarget { get; set; } = OutputTargetType.M3uFile;
    public string? JellyfinServerUrl { get; set; }
    public string? JellyfinApiKey { get; set; }
    public string? JellyfinUserId { get; set; }
    /// <summary>Media server profile id (Settings, Media servers); null
    /// for legacy jobs with inline connection values.</summary>
    public string? MediaServerProfileId { get; set; }
    public string? M3uOutputPath { get; set; }
    /// <summary>Write M3U entries relative to the output file instead of
    /// absolute paths, so the playlist travels with the library.</summary>
    public bool M3uRelativePaths { get; set; }
    /// <summary>Windows line endings in the M3U.</summary>
    public bool M3uCrlf { get; set; }
    /// <summary>Backslashes in the relative M3U entries.</summary>
    public bool M3uBackslashes { get; set; }
    /// <summary>Start the M3U with a UTF-8 BOM.</summary>
    public bool M3uBom { get; set; }
    public Dictionary<string, string> PathRemapRules { get; set; } = new();
    public Dictionary<string, string> ExtensionRemapRules { get; set; } = new();
    public SyncJobSchedule Schedule { get; set; } = SyncJobSchedule.Manual;
    public string? CronExpression { get; set; }
    public SyncStatus LastRunStatus { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string? LastRunSummary { get; set; }
}

/// <summary>
/// A reusable bundle of sync output rules: where M3U files land, their
/// format, and the path/extension remaps every linked job uses.
/// </summary>
public class SyncRuleProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    /// <summary>Destination kind this profile publishes to. A profile is
    /// one target: an M3U file or one media server kind.</summary>
    public OutputTargetType Kind { get; set; } = OutputTargetType.M3uFile;
    /// <summary>Media server connection profile for a server kind. Null
    /// falls back to the app default, like a legacy job does.</summary>
    public string? MediaServerProfileId { get; set; }
    /// <summary>Playlist name on a media server; {job} and {playlist}
    /// expand to the job name.</summary>
    public string PlaylistNamePattern { get; set; } = "{job}";
    /// <summary>Optional archive copy to publish: null for the primary
    /// file, "default" for Tracks.ArchivePath, or an archive profile id.
    /// When set, the runner skips tracks without that copy and media
    /// servers only resolve the exact archive path.</summary>
    public string? SourceArchiveProfileId { get; set; }
    public string M3uOutputFolder { get; set; } = "/app/playlists";
    /// <summary>File name pattern; {job} and {playlist} expand to the job name.</summary>
    public string M3uFileNamePattern { get; set; } = "{job}.m3u8";
    public bool M3uRelativePaths { get; set; }
    public bool M3uCrlf { get; set; } = true;
    public bool M3uBackslashes { get; set; }
    public bool M3uBom { get; set; } = true;
    public Dictionary<string, string> PathRemapRules { get; set; } = new();
    public Dictionary<string, string> ExtensionRemapRules { get; set; } = new();
}

/// <summary>Source variants a publish profile can read from.</summary>
public static class SyncSourceVariants
{
    /// <summary>The legacy single archive copy in Tracks.ArchivePath.</summary>
    public const string DefaultArchive = "default";
}

public class DownloaderConfig
{
    public string ProviderId { get; set; } = string.Empty;
    public int Priority { get; set; }
    public bool IsEnabled { get; set; } = true;
    public Dictionary<string, string> Settings { get; set; } = new();
}

public record SongID(Platform Platform, string ID);
public record TrackQuality(int Bitrate, MediaType Format);
public record ScanLocation(string Path);
public record DownloadLocation(string Path);
public record FileLocation(string Path);

/// <summary>One from/to replace rule; serialized as JSON string pairs.</summary>
public record RemapRule(string From, string To);

/// <summary>Per-track warnings of one run, stored as a JSON string array.</summary>
public record SyncJobRunLog(DateTime Timestamp, SyncStatus Status, string Message, int ResolvedTracks, int TotalTracks, List<string>? WarningDetails = null);