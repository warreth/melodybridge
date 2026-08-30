using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Playlists;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// M3uGenerator tests. Every assertion reads the produced file back
/// from disk — no mocked writers.
/// </summary>
[TestFixture]
public class M3uGeneratorTests
{
    private M3uGenerator NewGenerator() => new(NullLogger<M3uGenerator>.Instance);

    private static string TempM3u()
        => Path.Combine(Path.GetTempPath(), $"mb-m3u-{Guid.NewGuid():N}.m3u");

    private static Playlist Tracks(params Track[] tracks)
        => new() { Name = "Test", Tracks = tracks.ToList() };

    [Test]
    public async Task EmptyPlaylist_WritesHeaderOnly()
    {
        var outputPath = TempM3u();
        try
        {
            var result = await NewGenerator().GenerateM3uAsync(
                Tracks(), Array.Empty<ScanLocation>(), new PlaylistOutputOptions(outputPath, false, null));

            Assert.That(result, Is.EqualTo(outputPath));
            var content = await File.ReadAllLinesAsync(outputPath);
            Assert.That(content, Is.EqualTo(new[] { "#EXTM3U" }));
        }
        finally { File.Delete(outputPath); }
    }

    [Test]
    public async Task TrackWithPath_GetsExtinfMetadataAndPath()
    {
        var outputPath = TempM3u();
        try
        {
            var playlist = Tracks(new Track
            {
                Title = "Für Elise",
                Artist = "Beethoven",
                Duration = TimeSpan.FromSeconds(173),
                CurrentTrackLocation = new FileLocation("/music/fur-elise.mp3"),
            });

            await NewGenerator().GenerateM3uAsync(
                playlist, Array.Empty<ScanLocation>(), new PlaylistOutputOptions(outputPath, false, null));

            var content = await File.ReadAllLinesAsync(outputPath);
            Assert.That(content, Has.Length.EqualTo(3), "header + EXTINF + path");
            Assert.That(content[1], Is.EqualTo("#EXTINF:173,Beethoven - Für Elise"));
            Assert.That(content[2], Is.EqualTo("/music/fur-elise.mp3"));
        }
        finally { File.Delete(outputPath); }
    }

    [Test]
    public async Task TrackWithoutArtist_LabelsTitleOnly()
    {
        var outputPath = TempM3u();
        try
        {
            var playlist = Tracks(new Track
            {
                Title = "Untitled",
                CurrentTrackLocation = new FileLocation("/music/a.mp3"),
            });

            await NewGenerator().GenerateM3uAsync(
                playlist, Array.Empty<ScanLocation>(), new PlaylistOutputOptions(outputPath, false, null));

            var content = await File.ReadAllLinesAsync(outputPath);
            Assert.That(content[1], Is.EqualTo("#EXTINF:-1,Untitled"), "unknown duration is -1");
        }
        finally { File.Delete(outputPath); }
    }

    [Test]
    public async Task TrackWithoutPath_IsSkipped()
    {
        var outputPath = TempM3u();
        try
        {
            var playlist = Tracks(
                new Track { Title = "No file", SongID = new SongID(Platform.Qobuz, "x") },
                new Track { Title = "Has file", CurrentTrackLocation = new FileLocation("/music/b.mp3") });

            await NewGenerator().GenerateM3uAsync(
                playlist, Array.Empty<ScanLocation>(), new PlaylistOutputOptions(outputPath, false, null));

            var content = await File.ReadAllLinesAsync(outputPath);
            Assert.That(content, Has.Length.EqualTo(3), "only the track with a file is written");
            Assert.That(content[2], Is.EqualTo("/music/b.mp3"));
        }
        finally { File.Delete(outputPath); }
    }

    [Test]
    public async Task PathRemap_RewritesPrefix()
    {
        var outputPath = TempM3u();
        try
        {
            var playlist = Tracks(new Track
            {
                Title = "Song",
                CurrentTrackLocation = new FileLocation("/mnt/music/artist/album/song.flac"),
            });

            await NewGenerator().GenerateM3uAsync(
                playlist, Array.Empty<ScanLocation>(),
                new PlaylistOutputOptions(outputPath, false,
                    new Dictionary<string, string> { ["/mnt/music"] = "/data/music" }));

            var content = await File.ReadAllLinesAsync(outputPath);
            Assert.That(content[2], Is.EqualTo("/data/music/artist/album/song.flac"));
        }
        finally { File.Delete(outputPath); }
    }

    [Test]
    public async Task RelativePaths_AreRelativeToOutputDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mb-m3u-dir-{Guid.NewGuid():N}");
        var outputPath = Path.Combine(dir, "test.m3u");
        Directory.CreateDirectory(dir);
        try
        {
            var playlist = Tracks(new Track
            {
                Title = "Song",
                CurrentTrackLocation = new FileLocation(Path.Combine(dir, "sub", "song.flac")),
            });

            await NewGenerator().GenerateM3uAsync(
                playlist, Array.Empty<ScanLocation>(), new PlaylistOutputOptions(outputPath, true, null));

            var content = await File.ReadAllLinesAsync(outputPath);
            Assert.That(content[2], Is.EqualTo(Path.Combine("sub", "song.flac")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public void NullTracks_Throws()
    {
        var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
            await NewGenerator().GenerateM3uAsync(
                new Playlist { Name = "X", Tracks = null! },
                Array.Empty<ScanLocation>(),
                new PlaylistOutputOptions(TempM3u(), false, null)));
        Assert.That(ex!.Message, Does.Contain("no tracks"));
    }
}
