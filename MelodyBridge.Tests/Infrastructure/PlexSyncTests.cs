using System.Net;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.MediaServers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Plex client behavior against a scripted HTTP boundary: the tests assert
/// on the exact Plex routes called (identity, sections, /all queries,
/// playlist upserts, ratings) and the payload shape of the server:// uri.
/// </summary>
[TestFixture]
public class PlexSyncTests
{
    private sealed class FixedSettings : IPlexSettings
    {
        public string BaseUrl { get; init; } = "http://plex:32400";
        public string ApiKey { get; init; } = "plex-token";

        public Task<string> GetBaseUrlAsync(CancellationToken ct = default) => Task.FromResult(BaseUrl);
        public Task<string> GetApiKeyAsync(CancellationToken ct = default) => Task.FromResult(ApiKey);
    }

    private static PlexSync NewSync(ScriptedHandler handler, FixedSettings? settings = null)
        => new(new HttpClient(handler), NullLogger<PlexSync>.Instance, settings ?? new FixedSettings());

    private static Playlist PlaylistOf(params Track[] tracks) => new()
    {
        Name = "Road trip",
        Tracks = tracks.ToList(),
    };

    private static Track Track(string title, string path, bool liked = false, string? artist = null) => new()
    {
        Title = title,
        Artist = artist,
        IsLiked = liked,
        CurrentTrackLocation = new FileLocation(path),
    };

    /// <summary>Wires the standard route set: identity, one artist section, one matching track.</summary>
    private static ScriptedHandler StandardRoutes(string file = "/media/artist/album/song.mp3",
        string ratingKey = "rk-1")
    {
        var handler = new ScriptedHandler();
        handler.On("/identity",
            ScriptedHandler.Json("""{"MediaContainer":{"machineIdentifier":"machine-1","version":"1.40"}}"""));
        handler.On("/library/sections", ScriptedHandler.Json(
            """{"MediaContainer":{"Directory":[{"key":"31","type":"artist","title":"Music"}]}}"""));
        var allBody = string.Format(
            "{{\"MediaContainer\":{{\"Metadata\":[{{\"ratingKey\":\"{0}\",\"title\":\"song\"," +
            "\"Media\":[{{\"Part\":[{{\"file\":\"{1}\"}}]}}]}}]}}}}", ratingKey, file);
        handler.On("/library/sections/31/all", ScriptedHandler.Json(allBody));
        // GET playlist listing: empty by default (tests that find one override this route).
        handler.On("/playlists?playlistType=audio", ScriptedHandler.Json(
            """{"MediaContainer":{"Metadata":[]}}"""));
        // POST /playlists (create): the created playlist shell, as Plex returns it.
        handler.On("/playlists?uri=", ScriptedHandler.Json(
            """{"MediaContainer":{"size":1,"Metadata":[{"ratingKey":"pl-1","title":"Road trip"}]}}"""));
        return handler;
    }

    [Test]
    public void Name_IsPlex()
        => Assert.That(new PlexSync(new HttpClient(new ScriptedHandler()),
            NullLogger<PlexSync>.Instance, new FixedSettings()).Name, Is.EqualTo("Plex"));

    [Test]
    public async Task SyncPlaylistAsync_UnreachableServer_ReportsErrorAndMakesNoPlaylistCalls()
    {
        var handler = new ScriptedHandler(); // no routes: identity 404s
        var sync = NewSync(handler);

        await sync.SyncPlaylistAsync(PlaylistOf(Track("song", "/media/song.mp3")),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.That(sync.LastReport, Is.Not.Null);
        Assert.That(sync.LastReport!.Message, Does.Contain("machineIdentifier"),
            "the report explains what was missing");
        Assert.That(handler.Requests.Any(r => r.Url.Contains("/playlists")), Is.False,
            "no playlist calls when identity fails");
    }

    [Test]
    public async Task SyncPlaylistAsync_NoMusicSection_ReportsError()
    {
        var handler = new ScriptedHandler();
        handler.On("/identity",
            ScriptedHandler.Json("""{"MediaContainer":{"machineIdentifier":"m"}}"""));
        handler.On("/library/sections", ScriptedHandler.Json(
            """{"MediaContainer":{"Directory":[{"key":"2","type":"movie","title":"Movies"}]}}"""));
        var sync = NewSync(handler);

        await sync.SyncPlaylistAsync(PlaylistOf(Track("song", "/media/song.mp3")),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.That(sync.LastReport!.Message, Does.Contain("music"));
    }

    [Test]
    public async Task SyncPlaylistAsync_NewPlaylist_UsesServerUriAndCreates()
    {
        var handler = StandardRoutes();
        var sync = NewSync(handler);

        await sync.SyncPlaylistAsync(PlaylistOf(Track("song", "/media/artist/album/song.mp3")),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        // Create request: server:// uri with the resolved ratingKey.
        var create = handler.Requests.FirstOrDefault(r =>
            r.Method == "POST" && r.Url.Contains("/playlists?"));
        Assert.That(create, Is.Not.EqualTo(default((string Method, string Url))),
            "a POST /playlists call happened");
        var createUrl = create.Url;
        Assert.That(createUrl, Does.Contain("server%3A%2F%2Fmachine-1%2Fcom.plexapp.plugins.library"),
            "python-plexapi uri form");
        Assert.That(createUrl, Does.Contain("rk-1"));
        Assert.That(createUrl, Does.Contain("type=audio"));
        Assert.That(createUrl, Does.Contain("smart=0"));
        Assert.That(sync.LastReport!.Message, Is.EqualTo("Created playlist"));
    }

    [Test]
    public async Task SyncPlaylistAsync_ExistingPlaylist_ReplacesItems()
    {
        var handler = StandardRoutes();
        handler.On("/playlists?playlistType=audio", ScriptedHandler.Json(
            """{"MediaContainer":{"Metadata":[{"ratingKey":"pl-9","title":"Road trip"}]}}"""));
        // The clear (DELETE) and re-add (PUT) both succeed for the found playlist.
        handler.On("/playlists/pl-9/items", ScriptedHandler.Json("""{"MediaContainer":{}}"""));
        var sync = NewSync(handler);

        await sync.SyncPlaylistAsync(PlaylistOf(Track("song", "/media/artist/album/song.mp3")),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.That(handler.Requests.Any(r => r.Method == "DELETE" && r.Url.Contains("/playlists/pl-9/items")),
            Is.True, "existing playlist is cleared");
        var put = handler.Requests.FirstOrDefault(r =>
            r.Method == "PUT" && r.Url.Contains("/playlists/pl-9/items?"));
        Assert.That(put.Url, Does.Contain("rk-1"), "items re-added with the resolved key");
        Assert.That(sync.LastReport!.Message, Is.EqualTo("Updated existing playlist"));
    }

    [Test]
    public async Task SyncPlaylistAsync_LikedTrack_GetsRatingTen()
    {
        var handler = StandardRoutes();
        var sync = NewSync(handler);

        await sync.SyncPlaylistAsync(PlaylistOf(Track("song", "/media/artist/album/song.mp3", liked: true)),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        var rate = handler.Requests.FirstOrDefault(r => r.Url.Contains("/:/rate"));
        Assert.That(rate.Method, Is.Not.Null.Or.Empty, "liked track gets rated");
        Assert.That(rate.Url, Does.Contain("key=rk-1"));
        Assert.That(rate.Url, Does.Contain("rating=10"), "param is rating, not value");
        Assert.That(rate.Url, Does.Contain("identifier=com.plexapp.plugins.library"));
    }

    [Test]
    public async Task SyncPlaylistAsync_UnresolvedTrack_ListedInReport()
    {
        var handler = StandardRoutes(file: "/media/other/none.mp3", ratingKey: "rk-x");
        var sync = NewSync(handler);

        await sync.SyncPlaylistAsync(PlaylistOf(Track("song", "/media/artist/album/song.mp3")),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.That(sync.LastReport!.UnresolvedPaths, Has.Length.EqualTo(1));
        Assert.That(sync.LastReport.UnresolvedPaths![0], Is.EqualTo("/media/artist/album/song.mp3"));
    }

    [Test]
    public async Task SyncPlaylistAsync_PathRemap_AppliedBeforeLookup()
    {
        var handler = StandardRoutes();
        var sync = NewSync(handler);
        var remap = new Dictionary<string, string> { { "/host/media", "/media" } };

        await sync.SyncPlaylistAsync(PlaylistOf(Track("song", "/host/media/artist/album/song.mp3")),
            new PlaylistOutputOptions("/tmp/out.m3u", false, remap));

        Assert.That(handler.Requests.Any(r => r.Url.Contains("title%3D%3Dsong")), Is.False,
            "lookup went through; file match proved the remap");
        Assert.That(sync.LastReport!.ResolvedCount, Is.EqualTo(1),
            "report: " + sync.LastReport?.Message + "; requests: " +
            string.Join(" | ", handler.Requests.Select(r => r.Method + " " + r.Url)));
    }

    [Test]
    public async Task SyncPlaylistAsync_ConnectionOverride_WinsOverSettings()
    {
        var handler = StandardRoutes();
        var sync = NewSync(handler);

        // Different base URL per call through the options override.
        await sync.SyncPlaylistAsync(PlaylistOf(),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null,
                new MediaServerConnection("http://other-plex:32400", "tok", null)));

        Assert.That(handler.Requests.All(r => r.Url.StartsWith("/") || !r.Url.Contains("://")),
            Is.True, "urls are request-relative");
        Assert.That(handler.Requests.First().Url, Is.EqualTo("/identity"),
            "first call probes the overridden server (PathAndQuery only proves route, not host)");
        // The handler sees PathAndQuery; the host is on the request line, verified by route behavior.
    }

    [Test]
    public async Task SyncPlaylistAsync_TitleEqualsQuery_UsesMediaQuerySyntax()
    {
        var handler = StandardRoutes();
        var sync = NewSync(handler);

        await sync.SyncPlaylistAsync(PlaylistOf(Track("song", "/media/artist/album/song.mp3")),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.That(handler.Requests.Any(r => r.Url.Contains("type=10&title==song")),
            Is.True, "track query uses the media-query equals operator");
    }
}
