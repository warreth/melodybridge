using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.MediaServers;
using MelodyBridge.Infrastructure.Playlists;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace MelodyBridge.Tests.Services;

[TestFixture]
public class SyncEngineExtendedTests
{
    private MelodyBridgeDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"SyncEngineExt_{Guid.NewGuid()}")
            .Options;
        var db = new MelodyBridgeDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private SyncEngine CreateSyncEngine(
        MelodyBridgeDbContext db,
        IEnumerable<IMediaServerSync>? servers = null)
    {
        var m3u = new M3uGenerator(NullLogger<M3uGenerator>.Instance);
        servers ??= Array.Empty<IMediaServerSync>();

        return new SyncEngine(db, m3u, servers, NullLogger<SyncEngine>.Instance);
    }

    [Test]
    public void SyncToServerAsync_UnknownServer_Throws()
    {
        using var db = CreateDbContext();
        var engine = CreateSyncEngine(db);

        var playlist = new Playlist { Name = "Test", Tracks = new List<Track>() };

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.SyncToServerAsync(playlist,
                new PlaylistOutputOptions("/tmp/out.m3u", false, null),
                "NonExistentServer"));
    }

    [Test]
    public void SyncToServerWithReportAsync_UnknownServer_Throws()
    {
        using var db = CreateDbContext();
        var engine = CreateSyncEngine(db);

        var playlist = new Playlist { Name = "Test", Tracks = new List<Track>() };

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await engine.SyncToServerWithReportAsync(playlist,
                new PlaylistOutputOptions("/tmp/out.m3u", false, null),
                "NonExistentServer"));
    }

    [Test]
    public async Task SyncToServerAsync_WithRegisteredServer_DoesNotThrow()
    {
        using var db = CreateDbContext();

        var mockServer = new Mock<IMediaServerSync>();
        mockServer.Setup(s => s.Name).Returns("MockServer");

        var engine = CreateSyncEngine(db, new[] { mockServer.Object });

        var playlist = new Playlist { Name = "Test", Tracks = new List<Track>() };

        Assert.DoesNotThrowAsync(async () =>
            await engine.SyncToServerAsync(playlist,
                new PlaylistOutputOptions("/tmp/out.m3u", false, null),
                "MockServer"));
    }

    [Test]
    public async Task SyncToServerWithReportAsync_NonJellyfinServer_ReturnsNull()
    {
        using var db = CreateDbContext();

        var mockServer = new Mock<IMediaServerSync>();
        mockServer.Setup(s => s.Name).Returns("GenericServer");

        var engine = CreateSyncEngine(db, new[] { mockServer.Object });

        var playlist = new Playlist { Name = "Test", Tracks = new List<Track>() };
        var result = await engine.SyncToServerWithReportAsync(playlist,
            new PlaylistOutputOptions("/tmp/out.m3u", false, null),
            "GenericServer");

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task SyncToServerAsync_NullPlaylist_Throws()
    {
        using var db = CreateDbContext();

        var mockServer = new Mock<IMediaServerSync>(MockBehavior.Strict);
        mockServer.Setup(s => s.Name).Returns("MockServer");
        mockServer.Setup(s => s.SyncPlaylistAsync(null!, It.IsAny<PlaylistOutputOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException("Playlist cannot be null"));

        var engine = CreateSyncEngine(db, new[] { mockServer.Object });

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await engine.SyncToServerAsync(null!,
                new PlaylistOutputOptions("/tmp/out.m3u", false, null),
                "MockServer"));
    }
}
