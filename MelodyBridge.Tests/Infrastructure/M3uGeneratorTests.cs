using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Playlists;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

[TestFixture]
public class M3uGeneratorTests
{
    private MelodyBridgeDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"M3uTest_{Guid.NewGuid()}")
            .Options;
        var db = new MelodyBridgeDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    [Test]
    public async Task GenerateM3uAsync_CreatesFileWithHeader()
    {
        using var db = CreateDbContext();
        var generator = new M3uGenerator(db, NullLogger<M3uGenerator>.Instance);

        var playlist = new Playlist
        {
            Name = "Test",
            Tracks = new List<Track>()
        };

        var outputPath = Path.GetTempFileName() + ".m3u";
        try
        {
            var options = new PlaylistOutputOptions(outputPath, false, null);
            var result = await generator.GenerateM3uAsync(playlist, Array.Empty<ScanLocation>(), options);

            Assert.That(result, Is.EqualTo(outputPath));
            Assert.That(File.Exists(outputPath), Is.True);

            var content = await File.ReadAllLinesAsync(outputPath);
            Assert.That(content[0], Is.EqualTo("#EXTM3U"));
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Test]
    public async Task GenerateM3uAsync_IncludesDbTrackPaths()
    {
        using var db = CreateDbContext();
        db.Tracks.Add(new TrackEntity
        {
            MelodyId = "melody-test-1",
            Title = "Test Track",
            Artist = "Test Artist",
            CurrentPath = "/music/test.flac",
        });
        await db.SaveChangesAsync();

        var generator = new M3uGenerator(db, NullLogger<M3uGenerator>.Instance);

        var playlist = new Playlist
        {
            Tracks = new List<Track>
            {
                new()
                {
                    SongID = new SongID(Platform.Qobuz, "melody-test-1"),
                    Title = "Test Track",
                }
            }
        };

        var outputPath = Path.GetTempFileName() + ".m3u";
        try
        {
            var options = new PlaylistOutputOptions(outputPath, false, null);
            await generator.GenerateM3uAsync(playlist, Array.Empty<ScanLocation>(), options);

            var content = await File.ReadAllLinesAsync(outputPath);
            Assert.That(content, Has.Some.Matches<string>(x => x.Contains("/music/test.flac")));
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Test]
    public void GenerateM3uAsync_NoTracks_Throws()
    {
        using var db = CreateDbContext();
        var generator = new M3uGenerator(db, NullLogger<M3uGenerator>.Instance);

        var playlist = new Playlist
        {
            Name = "No Tracks",
            Tracks = null
        };

        var ex = Assert.ThrowsAsync<ArgumentException>(async () =>
            await generator.GenerateM3uAsync(playlist!, Array.Empty<ScanLocation>(),
                new PlaylistOutputOptions("/tmp/test.m3u", false, null)));

        Assert.That(ex!.Message, Does.Contain("no tracks"));
    }

    [Test]
    public async Task GenerateM3uAsync_AppliesPathRemap()
    {
        using var db = CreateDbContext();
        db.Tracks.Add(new TrackEntity
        {
            MelodyId = "remap-test",
            CurrentPath = "/mnt/music/artist/album/song.flac",
        });
        await db.SaveChangesAsync();

        var generator = new M3uGenerator(db, NullLogger<M3uGenerator>.Instance);

        var playlist = new Playlist
        {
            Tracks = new List<Track>
            {
                new() { SongID = new SongID(Platform.Qobuz, "remap-test") }
            }
        };

        var remap = new Dictionary<string, string>
        {
            ["/mnt/music"] = "/data/music"
        };

        var outputPath = Path.GetTempFileName() + ".m3u";
        try
        {
            var options = new PlaylistOutputOptions(outputPath, false, remap);
            await generator.GenerateM3uAsync(playlist, Array.Empty<ScanLocation>(), options);

            var content = await File.ReadAllLinesAsync(outputPath);
            Assert.That(content, Has.Some.Matches<string>(x => x.Contains("/data/music/artist/album/song.flac")));
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Test]
    public async Task GenerateM3uAsync_RelativePaths()
    {
        using var db = CreateDbContext();
        db.Tracks.Add(new TrackEntity
        {
            MelodyId = "relative-test",
            CurrentPath = "/music/artist/album/song.flac",
        });
        await db.SaveChangesAsync();

        var generator = new M3uGenerator(db, NullLogger<M3uGenerator>.Instance);

        var playlist = new Playlist
        {
            Tracks = new List<Track>
            {
                new() { SongID = new SongID(Platform.Qobuz, "relative-test") }
            }
        };

        var outputPath = Path.Combine(Path.GetTempPath(), "playlists", "test.m3u");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        try
        {
            var options = new PlaylistOutputOptions(outputPath, true, null);
            await generator.GenerateM3uAsync(playlist, Array.Empty<ScanLocation>(), options);

            var content = await File.ReadAllLinesAsync(outputPath);
            Assert.That(content, Has.Some.Matches<string>(x => x.Contains("artist/album/song.flac")));
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
            Directory.Delete(Path.GetDirectoryName(outputPath)!, true);
        }
    }

    [Test]
    public async Task GenerateM3uAsync_SkipsTrack_WhenMelodyIdMissing()
    {
        using var db = CreateDbContext();
        var generator = new M3uGenerator(db, NullLogger<M3uGenerator>.Instance);

        var playlist = new Playlist
        {
            Tracks = new List<Track>
            {
                new() { SongID = null, Title = "No ID" },
                new() { SongID = new SongID(Platform.Qobuz, ""), Title = "Empty ID" },
            }
        };

        var outputPath = Path.GetTempFileName() + ".m3u";
        try
        {
            var options = new PlaylistOutputOptions(outputPath, false, null);
            await generator.GenerateM3uAsync(playlist, Array.Empty<ScanLocation>(), options);

            var content = await File.ReadAllLinesAsync(outputPath);
            Assert.That(content, Has.Exactly(1).Items); // Just the header
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }
}
