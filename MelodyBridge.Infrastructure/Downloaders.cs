namespace MelodyBridge.Infrastructure.Downloaders;
// concrete implementation (database, downloaders, media plugins)
// gets implemented as dependency-injected services
// referenced by UI projects

using MelodyBridge.Core;

public class squidwtfDownloaderPlugin : IDownloaderPlugin
{
    public List<TrackQuality> GetSupportedQualities() => SoundQualities.GetSquidwtfQualities();
    public Track DownloadTrack(SongID songID, TrackQuality quality)
    {
        //How to match a quality to the right platform? Like an array ? or if quality=flac then platform = qobuz?
    }
}
