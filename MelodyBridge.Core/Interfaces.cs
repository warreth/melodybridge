using System.Net.NetworkInformation;
using MelodyBridge.Core;

namespace MelodyBridge.Core;
// Contains interfaces
// Used by other projects
public interface IDownloaderPlugin
{
    //DownloadSource SupportedDownloadSource { get; } //Which download source this plugin uses
    Track DownloadTrack(SongID songID, TrackQuality quality); //Download a track with specified quality
}

/// <summary>
/// Static helpers that return supported qualities for various sources.
/// </summary>
public static class ProviderQualities
{
    public static List<TrackQuality> SquidWtf => new()
    {
        new(320, MediaType.AAC),   // Tidal
        new(320, MediaType.OPUS),  // Amazon Music
        new(320, MediaType.MP3),   // SoundCloud / Qobuz
        new(24, MediaType.FLAC),   // Tidal / Amazon / Qobuz Hi-Res
    };

    public static List<TrackQuality> Lucida => new()
    {
        new(128, MediaType.MP3),
        new(320, MediaType.MP3),
        new(16, MediaType.FLAC),
        new(24, MediaType.FLAC),
    };

    public static List<TrackQuality> DoubleDouble => new()
    {
        new(320, MediaType.MP3),
        new(16, MediaType.FLAC),
        new(24, MediaType.FLAC),
    };

    public static List<TrackQuality> Monochrome => new()
    {
        new(320, MediaType.AAC),
        new(16, MediaType.FLAC),
        new(24, MediaType.FLAC),
    };

    public static List<TrackQuality> YouTubeDlp => new()
    {
        new(128, MediaType.MP3),
        new(192, MediaType.MP3),
        new(320, MediaType.MP3),
        new(256, MediaType.AAC),
    };
}

/// <summary>
/// Interface for file download strategies for different sites.
/// </summary>
public interface IFileDownloadStrategy
{
    /// <summary>
    /// Downloads a file from the given URL to the specified file path.
    /// </summary>
    Task DownloadFileAsync(string url, string filePath);
    /// <summary>
    /// Returns true if this strategy can handle the given URL.
    /// </summary>
    bool CanHandle(string url);
}