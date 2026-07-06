namespace MelodyBridge.Core;
// Contains models, interfaces
// Used by other projects

public class Track
{
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public SongID? SongID { get; set; } //The ISRC song ID
    public SongID? PlatformSongID { get; set; } //Like the qobuz ID for squid.wtf downloads
    public TrackQuality? Quality { get; set; }
    public Platform SourcePlatform { get; set; }
    public SyncStatus SyncStatus { get; set; }
    public MediaType MediaType { get; set; }
    public FileLocation? CurrentTrackLocation { get; set; }
}

public class Playlist
{
    public string? Name { get; set; }
    public List<Track>? Tracks { get; set; } //TODO: Implement in sql
}

public class SyncPlaylistJob
{
    public Playlist? PlaylistToSync { get; set; }
    public SyncStatus Status { get; set; }
    public DownloadLocation? DownloadLocation { get; set; }

}

public record SongID(Platform Platform, string ID);
public record TrackQuality(int Bitrate, MediaType Format);

public record ScanLocation(string Path); //Path to scan for media files
public record DownloadLocation(string Path); //Path to save downloaded files
public record FileLocation(string Path); //Path to the file on disk