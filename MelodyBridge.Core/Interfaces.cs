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

public static class SoundQualities
{
    // Returns the list of supported Qobuz qualities
    public static List<TrackQuality> GetSquidwtfQualities()
    {
        return new List<TrackQuality>
        {
            new TrackQuality(320, MediaType.AAC), //Tidal
            new TrackQuality(320, MediaType.OPUS), //Amazon Music
            new TrackQuality(320, MediaType.MP3), //Soundcloud or Qobuz
            new TrackQuality(24, MediaType.FLAC), //Tidal 192kHz/24bit
            new TrackQuality(24, MediaType.FLAC), //AmazonMusic
            new TrackQuality(24, MediaType.FLAC) //Qobuz
        };
    }
    public static List<TrackQuality> GetDabmusicxyzQualities()
    {
        return new List<TrackQuality>
        {
            new TrackQuality(16, MediaType.FLAC),
            new TrackQuality(24, MediaType.FLAC), //Not always, but highest available
        };
    }
    public static List<TrackQuality> GetJumodlQualities()
    {
        return new List<TrackQuality>
        {
            new TrackQuality(320, MediaType.MP3),
            new TrackQuality(16, MediaType.FLAC),
            new TrackQuality(24, MediaType.FLAC), //Not always, but highest available
        };
    }
    public static List<TrackQuality> GetStreamripQualities()
    {
        return new List<TrackQuality>
        {
            new TrackQuality(128, MediaType.MP3), //0
            new TrackQuality(320, MediaType.FLAC), //1
            new TrackQuality(16, MediaType.FLAC), //2
            new TrackQuality(24, MediaType.FLAC), //3
            //I dont include 192kHz cuz nobody uses it 
        };
    }

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