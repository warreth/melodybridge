using MelodyBridge.Core;
using MelodyBridge.Infrastructure.MediaServers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Live Navidrome end-to-end: a real container with the admin password
/// bootstrap env, a scanned music folder, then NavidromeSync resolves the
/// tagged files by title+artist (paths are metadata-built in Navidrome and
/// cannot be matched), creates and re-creates a playlist without
/// duplicates, and stars the liked track. Docker is required; the test
/// ignores itself when the daemon or image is missing.
/// </summary>
[TestFixture]
[Category("Live")]
[Category("ReleaseGate")]
public class NavidromeSyncLiveTests
{
    private NavidromeLiveServer _server = null!;
    private string _music = null!;
    private HttpClient _syncClient = null!;

    private sealed class FixedSettings : INavidromeSettings
    {
        public Task<string> GetBaseUrlAsync(CancellationToken ct = default)
            => Task.FromResult(NavidromeLiveServer.BaseUrl);
        public Task<string> GetUsernameAsync(CancellationToken ct = default)
            => Task.FromResult(NavidromeLiveServer.Username);
        public Task<string> GetPasswordAsync(CancellationToken ct = default)
            => Task.FromResult(NavidromeLiveServer.Password);
    }

    private sealed class BadPasswordSettings : INavidromeSettings
    {
        public Task<string> GetBaseUrlAsync(CancellationToken ct = default)
            => Task.FromResult(NavidromeLiveServer.BaseUrl);
        public Task<string> GetUsernameAsync(CancellationToken ct = default)
            => Task.FromResult(NavidromeLiveServer.Username);
        public Task<string> GetPasswordAsync(CancellationToken ct = default)
            => Task.FromResult("not-the-password");
    }

    [OneTimeSetUp]
    public async Task SetUp()
    {
        Assume.That(LiveFixtureHelpers.DockerImageReady("deluan/navidrome:latest"),
            "navidrome image or docker daemon unavailable");

        _music = Path.Combine(LiveServers.ScratchDir("navidrome"), "music");
        Directory.CreateDirectory(_music);
        LiveFixtureHelpers.WriteTaggedMp3(Path.Combine(_music, "alpha.mp3"), "Alpha Song", "Alice");
        LiveFixtureHelpers.WriteTaggedMp3(Path.Combine(_music, "beta.mp3"), "Beta Song", "Bob");

        _server = await NavidromeLiveServer.StartAsync(_music, expectedTracks: 2);
        _syncClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _server?.Dispose();
        _syncClient?.Dispose();
    }

    // ── the sync under test ──────────────────────────────────────────

    private NavidromeSync NewSync()
        => new(_syncClient, NullLogger<NavidromeSync>.Instance, new FixedSettings());

    private static Playlist PlaylistOf(params (string title, string artist, bool liked)[] tracks) => new()
    {
        Name = "MB Live Mix",
        Tracks = tracks.Select(t => new Track
        {
            Title = t.title,
            Artist = t.artist,
            IsLiked = t.liked,
            CurrentTrackLocation = new FileLocation("/music/" +
                (t.title.Contains("Alpha") ? "alpha.mp3" : "beta.mp3")),
        }).ToList(),
    };

    private static PlaylistOutputOptions Options() => new("/tmp/ignored.m3u", false, null);

    // ── tests ───────────────────────────────────────────────────────

    [Test]
    public async Task Sync_CreatesPlaylist_AndSearchFindsBothTracks()
    {
        var sync = NewSync();
        await sync.SyncPlaylistAsync(
            PlaylistOf(("Alpha Song", "Alice", false), ("Beta Song", "Bob", false)), Options());

        Assert.That(sync.LastReport!.Message, Is.EqualTo("Created playlist"));
        Assert.That(sync.LastReport.ResolvedCount, Is.EqualTo(2),
            "both tagged files resolve via title+artist search3");
        Assert.That(sync.LastReport.UnresolvedPaths, Is.Empty);
        Assert.That(sync.LastReport.PlaylistId, Is.Not.Null);

        var playlists = await _server.GetSubsonicAsync("getPlaylists");
        var names = playlists.GetProperty("playlists").GetProperty("playlist").EnumerateArray()
            .Select(p => p.GetProperty("name").GetString());
        Assert.That(names, Does.Contain("MB Live Mix"));
    }

    [Test]
    public async Task Sync_Twice_UpdatesInsteadOfDuplicating()
    {
        var sync = NewSync();
        await sync.SyncPlaylistAsync(PlaylistOf(("Alpha Song", "Alice", false)), Options());
        await sync.SyncPlaylistAsync(
            PlaylistOf(("Alpha Song", "Alice", false), ("Beta Song", "Bob", false)), Options());

        var playlists = await _server.GetSubsonicAsync("getPlaylists");
        var mixes = playlists.GetProperty("playlists").GetProperty("playlist").EnumerateArray()
            .Where(p => p.GetProperty("name").GetString() == "MB Live Mix").ToList();
        Assert.That(mixes.Count, Is.EqualTo(1),
            "the second sync replaces the track list instead of creating a duplicate");
    }

    [Test]
    public async Task Sync_LikedTrack_BecomesStarred()
    {
        var sync = NewSync();
        await sync.SyncPlaylistAsync(PlaylistOf(("Beta Song", "Bob", true)), Options());

        var starred = await _server.GetSubsonicAsync("getStarred2");
        var names = starred.GetProperty("starred2").GetProperty("song").EnumerateArray()
            .Select(s => s.GetProperty("title").GetString());
        Assert.That(names, Does.Contain("Beta Song"), "the liked track carries a star");
    }

    [Test]
    public async Task Sync_WrongPassword_ReportsErrorNotCrash()
    {
        var sync = new NavidromeSync(_syncClient, NullLogger<NavidromeSync>.Instance,
            new BadPasswordSettings());
        await sync.SyncPlaylistAsync(PlaylistOf(("Alpha Song", "Alice", false)), Options());

        Assert.That(sync.LastReport!.Message, Does.Contain("password").Or.Contain("Wrong"),
            "the subsonic auth failure surfaces in the report: " + sync.LastReport.Message);
    }
}
