namespace MelodyBridge.Core;
// Contains enums
// Used by other projects
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
    Zero, //for streamrip quality 0
    One,
    Two,
    Three
}

public enum DownloadSource
{
    ytdlp,
    squidwtf,
    dabmusicxyz,
    jumodl,
    hifiapi,
    streamrip
}

public enum SyncStatus
{
    Pending,
    InProgress,
    Completed,
    Failed
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

