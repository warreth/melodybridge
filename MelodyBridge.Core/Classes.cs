namespace MelodyBridge.Core;

public class Track
{
    public string? Title { get; set; }
    public string? Artist { get; set; }
    /// <summary>Album name, when the source knows it (used for tags).</summary>
    public string? Album { get; set; }
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
}

public class SyncPlaylistJob
{
    public Playlist? PlaylistToSync { get; set; }
    public SyncStatus Status { get; set; }
    public DownloadLocation? DownloadLocation { get; set; }
}

public class SyncJob
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string? SourceId { get; set; }
    public List<string> SearchLocationPaths { get; set; } = new();
    public OutputTargetType OutputTarget { get; set; } = OutputTargetType.M3uFile;
    public string? JellyfinServerUrl { get; set; }
    public string? JellyfinApiKey { get; set; }
    public string? JellyfinUserId { get; set; }
    public string? M3uOutputPath { get; set; }
    public Dictionary<string, string> PathRemapRules { get; set; } = new();
    public Dictionary<string, string> ExtensionRemapRules { get; set; } = new();
    public SyncJobSchedule Schedule { get; set; } = SyncJobSchedule.Manual;
    public string? CronExpression { get; set; }
    public SyncStatus LastRunStatus { get; set; }
    public DateTime? LastRunAt { get; set; }
    public string? LastRunSummary { get; set; }
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