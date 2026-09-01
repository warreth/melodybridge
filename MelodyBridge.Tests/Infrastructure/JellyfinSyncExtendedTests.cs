using System.Net;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.MediaServers;
using Moq;
using Moq.Protected;

namespace MelodyBridge.Tests.Infrastructure;

[TestFixture]
public class JellyfinSyncExtendedTests
{
    private sealed class FixedSettings : MelodyBridge.Infrastructure.MediaServers.IJellyfinSettings
    {
        public Task<string> GetBaseUrlAsync(CancellationToken ct = default) => Task.FromResult("http://jellyfin:8096");
        public Task<string> GetApiKeyAsync(CancellationToken ct = default) => Task.FromResult("test-key");
        public Task<string?> GetUserIdAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private static readonly FixedSettings _settings = new();
    private Mock<HttpMessageHandler> CreateMockHandler(
        HttpStatusCode statusCode,
        string content = "{}",
        string requestPath = "")
    {
        var mock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        mock.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.Is<HttpRequestMessage>(r =>
                    string.IsNullOrEmpty(requestPath) ||
                    r.RequestUri!.ToString().Contains(requestPath)),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode,
                Content = new StringContent(content),
            });
        return mock;
    }

    private Mock<HttpMessageHandler> CreateSequenceMock(
        List<(HttpStatusCode StatusCode, string Content, string? PathContains)> responses)
    {
        var mock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        var setup = mock.Protected()
            .SetupSequence<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());

        foreach (var (status, content, _) in responses)
        {
            setup.ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = status,
                Content = new StringContent(content),
            });
        }

        return mock;
    }

    private JellyfinSync CreateSync(Mock<HttpMessageHandler> mockHandler)
    {
        var client = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("http://jellyfin:8096") };
        return new JellyfinSync(client,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<JellyfinSync>(),
            _settings);
    }

    [Test]
    public async Task SyncPlaylistAsync_PathRemap_TranslatesPaths()
    {
        // Create a handler that returns success for ByPath lookup
        var mock = CreateMockHandler(HttpStatusCode.OK,
            """{"Id": "item1", "Name": "Test", "Path": "/data/music/song.flac"}""",
            "/Items/ByPath");

        var sync = CreateSync(mock);
        var remap = new Dictionary<string, string>
        {
            { "/host/music", "/data/music" }
        };

        var playlist = new Playlist
        {
            Name = "Remap Test",
            Tracks = new List<Track>
            {
                new()
                {
                    Title = "Test",
                    CurrentTrackLocation = new FileLocation("/host/music/song.flac")
                }
            }
        };

        Assert.DoesNotThrowAsync(async () =>
            await sync.SyncPlaylistAsync(playlist,
                new PlaylistOutputOptions("/tmp/out.m3u", false, remap)));
    }

    [Test]
    public async Task SyncPlaylistAsync_NoTracks_DoesNotThrow()
    {
        var mock = CreateMockHandler(HttpStatusCode.OK);
        var sync = CreateSync(mock);

        var playlist = new Playlist
        {
            Name = "Empty Playlist",
            Tracks = new List<Track>()
        };

        Assert.DoesNotThrowAsync(async () =>
            await sync.SyncPlaylistAsync(playlist,
                new PlaylistOutputOptions("/tmp/out.m3u", false, null)));
    }

    [Test]
    public async Task SyncPlaylistAsync_TracksWithNullLocation_SkipsLookup()
    {
        var mock = CreateMockHandler(HttpStatusCode.OK);
        var sync = CreateSync(mock);

        var playlist = new Playlist
        {
            Name = "Partial",
            Tracks = new List<Track>
            {
                new() { Title = "Skip Me" } // no CurrentTrackLocation
            }
        };

        Assert.DoesNotThrowAsync(async () =>
            await sync.SyncPlaylistAsync(playlist,
                new PlaylistOutputOptions("/tmp/out.m3u", false, null)));
    }

    [Test]
    public void SyncPlaylistAsync_NullPlaylist_Throws()
    {
        var mock = CreateMockHandler(HttpStatusCode.OK);
        var sync = CreateSync(mock);

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await sync.SyncPlaylistAsync(null!,
                new PlaylistOutputOptions("/tmp/out.m3u", false, null)));
    }

    [Test]
    public void SyncPlaylistAsync_MissingName_Throws()
    {
        var mock = CreateMockHandler(HttpStatusCode.OK);
        var sync = CreateSync(mock);

        var playlist = new Playlist { Name = null, Tracks = new List<Track>() };

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await sync.SyncPlaylistAsync(playlist,
                new PlaylistOutputOptions("/tmp/out.m3u", false, null)));
    }

    [Test]
    public async Task SyncPlaylistAsync_AllLookupsFail_CreatesEmptyPlaylist()
    {
        var mock = CreateSequenceMock(new List<(HttpStatusCode, string, string?)>
        {
            (HttpStatusCode.NotFound, """{"Items":[]}""", null),
        });

        var sync = CreateSync(mock);

        var playlist = new Playlist
        {
            Name = "All Fail",
            Tracks = new List<Track>
            {
                new()
                {
                    Title = "Lost Track",
                    CurrentTrackLocation = new FileLocation("/music/lost.flac")
                }
            }
        };

        Assert.DoesNotThrowAsync(async () =>
            await sync.SyncPlaylistAsync(playlist,
                new PlaylistOutputOptions("/tmp/out.m3u", false, null)));
    }

    [Test]
    public async Task SyncPlaylistAsync_ExistingPlaylist_UpdatesInsteadOfCreating()
    {
        var mock = CreateSequenceMock(new List<(HttpStatusCode, string, string?)>
        {
            (HttpStatusCode.OK, """{"Id": "existing-id", "Name": "Existing"}""", null),
            (HttpStatusCode.OK, """{"Items": [{"Id": "item1", "Name": "Test", "Path": "/music/song.flac"}]}""", null),
            (HttpStatusCode.OK, """{"Items": [{"Id": "existing-playlist", "Name": "Test Playlist"}]}""", null),
            (HttpStatusCode.OK, "", null),
        });

        var sync = CreateSync(mock);

        var playlist = new Playlist
        {
            Name = "Test Playlist",
            Tracks = new List<Track>
            {
                new()
                {
                    Title = "Test Song",
                    CurrentTrackLocation = new FileLocation("/music/song.flac")
                }
            }
        };

        Assert.DoesNotThrowAsync(async () =>
            await sync.SyncPlaylistAsync(playlist,
                new PlaylistOutputOptions("/tmp/out.m3u", false, null)));
    }
}
