namespace MelodyBridge.Core;
// Contains interfaces
// Used by other projects
public interface IDownloaderPlugin
{
    Platform SupportedPlatform { get; } //Which platform this plugin supports
    List<TrackQuality> GetAvailableQualities(SongID songID); //Get available qualities for a track
    Track DownloadTrack(SongID songID, TrackQuality quality); //Download a track with specified quality
}

