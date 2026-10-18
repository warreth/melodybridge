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

    private JellyfinSync CreateSync(Mock<HttpMessageHandler> mockHandler)
    {
        var client = new HttpClient(mockHandler.Object) { BaseAddress = new Uri("http://jellyfin:8096") };
        return new JellyfinSync(client,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<JellyfinSync>(),
            _settings);
    }

    private JellyfinSync NewSync(ScriptedHandler handler)
    {
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://jellyfin:8096") };
        return new JellyfinSync(client,
            new Microsoft.Extensions.Logging.Abstractions.NullLogger<JellyfinSync>(),
            _settings);
    }

    [Test]
    public async Task SyncPlaylistAsync_PathRemap_TranslatesPaths()
    {
        // The server holds the file under the remapped path: the library
        // walk resolves it there, no per-track lookup needed.
        var handler = new ScriptedHandler()
            .On("IncludeItemTypes=Audio", """{"Items":[{"Id":"item1","Name":"Test","Path":"/data/music/song.flac"}]}""")
            .On("/Playlists", """{"Id":"pl-1","Name":"Remap Test"}""");

        var sync = NewSync(handler);
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

        await sync.SyncPlaylistAsync(playlist,
            new PlaylistOutputOptions("/tmp/out.m3u", false, remap));

        Assert.Multiple(() =>
        {
            Assert.That(sync.LastReport!.ResolvedCount, Is.EqualTo(1),
                "the remapped path must resolve");
            Assert.That(handler.Requests.Any(r => r.Url.Contains("SearchTerm")), Is.False,
                "a resolved path needs no search");
        });
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
        // A server that knows nothing: every route answers 404. The sync
        // still completes, reports the failure honestly, and never throws.
        var handler = new ScriptedHandler(); // no routes: everything is a 404
        var sync = NewSync(handler);

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

        await sync.SyncPlaylistAsync(playlist,
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.Multiple(() =>
        {
            Assert.That(sync.LastReport, Is.Not.Null);
            Assert.That(sync.LastReport!.Succeeded, Is.False,
                "nothing was created, the report says so");
            Assert.That(sync.LastReport.UnresolvedPaths, Is.Not.Empty);
        });
    }

    [Test]
    public async Task SyncPlaylistAsync_ExistingPlaylist_UpdatesInsteadOfCreating()
    {
        var handler = new ScriptedHandler()
            .On("IncludeItemTypes=Audio", """{"Items":[{"Id":"item1","Path":"/music/song.flac"}]}""")
            .On("IncludeItemTypes=Playlist", """{"Items":[{"Id":"existing-playlist","Name":"Test Playlist"}]}""")
            .On("/Playlists/existing-playlist", """{"ItemIds":[]}""");

        var sync = NewSync(handler);

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

        await sync.SyncPlaylistAsync(playlist,
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.Multiple(() =>
        {
            Assert.That(handler.Recorded.Any(r => r.Method == "POST" && r.Url == "/Playlists"), Is.False,
                "an existing playlist is never duplicated");
            var update = handler.Recorded.Single(r => r.Method == "POST");
            Assert.That(update.Url, Does.Contain("/Playlists/existing-playlist"));
            Assert.That(update.Body, Does.Contain("item1"));
        });
    }

    [Test]
    public async Task SyncPlaylistAsync_UnreadableLibrary_FallsBackToByPathAndSearch()
    {
        // An older server (or a library the account cannot read): the walk
        // fails, so the exact-path route gets its chance again.
        var handler = new ScriptedHandler()
            .On("IncludeItemTypes=Audio", """{"status":400}""", HttpStatusCode.BadRequest)
            .On("/Items/ByPath", """{"Id":"item1","Name":"Test"}""")
            .On("IncludeItemTypes=Playlist", """{"Items":[]}""")
            .On("/Playlists", """{"Id":"pl-1","Name":"Legacy"}""");

        var sync = NewSync(handler);
        var playlist = new Playlist
        {
            Name = "Legacy",
            Tracks = new List<Track>
            {
                new() { Title = "Test", CurrentTrackLocation = new FileLocation("/music/song.flac") }
            }
        };

        await sync.SyncPlaylistAsync(playlist,
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.Multiple(() =>
        {
            Assert.That(sync.LastReport!.ResolvedCount, Is.EqualTo(1));
            Assert.That(handler.Requests.Any(r => r.Url.Contains("/Items/ByPath")), Is.True,
                "without a library index the exact-path route is the first try");
        });
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
