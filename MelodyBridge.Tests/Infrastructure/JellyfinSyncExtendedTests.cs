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

    [Test]
    public async Task SyncPlaylistAsync_TitleSearch_PicksTheArtistMatch()
    {
        // Current Jellyfin answers 400 for /Items/ByPath and its search does
        // not match a combined "title artist" term, so the title search is
        // the lookup that resolves real tracks. It also returns covers, so
        // the artist and duration decide which item joins the playlist.
        var handler = new RecordingJellyfinHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://jellyfin:8096") };
        var sync = new JellyfinSync(client,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<JellyfinSync>(), _settings);

        var playlist = new Playlist
        {
            Name = "Real Mix",
            Tracks = new List<Track>
            {
                new()
                {
                    Title = "Real Song",
                    Artist = "The Artist",
                    Duration = TimeSpan.FromSeconds(210),
                    CurrentTrackLocation = new FileLocation("/media/real.mp3"),
                },
            },
        };

        await sync.SyncPlaylistAsync(playlist, new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.That(sync.LastReport!.ResolvedCount, Is.EqualTo(1));
        Assert.That(handler.PlaylistPostBody, Is.Not.Null, "the playlist must be created");
        Assert.That(handler.PlaylistPostBody, Does.Contain("real-id"),
            "the artist match must win over the cover: " + handler.PlaylistPostBody);
        Assert.That(handler.PlaylistPostBody, Does.Not.Contain("cover-id"));
    }

    /// <summary>
    /// Answers like current Jellyfin: ByPath fails, a "title artist" search
    /// matches nothing, the title search returns a cover and the real item.
    /// Records the playlist create payload.
    /// </summary>
    private sealed class RecordingJellyfinHandler : HttpMessageHandler
    {
        public string? PlaylistPostBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (url.Contains("/Items/ByPath"))
                return Json("""{"status":400}""", HttpStatusCode.BadRequest);
            if (url.Contains("IncludeItemTypes=Playlist"))
                return Json("""{"Items":[]}""");
            // The combined term first: it is a prefix of the title term.
            if (url.Contains("SearchTerm=Real+Song+The+Artist"))
                return Json("""{"Items":[]}""");
            if (url.Contains("SearchTerm=Real+Song"))
                return Json("""
                    {"Items":[
                    {"Id":"cover-id","Name":"Real Song","Artists":["Cover Band"],"RunTimeTicks":1000000000},
                    {"Id":"real-id","Name":"Real Song","Artists":["The Artist"],"RunTimeTicks":2100000000}]}
                    """);
            if (request.Method == HttpMethod.Post && url.Contains("/Playlists"))
            {
                PlaylistPostBody = request.Content is null
                    ? null
                    : await request.Content.ReadAsStringAsync(ct);
                return Json("""{"Id":"pl-1","Name":"Real Mix"}""");
            }
            return Json("""{"Items":[]}""");
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
            => new(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
    }
}
