namespace MelodyBridge.Core;

public enum Platform
{
    Spotify,
    YouTubeMusic,
    AppleMusic,
    Tidal,
    AmazonMusic,
    Qobuz,
    Soundcloud,
    Deezer,
    Unknown,
}

public enum DownloadSource
{
    ytdlp,
    squidwtf,
    lucida,
    doubledouble,
    monochrome,
}

public enum SyncStatus
{
    Pending,
    InProgress,
    Completed,
    Failed
}

/// <summary>
/// How a playlist re-sync reconciles with its source.
/// </summary>
public enum PlaylistSyncMode
{
    /// <summary>New tracks are added; tracks removed from the source stay local.</summary>
    Additive,

    /// <summary>Local snapshot exactly matches the source; removed tracks are deleted.</summary>
    Mirror
}

public enum SyncJobSchedule
{
    Manual,
    Hourly,
    Daily,
    Weekly,
    Monthly,
    Cron
}

public enum OutputTargetType
{
    M3uFile,
    JellyfinApi,
    PlexApi
}

public enum MediaType
{
    MP3,
    AAC,
    FLAC,
    WAV,
    ALAC,
    OGG,
    MP4,
    MP4A,
    WEBM,
    OPUS,
    UNKNOWN
}

