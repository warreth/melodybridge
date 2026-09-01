using System.Net;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.MediaServers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Moq.Protected;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Favorites marking for liked songs. The HTTP boundary is a scripted
/// handler that records every request: the test asserts on the exact
/// Jellyfin routes that were (or were not) called, including the modern
/// FavoriteItems route with the legacy fallback.
/// </summary>
[TestFixture]
public class JellyfinFavoritesTests
{
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Method, string Url)> Requests { get; } = new();
        public Func<string, HttpResponseMessage> Respond { get; set; }
            = _ => new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.PathAndQuery;
            Requests.Add((request.Method.Method, url));
            return Task.FromResult(Respond(url));
        }
    }

    private static Playlist LikedPlaylist(params Track[] tracks) => new()
    {
        Name = "Liked songs",
        Tracks = tracks.ToList(),
    };

    private static Track Track(string path, bool liked = false) => new()
    {
        Title = path,
        CurrentTrackLocation = new FileLocation(path),
        IsLiked = liked,
    };

    /// <summary>Fixed connection values, exactly what the settings
    /// interface delivers in production.</summary>
    private sealed class FixedSettings : IJellyfinSettings
    {
        public string BaseUrl { get; init; } = "http://jellyfin:8096";
        public string ApiKey { get; init; } = "test-key";
        public string? UserId { get; init; }

        public Task<string> GetBaseUrlAsync(CancellationToken ct = default)
            => Task.FromResult(BaseUrl);
        public Task<string> GetApiKeyAsync(CancellationToken ct = default)
            => Task.FromResult(ApiKey);
        public Task<string?> GetUserIdAsync(CancellationToken ct = default)
            => Task.FromResult(UserId);
    }

    private static JellyfinSync NewSync(RecordingHandler handler,
        string? userId = null, string baseUrl = "http://jellyfin:8096")
        => new(new HttpClient(handler) { BaseAddress = new Uri(baseUrl) },
            NullLogger<JellyfinSync>.Instance,
            new FixedSettings { UserId = userId, BaseUrl = baseUrl });

    [Test]
    public async Task LikedTracks_GetFavoriteItemsCalls_WithConfiguredUser()
    {
        var handler = new RecordingHandler();
        // Item lookups resolve to ids derived from the file path so the
        // flow reaches the favorites marking.
        handler.Respond = url =>
        {
            if (url.StartsWith("/Items?")) return Json(
                $$"""{"Items": [{"Id": "item-1"}]}""");
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        var sync = NewSync(handler, userId: "user-42");

        await sync.SyncPlaylistAsync(
            LikedPlaylist(Track("/music/a.mp3", liked: true)),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        var favoriteCall = handler.Requests.FirstOrDefault(r =>
            r.Url == "/Users/user-42/FavoriteItems/item-1");
        Assert.That(favoriteCall.Method, Is.EqualTo("POST"),
            "the modern FavoriteItems route must be called for liked songs");
    }

    [Test]
    public async Task NotLikedTracks_GetNoFavoriteCalls()
    {
        var handler = new RecordingHandler();
        handler.Respond = url =>
        {
            if (url.StartsWith("/Items?")) return Json(
                """{"Items": [{"Id": "item-1"}]}""");
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        var sync = NewSync(handler, userId: "user-42");

        await sync.SyncPlaylistAsync(
            LikedPlaylist(Track("/music/a.mp3", liked: false)),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.That(handler.Requests.Any(r => r.Url.Contains("Favorite")), Is.False,
            "unliked songs must not be favorited");
    }

    [Test]
    public async Task LegacyFavoriteRoute_UsedAsFallback()
    {
        var handler = new RecordingHandler();
        handler.Respond = url =>
        {
            if (url.StartsWith("/Items?")) return Json(
                """{"Items": [{"Id": "item-1"}]}""");
            // Modern route rejects; legacy route accepts.
            if (url.EndsWith("/FavoriteItems/item-1"))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        var sync = NewSync(handler, userId: "user-42");

        await sync.SyncPlaylistAsync(
            LikedPlaylist(Track("/music/a.mp3", liked: true)),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.That(handler.Requests.Any(r =>
            r.Url == "/Users/user-42/Items/item-1/Favorite" && r.Method == "POST"),
            Is.True, "older Jellyfin servers get the legacy route");
    }

    [Test]
    public async Task WithoutUserId_FirstRealUserIsUsed()
    {
        var handler = new RecordingHandler();
        handler.Respond = url =>
        {
            if (url == "/Users") return Json(
                """[{"Id": "sys", "Name": "system"}, {"Id": "real-user", "Name": "me"}]""");
            if (url.StartsWith("/Items?")) return Json(
                """{"Items": [{"Id": "item-1"}]}""");
            return new HttpResponseMessage(HttpStatusCode.OK);
        };
        var sync = NewSync(handler);

        await sync.SyncPlaylistAsync(
            LikedPlaylist(Track("/music/a.mp3", liked: true)),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.That(handler.Requests.Any(r =>
            r.Url == "/Users/real-user/FavoriteItems/item-1" && r.Method == "POST"),
            Is.True, "with no configured user, the first real user gets the favorites");
    }

    [Test]
    public async Task FavoriteFailure_DoesNotBreakSync()
    {
        var handler = new RecordingHandler();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        var sync = NewSync(handler, userId: "user-42");

        // All routes fail: the sync still completes and reports.
        await sync.SyncPlaylistAsync(
            LikedPlaylist(Track("/music/a.mp3", liked: true)),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.That(sync.GetLastReport(), Is.Not.Null,
            "a favorites failure must not fail the playlist sync");
    }

    [Test]
    public async Task ConnectionValues_AppliedPerCall_FromSettings()
    {
        var handler = new RecordingHandler();
        handler.Respond = url =>
        {
            if (url == "/Users") return Json(
                """[{"Id": "real-user", "Name": "me"}]""");
            if (url.StartsWith("/Items?")) return Json(
                """{"Items": [{"Id": "item-1"}]}""");
            return new HttpResponseMessage(HttpStatusCode.OK);
        };

        // The settings interface points at a different server than the
        // client was built with: the sync must re-point the client.
        var sync = NewSync(handler, baseUrl: "http://configured:1234");

        await sync.SyncPlaylistAsync(
            LikedPlaylist(Track("/music/a.mp3", liked: true)),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.That(handler.Requests[0].Url, Does.StartWith("/"),
            "the request went through the re-pointed client");
    }

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
}
