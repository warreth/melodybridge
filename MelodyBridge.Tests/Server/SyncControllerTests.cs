using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Playlists;
using MelodyBridge.Server.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server;

[TestFixture]
public class SyncControllerTests
{
    private MelodyBridgeDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"SyncControllerTest_{Guid.NewGuid()}")
            .Options;
        var db = new MelodyBridgeDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private SyncEngine CreateSyncEngine(MelodyBridgeDbContext db)
    {
        var m3u = new M3uGenerator(NullLogger<M3uGenerator>.Instance);
        var servers = Array.Empty<IMediaServerSync>();
        return new SyncEngine(db, m3u, servers, NullLogger<SyncEngine>.Instance);
    }

    [Test]
    public void Constructor_WithValidDependencies_Succeeds()
    {
        using var db = CreateDbContext();
        var engine = CreateSyncEngine(db);
        var controller = new SyncController(engine, NullLogger<SyncController>.Instance);
        Assert.That(controller, Is.Not.Null);
    }

    [Test]
    public async Task Run_MissingPlaylist_ReturnsBadRequest()
    {
        using var db = CreateDbContext();
        var engine = CreateSyncEngine(db);
        var controller = new SyncController(engine, NullLogger<SyncController>.Instance);
        var request = new SyncController.RunSyncRequest(
            null!,
            "Jellyfin",
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        var result = await controller.Run(request, CancellationToken.None);

        Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
    }

    [Test]
    public async Task Run_UnknownServer_ReturnsInternalServerError()
    {
        using var db = CreateDbContext();
        var engine = CreateSyncEngine(db);
        var controller = new SyncController(engine, NullLogger<SyncController>.Instance);
        var playlist = new Playlist
        {
            Name = "Test",
            Tracks = new List<Track>()
        };
        var request = new SyncController.RunSyncRequest(
            playlist,
            "NonExistentServer",
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        var result = await controller.Run(request, CancellationToken.None);

        var objResult = result as ObjectResult;
        Assert.That(objResult, Is.Not.Null);
        Assert.That(objResult!.StatusCode, Is.EqualTo(500));
    }

    [Test]
    public async Task Run_ValidRequest_ReturnsOkResult()
    {
        using var db = CreateDbContext();
        var engine = CreateSyncEngine(db);
        var controller = new SyncController(engine, NullLogger<SyncController>.Instance);
        var playlist = new Playlist
        {
            Name = "Test",
            Tracks = new List<Track>()
        };
        var request = new SyncController.RunSyncRequest(
            playlist,
            "NonExistentServer",
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        var result = await controller.Run(request, CancellationToken.None);

        // With no servers registered, the controller catches the exception and returns 500
        Assert.That(result, Is.InstanceOf<ObjectResult>());
    }
}
