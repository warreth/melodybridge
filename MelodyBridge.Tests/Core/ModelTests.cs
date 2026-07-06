using MelodyBridge.Core;

namespace MelodyBridge.Tests.Core;

[TestFixture]
public class ModelTests
{
    [Test]
    public void Track_DefaultValues()
    {
        var track = new Track();
        Assert.Multiple(() =>
        {
            Assert.That(track.Title, Is.Null);
            Assert.That(track.Artist, Is.Null);
            Assert.That(track.SongID, Is.Null);
            Assert.That(track.PlatformSongID, Is.Null);
            Assert.That(track.Quality, Is.Null);
            Assert.That(track.SourcePlatform, Is.EqualTo(Platform.Spotify));
            Assert.That(track.SyncStatus, Is.EqualTo(SyncStatus.Pending));
            Assert.That(track.MediaType, Is.EqualTo(MediaType.MP3));
            Assert.That(track.CurrentTrackLocation, Is.Null);
        });
    }

    [Test]
    public void Track_SetProperties()
    {
        var track = new Track
        {
            Title = "Test Song",
            Artist = "Test Artist",
            SourcePlatform = Platform.Qobuz,
            SyncStatus = SyncStatus.Completed,
            MediaType = MediaType.FLAC,
        };

        Assert.Multiple(() =>
        {
            Assert.That(track.Title, Is.EqualTo("Test Song"));
            Assert.That(track.Artist, Is.EqualTo("Test Artist"));
            Assert.That(track.SourcePlatform, Is.EqualTo(Platform.Qobuz));
            Assert.That(track.SyncStatus, Is.EqualTo(SyncStatus.Completed));
            Assert.That(track.MediaType, Is.EqualTo(MediaType.FLAC));
        });
    }

    [Test]
    public void SongID_Record()
    {
        var id = new SongID(Platform.Spotify, "abc123");
        Assert.Multiple(() =>
        {
            Assert.That(id.Platform, Is.EqualTo(Platform.Spotify));
            Assert.That(id.ID, Is.EqualTo("abc123"));
        });
    }

    [Test]
    public void SongID_Equals_ByValue()
    {
        var id1 = new SongID(Platform.Tidal, "track456");
        var id2 = new SongID(Platform.Tidal, "track456");
        Assert.That(id1, Is.EqualTo(id2));
        Assert.That(id1 == id2, Is.True);
        Assert.That(id1.GetHashCode(), Is.EqualTo(id2.GetHashCode()));
    }

    [Test]
    public void TrackQuality_Record()
    {
        var quality = new TrackQuality(320, MediaType.MP3);
        Assert.Multiple(() =>
        {
            Assert.That(quality.Bitrate, Is.EqualTo(320));
            Assert.That(quality.Format, Is.EqualTo(MediaType.MP3));
        });
    }

    [Test]
    public void TrackQuality_Equals_ByValue()
    {
        var q1 = new TrackQuality(320, MediaType.MP3);
        var q2 = new TrackQuality(320, MediaType.MP3);
        var q3 = new TrackQuality(24, MediaType.FLAC);

        Assert.Multiple(() =>
        {
            Assert.That(q1, Is.EqualTo(q2));
            Assert.That(q1 == q2, Is.True);
            Assert.That(q1 != q3, Is.True);
            Assert.That(q1.Equals(q3), Is.False);
        });
    }

    [Test]
    public void SearchResult_Record()
    {
        var qualities = new[] { new TrackQuality(320, MediaType.MP3) };
        var result = new SearchResult("Song", "Artist", "Album", "https://example.com/track/1", Platform.Qobuz, qualities);

        Assert.Multiple(() =>
        {
            Assert.That(result.Title, Is.EqualTo("Song"));
            Assert.That(result.Artist, Is.EqualTo("Artist"));
            Assert.That(result.Album, Is.EqualTo("Album"));
            Assert.That(result.Url, Is.EqualTo("https://example.com/track/1"));
            Assert.That(result.SourcePlatform, Is.EqualTo(Platform.Qobuz));
            Assert.That(result.AvailableQualities, Has.One.Items);
        });
    }

    [Test]
    public void TrackInfo_Record()
    {
        var qualities = new[] { new TrackQuality(24, MediaType.FLAC) };
        var info = new TrackInfo("Hi-Res Track", "Artist", "Album", "https://example.com/cover.jpg",
            "https://tidal.com/track/1", Platform.Tidal, qualities);

        Assert.Multiple(() =>
        {
            Assert.That(info.Title, Is.EqualTo("Hi-Res Track"));
            Assert.That(info.CoverUrl, Is.EqualTo("https://example.com/cover.jpg"));
            Assert.That(info.SourcePlatform, Is.EqualTo(Platform.Tidal));
        });
    }

    [Test]
    public void DownloadResult_Success()
    {
        var result = new DownloadResult(true, "/path/to/file.flac", null, new TrackQuality(24, MediaType.FLAC));
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.FilePath, Is.EqualTo("/path/to/file.flac"));
            Assert.That(result.ErrorMessage, Is.Null);
            Assert.That(result.ActualQuality, Is.Not.Null);
        });
    }

    [Test]
    public void DownloadResult_Failure()
    {
        var result = new DownloadResult(false, null, "Something went wrong", null);
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.FilePath, Is.Null);
            Assert.That(result.ErrorMessage, Is.EqualTo("Something went wrong"));
            Assert.That(result.ActualQuality, Is.Null);
        });
    }

    [Test]
    public void ProviderMetadata_Record()
    {
        var platforms = new[] { Platform.Qobuz, Platform.Tidal };
        var qualities = new[] { new TrackQuality(320, MediaType.MP3) };
        var meta = new ProviderMetadata("test-id", "Test Provider", "A test provider", "🧪", platforms, qualities);

        Assert.Multiple(() =>
        {
            Assert.That(meta.Id, Is.EqualTo("test-id"));
            Assert.That(meta.Name, Is.EqualTo("Test Provider"));
            Assert.That(meta.SupportedPlatforms, Has.Exactly(2).Items);
            Assert.That(meta.Icon, Is.EqualTo("🧪"));
        });
    }

    [Test]
    public void Playlist_WithTracks()
    {
        var tracks = new List<Track>
        {
            new() { Title = "Track 1", Artist = "A", SourcePlatform = Platform.Qobuz },
            new() { Title = "Track 2", Artist = "B", SourcePlatform = Platform.Tidal },
        };
        var playlist = new Playlist { Name = "Test Playlist", Tracks = tracks };

        Assert.Multiple(() =>
        {
            Assert.That(playlist.Name, Is.EqualTo("Test Playlist"));
            Assert.That(playlist.Tracks, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void Playlist_TracksCanBeEmpty()
    {
        var playlist = new Playlist { Name = "Empty List", Tracks = new List<Track>() };
        Assert.That(playlist.Tracks, Is.Empty);
    }

    [Test]
    public void FileLocation_Record()
    {
        var loc = new FileLocation("/music/song.flac");
        Assert.That(loc.Path, Is.EqualTo("/music/song.flac"));
    }

    [Test]
    public void ScanLocation_Record()
    {
        var loc = new ScanLocation("/music");
        Assert.That(loc.Path, Is.EqualTo("/music"));
    }

    [Test]
    public void DownloadLocation_Record()
    {
        var loc = new DownloadLocation("/downloads");
        Assert.That(loc.Path, Is.EqualTo("/downloads"));
    }

    [Test]
    public void PlaylistOutputOptions_Default()
    {
        var options = new PlaylistOutputOptions("/output/playlist.m3u", true, null);
        Assert.Multiple(() =>
        {
            Assert.That(options.OutputPath, Is.EqualTo("/output/playlist.m3u"));
            Assert.That(options.UseRelativePaths, Is.True);
            Assert.That(options.PathRemap, Is.Null);
        });
    }

    [Test]
    public void MediaServerSyncReport_Record()
    {
        var report = new MediaServerSyncReport(5, new[] { "path1", "path2" }, "playlist-1", "Synced successfully");
        Assert.Multiple(() =>
        {
            Assert.That(report.ResolvedCount, Is.EqualTo(5));
            Assert.That(report.UnresolvedPaths, Has.Length.EqualTo(2));
            Assert.That(report.PlaylistId, Is.EqualTo("playlist-1"));
            Assert.That(report.Message, Is.EqualTo("Synced successfully"));
        });
    }
}
