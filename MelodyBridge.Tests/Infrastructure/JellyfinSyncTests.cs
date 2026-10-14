using System.Net;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.MediaServers;
using Moq;
using Moq.Protected;

namespace MelodyBridge.Tests.Infrastructure;

[TestFixture]
public class JellyfinSyncTests
{
    private sealed class FixedSettings : MelodyBridge.Infrastructure.MediaServers.IJellyfinSettings
    {
        public Task<string> GetBaseUrlAsync(CancellationToken ct = default) => Task.FromResult("http://jellyfin:8096");
        public Task<string> GetApiKeyAsync(CancellationToken ct = default) => Task.FromResult("test-key");
        public Task<string?> GetUserIdAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private static readonly FixedSettings _settings = new();
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
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<JellyfinSync>(),
            _settings);

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await sync.SyncPlaylistAsync(null!,
                new PlaylistOutputOptions("/tmp/out.m3u", false, null)));
    }

    [Test]
    public async Task SyncPlaylistAsync_PlaylistWithoutName_Throws()
    {
        var client = new HttpClient(CreateMockHandler(HttpStatusCode.OK).Object);
        var sync = new JellyfinSync(client,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<JellyfinSync>(),
            _settings);

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
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<JellyfinSync>(),
            _settings);

        Assert.That(sync.Name, Is.EqualTo("Jellyfin"));
    }

    [Test]
    public async Task SyncPlaylistAsync_SendsTheModernAuthorizationHeader()
    {
        // Current Jellyfin rejects the legacy X-Emby-Token header with 401;
        // the supported form is Authorization: MediaBrowser Token="...".
        var authHeaders = new List<string?>();
        var mock = new Mock<HttpMessageHandler>(MockBehavior.Loose);
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .Callback<HttpRequestMessage, CancellationToken>((request, _) =>
                authHeaders.Add(request.Headers.TryGetValues("Authorization", out var values)
                    ? values.FirstOrDefault()
                    : null))
            .ReturnsAsync(() => new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("{}"),
            });

        var client = new HttpClient(mock.Object);
        var sync = new JellyfinSync(client,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<JellyfinSync>(),
            _settings);

        var playlist = new Playlist
        {
            Name = "Mix",
            Tracks = new List<Track>
            {
                new()
                {
                    Title = "Song",
                    Artist = "Artist",
                    CurrentTrackLocation = new FileLocation("/music/song.mp3"),
                },
            },
        };
        await sync.SyncPlaylistAsync(playlist, new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.That(authHeaders, Is.Not.Empty, "the sync must have sent at least one request");
        Assert.That(authHeaders, Has.All.EqualTo("MediaBrowser Token=\"test-key\""),
            "every request carries the modern Authorization header");
    }
}
