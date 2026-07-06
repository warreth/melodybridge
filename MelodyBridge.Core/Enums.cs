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

