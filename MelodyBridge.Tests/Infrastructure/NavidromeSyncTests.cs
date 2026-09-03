using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.MediaServers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Navidrome client behavior against a scripted HTTP boundary: the tests
/// assert on the exact Subsonic routes called (ping, search3, createPlaylist,
/// star), the salted-token auth parameters, and the upsert-vs-duplicate
/// playlist logic. Subsonic-level errors come back HTTP 200 with
/// status=failed: one test proves they surface as report errors.
/// </summary>
[TestFixture]
public class NavidromeSyncTests
{
    private sealed class FixedSettings : INavidromeSettings
    {
        public string BaseUrl { get; init; } = "http://navidrome:4533";
        public string Username { get; init; } = "admin";
        public string Password { get; init; } = "secret";

        public Task<string> GetBaseUrlAsync(CancellationToken ct = default) => Task.FromResult(BaseUrl);
        public Task<string> GetUsernameAsync(CancellationToken ct = default) => Task.FromResult(Username);
        public Task<string> GetPasswordAsync(CancellationToken ct = default) => Task.FromResult(Password);
    }

    private static NavidromeSync NewSync(ScriptedHandler handler, FixedSettings? settings = null)
        => new(new HttpClient(handler), NullLogger<NavidromeSync>.Instance,
            settings ?? new FixedSettings());

    private static Playlist PlaylistOf(params Track[] tracks) => new()
    {
        Name = "Evening mix",
        Tracks = tracks.ToList(),
    };

    private static Track Track(string title, string path, bool liked = false, string? artist = null) => new()
    {
        Title = title,
        Artist = artist,
        IsLiked = liked,
        CurrentTrackLocation = new FileLocation(path),
    };

    /// <summary>Wires search3 with one matching song (title+artist) and one decoy.</summary>
    private static ScriptedHandler StandardRoutes()
    {
        var handler = new ScriptedHandler();
        handler.On("/rest/search3", ScriptedHandler.Json(
            """{"subsonic-response":{"status":"ok","searchResult3":{"song":[""" +
            """{"id":"sg-decoy","title":"song","artist":"Someone Else"},""" +
            """{"id":"sg-1","title":"song","artist":"Artist"}]}}}"""));
        handler.On("/rest/getPlaylists", ScriptedHandler.Json(
            """{"subsonic-response":{"status":"ok","playlists":{"playlist":[]}}}"""));
        handler.On("/rest/createPlaylist", ScriptedHandler.Json(
            """{"subsonic-response":{"status":"ok","playlist":{"id":"pl-7","name":"Evening mix"}}}"""));
        return handler;
    }

    private static Track MatchedTrack(bool liked = false)
        => Track("song", "/media/artist/album/song.mp3", liked, artist: "Artist");

    [Test]
    public void Name_IsNavidrome()
        => Assert.That(new NavidromeSync(new HttpClient(new ScriptedHandler()),
            NullLogger<NavidromeSync>.Instance, new FixedSettings()).Name, Is.EqualTo("Navidrome"));

    [Test]
    public async Task SyncPlaylistAsync_Requests_CarrySaltedTokenAuth()
    {
        var handler = StandardRoutes();
        var sync = NewSync(handler);

        await sync.SyncPlaylistAsync(PlaylistOf(MatchedTrack()),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        var search = handler.Requests.First(r => r.Url.Contains("/rest/search3"));
        Assert.That(search.Url, Does.Contain("u=admin"));
        Assert.That(search.Url, Does.Contain("&t="));
        Assert.That(search.Url, Does.Contain("&s="));
        Assert.That(search.Url, Does.Contain("&v=1.16.1"));
        Assert.That(search.Url, Does.Contain("&f=json"));
        Assert.That(search.Url, Does.Not.Contain("secret"),
            "the password never travels in the clear");
    }

    [Test]
    public async Task SyncPlaylistAsync_MatchesByTitleAndArtist_SkipsDecoy()
    {
        var handler = StandardRoutes();
        var sync = NewSync(handler);

        await sync.SyncPlaylistAsync(PlaylistOf(MatchedTrack()),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        var create = handler.Requests.First(r => r.Url.Contains("/rest/createPlaylist"));
        Assert.That(create.Url, Does.Contain("songId=sg-1"), "the right song id");
        Assert.That(create.Url, Does.Not.Contains("sg-decoy"));
    }

    [Test]
    public async Task SyncPlaylistAsync_NewPlaylist_CreatesWithNameAndSongIds()
    {
        var handler = StandardRoutes();
        var sync = NewSync(handler);

        await sync.SyncPlaylistAsync(PlaylistOf(MatchedTrack(), MatchedTrack()),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        var create = handler.Requests.First(r => r.Url.Contains("/rest/createPlaylist"));
        Assert.That(create.Url, Does.Contain("name=Evening%20mix"));
        Assert.That(Regex.Matches(create.Url, "songId=sg-1").Count, Is.EqualTo(2),
            "repeated songId parameters, one per track");
        Assert.That(sync.LastReport!.Message, Is.EqualTo("Created playlist"));
        Assert.That(sync.LastReport.PlaylistId, Is.EqualTo("pl-7"));
    }

    [Test]
    public async Task SyncPlaylistAsync_ExistingPlaylist_UpdatesInsteadOfDuplicating()
    {
        var handler = StandardRoutes();
        handler.On("/rest/getPlaylists", ScriptedHandler.Json(
            """{"subsonic-response":{"status":"ok","playlists":{"playlist":[{"id":"pl-2","name":"Evening mix"}]}}}"""));
        var sync = NewSync(handler);

        await sync.SyncPlaylistAsync(PlaylistOf(MatchedTrack()),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        var create = handler.Requests.First(r => r.Url.Contains("/rest/createPlaylist"));
        Assert.That(create.Url, Does.Contain("playlistId=pl-2"),
            "createPlaylist with playlistId replaces the track list");
        Assert.That(create.Url, Does.Contain("songId=sg-1"));
        Assert.That(sync.LastReport!.Message, Is.EqualTo("Updated existing playlist"));
    }

    [Test]
    public async Task SyncPlaylistAsync_LikedTrack_GetsStarred()
    {
        var handler = StandardRoutes();
        handler.On("/rest/star", ScriptedHandler.Json(
            """{"subsonic-response":{"status":"ok"}}"""));
        var sync = NewSync(handler);

        await sync.SyncPlaylistAsync(PlaylistOf(MatchedTrack(liked: true)),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        var star = handler.Requests.FirstOrDefault(r => r.Url.Contains("/rest/star"));
        Assert.That(star.Method, Is.Not.Null.Or.Empty, "a star call happened");
        Assert.That(star.Url, Does.Contain("id=sg-1"));
    }

    [Test]
    public async Task SyncPlaylistAsync_NoTitle_SkipsLookupAndReportsUnresolved()
    {
        var handler = StandardRoutes();
        var sync = NewSync(handler);

        // Path-only track: Navidrome cannot be searched by path (fake paths),
        // so a title-less track is unresolved by design.
        await sync.SyncPlaylistAsync(PlaylistOf(Track(null!, "/media/unknown.mp3")),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.That(sync.LastReport!.ResolvedCount, Is.EqualTo(0));
        Assert.That(sync.LastReport.UnresolvedPaths, Has.Length.EqualTo(1));
    }

    [Test]
    public async Task SyncPlaylistAsync_SubsonicError_SurfacesInReport()
    {
        var handler = new ScriptedHandler();
        handler.On("/rest/getPlaylists", ScriptedHandler.Json(
            """{"subsonic-response":{"status":"failed","error":{"code":40,"message":"Wrong username or password"}}}"""));
        var sync = NewSync(handler);

        await sync.SyncPlaylistAsync(PlaylistOf(MatchedTrack()),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.That(sync.LastReport!.Message, Does.Contain("Wrong username or password"),
            "subsonic-level errors are not swallowed");
    }

    [Test]
    public async Task SyncPlaylistAsync_ConnectionOverride_UsernameFromUserId()
    {
        var handler = StandardRoutes();
        var sync = NewSync(handler);

        await sync.SyncPlaylistAsync(PlaylistOf(MatchedTrack()),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null,
                new MediaServerConnection("http://nd2:4533", "pw2", "alice")));

        var search = handler.Requests.First(r => r.Url.Contains("/rest/search3"));
        Assert.That(search.Url, Does.Contain("u=alice"), "override username wins");
    }

    [Test]
    public async Task SyncPlaylistAsync_TokenIsMd5OfPasswordPlusSalt()
    {
        var handler = StandardRoutes();
        var sync = NewSync(handler);

        await sync.SyncPlaylistAsync(PlaylistOf(MatchedTrack()),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        var search = handler.Requests.First(r => r.Url.Contains("/rest/search3"));
        var salt = ExtractParam(search.Url, "s=");
        var token = ExtractParam(search.Url, "t=");
        var expected = Convert.ToHexString(MD5.HashData(
            Encoding.UTF8.GetBytes("secret" + salt))).ToLowerInvariant();
        Assert.That(token, Is.EqualTo(expected),
            "t must be md5(password + salt) with the same salt sent as s");
    }

    private static string ExtractParam(string url, string prefix)
    {
        var at = url.IndexOf("&" + prefix, StringComparison.Ordinal) + 1;
        var rest = url[(at + prefix.Length)..];
        var end = rest.IndexOf('&');
        return end < 0 ? rest : rest[..end];
    }
}
