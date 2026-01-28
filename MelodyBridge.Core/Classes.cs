namespace MelodyBridge.Core;
// Contains models, interfaces
// Used by other projects

public class Track
{
    public string? Title;
    public string? Artist;
    public SongID? SongID;
    public TrackQuality? Quality;
    public FileLocation? CurrentTrackLocation;
    public Platform SourcePlatform;
    public SyncStatus SyncStatus;
    public MediaType MediaType;
}

public class Playlist
{
    public string? Name;
    public List<Track>? Tracks; //TODO: Implement in sql
}

public class SyncPlaylistJob
{
    public Playlist? PlaylistToSync;
    public SyncStatus Status;
    public DownloadLocation? DownloadLocation;

}

public record SongID(Platform Platform, string ID);
public record TrackQuality(int Bitrate, MediaType Format);

public record ScanLocation(string Path); //Path to scan for media files
public record DownloadLocation(string Path); //Path to save downloaded files
public record FileLocation(string Path); //Path to the file on disk
