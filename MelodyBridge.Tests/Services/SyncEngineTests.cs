using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Playlists;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

[TestFixture]
public class SyncEngineTests
{
    private MelodyBridgeDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"SyncTest_{Guid.NewGuid()}")
            .Options;
        var db = new MelodyBridgeDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    [Test]
    public async Task GenerateM3uForPlaylistAsync_CreatesM3uFile()
    {
        using var db = CreateDbContext();
        var m3u = new M3uGenerator(NullLogger<M3uGenerator>.Instance);
        var engine = new SyncEngine(db, m3u, Array.Empty<IMediaServerSync>(),
            NullLogger<SyncEngine>.Instance);

        var playlist = new Playlist
        {
            Name = "Sync Test",
            Tracks = new List<Track>()
        };

        var outputPath = Path.GetTempFileName() + ".m3u";
        try
        {
            var result = await engine.GenerateM3uForPlaylistAsync(playlist,
                Array.Empty<ScanLocation>(),
                new PlaylistOutputOptions(outputPath, false, null));

            Assert.That(result, Is.EqualTo(outputPath));
            Assert.That(File.Exists(outputPath), Is.True);
        }
        finally
        {
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }
    }

    [Test]
    public void SyncToServerAsync_UnknownServer_Throws()
    {
        using var db = CreateDbContext();
        var m3u = new M3uGenerator(NullLogger<M3uGenerator>.Instance);
        var engine = new SyncEngine(db, m3u, Array.Empty<IMediaServerSync>(),
            NullLogger<SyncEngine>.Instance);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.SyncToServerAsync(new Playlist(), new PlaylistOutputOptions("/tmp/out.m3u", false, null), "UnknownServer"));

        Assert.That(ex!.Message, Does.Contain("not found"));
    }

    [Test]
    public void SyncToServerWithReportAsync_UnknownServer_Throws()
    {
        using var db = CreateDbContext();
        var m3u = new M3uGenerator(NullLogger<M3uGenerator>.Instance);
        var engine = new SyncEngine(db, m3u, Array.Empty<IMediaServerSync>(),
            NullLogger<SyncEngine>.Instance);

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.SyncToServerWithReportAsync(
                new Playlist(),
                new PlaylistOutputOptions("/tmp/out.m3u", false, null),
                "MissingServer"));
    }
}
