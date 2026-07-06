using System.Net;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.MediaServers;
using Moq;
using Moq.Protected;

namespace MelodyBridge.Tests.Infrastructure;

[TestFixture]
public class JellyfinSyncTests
{
    private Mock<HttpMessageHandler> CreateMockHandler(HttpStatusCode statusCode, string content = "")
    {
        var mock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content),
            });
        return mock;
    }

    [Test]
    public async Task SyncPlaylistAsync_NullPlaylist_Throws()
    {
        var client = new HttpClient(CreateMockHandler(HttpStatusCode.OK).Object);
        var sync = new JellyfinSync(client,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<JellyfinSync>());

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await sync.SyncPlaylistAsync(null!,
                new PlaylistOutputOptions("/tmp/out.m3u", false, null)));
    }

    [Test]
    public async Task SyncPlaylistAsync_PlaylistWithoutName_Throws()
    {
        var client = new HttpClient(CreateMockHandler(HttpStatusCode.OK).Object);
        var sync = new JellyfinSync(client,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<JellyfinSync>());

        var playlist = new Playlist { Name = null, Tracks = new List<Track>() };

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await sync.SyncPlaylistAsync(playlist,
                new PlaylistOutputOptions("/tmp/out.m3u", false, null)));
    }

    [Test]
    public void Name_ReturnsJellyfin()
    {
        var client = new HttpClient(CreateMockHandler(HttpStatusCode.OK).Object);
        var sync = new JellyfinSync(client,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<JellyfinSync>());

        Assert.That(sync.Name, Is.EqualTo("Jellyfin"));
    }
}
