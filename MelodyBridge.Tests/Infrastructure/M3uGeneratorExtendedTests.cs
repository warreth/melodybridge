using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Playlists;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

[TestFixture]
public class M3uGeneratorExtendedTests
{
    private MelodyBridgeDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"M3uExt_{Guid.NewGuid()}")
            .Options;
        var db = new MelodyBridgeDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private async Task SeedTrackAsync(MelodyBridgeDbContext db, string melodyId, string path)
    {
        db.Tracks.Add(new TrackEntity
        {
            MelodyId = melodyId,
            CurrentPath = path,
            Title = "Test",
            Artist = "Artist",
        });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task GenerateM3uAsync_PathRemap_TranslatesCorrectly()
    {
        using var db = CreateDbContext();
        await SeedTrackAsync(db, "remap-test", "/host/music/song.flac");
        var generator = new M3uGenerator(db, NullLogger<M3uGenerator>.Instance);

        var playlist = new Playlist
        {
            Name = "Remap",
            Tracks = new List<Track>
            {
                new() { SongID = new SongID(Platform.Qobuz, "remap-test") }
            }
        };

        var outputPath = Path.Combine(Path.GetTempPath(), $"m3u_test_{Guid.NewGuid()}.m3u");
        var remap = new Dictionary<string, string>
        {
            { "/host/music", "/container/music" }
        };

        try
        {
            var result = await generator.GenerateM3uAsync(playlist,
                Array.Empty<ScanLocation>(),
                new PlaylistOutputOptions(outputPath, false, remap));

            Assert.That(result, Is.EqualTo(outputPath));

            var content = await File.ReadAllLinesAsync(outputPath);
            Assert.That(content, Has.Some.Matches<string>(x => x.Contains("/container/music/song.flac")));
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Test]
    public async Task GenerateM3uAsync_TrackWithoutMelodyId_Skipped()
    {
        using var db = CreateDbContext();
        var generator = new M3uGenerator(db, NullLogger<M3uGenerator>.Instance);

        var playlist = new Playlist
        {
            Name = "Skipped",
            Tracks = new List<Track>
            {
                new() { SongID = null }, // No melody ID
                new() { SongID = new SongID(Platform.Spotify, "nonexistent-id") }, // Not in DB
            }
        };

        var outputPath = Path.Combine(Path.GetTempPath(), $"m3u_skip_{Guid.NewGuid()}.m3u");
        try
        {
            var result = await generator.GenerateM3uAsync(playlist,
                Array.Empty<ScanLocation>(),
                new PlaylistOutputOptions(outputPath, false, null));

            Assert.That(result, Is.EqualTo(outputPath));
            var content = await File.ReadAllLinesAsync(outputPath);
            // Only header
            Assert.That(content, Has.Length.EqualTo(1));
            Assert.That(content[0], Is.EqualTo("#EXTM3U"));
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }
}
