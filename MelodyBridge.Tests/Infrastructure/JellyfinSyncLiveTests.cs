using System.Net.Http.Json;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.MediaServers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Live Jellyfin end-to-end: a real container is bootstrapped headless by
/// <see cref="JellyfinLiveServer"/> (startup wizard, admin user, music
/// library, scan), then JellyfinSync resolves the scanned files by path,
/// creates a playlist, updates it and marks a favorite: all verified
/// through the server's own API. Docker is required; the test ignores
/// itself when the daemon or image is missing.
/// </summary>
[TestFixture]
[Category("Live")]
[Category("ReleaseGate")]
public class JellyfinSyncLiveTests
{
    private JellyfinLiveServer _server = null!;
    private string _music = null!;
    private HttpClient _syncClient = null!;

    private sealed class FixedSettings : IJellyfinSettings
    {
        public string ApiKey { get; init; } = "";
        public string? UserId { get; init; }

        public Task<string> GetBaseUrlAsync(CancellationToken ct = default)
            => Task.FromResult(JellyfinLiveServer.BaseUrl);
        public Task<string> GetApiKeyAsync(CancellationToken ct = default) => Task.FromResult(ApiKey);
        public Task<string?> GetUserIdAsync(CancellationToken ct = default) => Task.FromResult(UserId);
    }

    [OneTimeSetUp]
    public async Task SetUp()
    {
        Assume.That(LiveFixtureHelpers.DockerImageReady("jellyfin/jellyfin:latest"),
            "jellyfin image or docker daemon unavailable");

        _music = Path.Combine(LiveServers.ScratchDir("jellyfin"), "music");
        Directory.CreateDirectory(_music);
        LiveFixtureHelpers.WriteTaggedMp3(Path.Combine(_music, "alpha.mp3"), "Alpha Song", "Alice");
        LiveFixtureHelpers.WriteTaggedMp3(Path.Combine(_music, "beta.mp3"), "Beta Song", "Bob");

        _server = await JellyfinLiveServer.StartAsync(_music, expectedTracks: 2);
        await JellyfinLiveServer.WaitUntilResolvableAsync(_server.Api, "Alpha Song");
        await JellyfinLiveServer.WaitUntilResolvableAsync(_server.Api, "Beta Song");

        _syncClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        _server?.Dispose();
        _syncClient?.Dispose();
    }

    // ── the sync under test ──────────────────────────────────────────

    private JellyfinSync NewSync(string token)
        => new(_syncClient, NullLogger<JellyfinSync>.Instance,
            new FixedSettings { ApiKey = token });

    private static Playlist PlaylistOf((string title, bool liked)[] tracks) => new()
    {
        Name = "MB Live Mix",
        Tracks = tracks.Select(t => new Track
        {
            Title = t.title,
            IsLiked = t.liked,
            CurrentTrackLocation = new FileLocation("/media/" +
                (t.title.Contains("Alpha") ? "alpha.mp3" : "beta.mp3")),
        }).ToList(),
    };

    // The job's path remap: host-side test dir -> container /media.
    private PlaylistOutputOptions HostOptions()
        => new("/tmp/ignored.m3u", false,
            new Dictionary<string, string> { { _music, "/media" } });

    // ── tests ───────────────────────────────────────────────────────

    [Test]
    public async Task Sync_CreatesPlaylist_WithBothTracks()
    {
        var sync = NewSync(_server.Token);
        var playlist = PlaylistOf(new[] { ("Alpha Song", false), ("Beta Song", false) });

        await sync.SyncPlaylistAsync(playlist, HostOptions());

        Assert.That(sync.LastReport!.Message, Is.EqualTo("Created playlist"),
            "report: " + sync.LastReport.Message);
        Assert.That(sync.LastReport.ResolvedCount, Is.EqualTo(2));
        Assert.That(sync.LastReport.UnresolvedPaths, Is.Empty);

        var mixes = await _server.PlaylistsNamedAsync("MB Live Mix");
        Assert.That(mixes, Has.Count.EqualTo(1), "playlist exists");
        var names = await _server.PlaylistItemNamesAsync(mixes[0].GetProperty("Id").GetString()!);
        Assert.That(names, Is.EquivalentTo(new[] { "Alpha Song", "Beta Song" }),
            "the created playlist must carry the resolved items, not an empty shell");
    }

    [Test]
    public async Task Sync_Twice_UpdatesInsteadOfDuplicating()
    {
        var sync = NewSync(_server.Token);
        var playlist = PlaylistOf(new[] { ("Alpha Song", false) });
        await sync.SyncPlaylistAsync(playlist, HostOptions());
        var first = (await _server.PlaylistsNamedAsync("MB Live Mix"))
            .Select(p => p.GetProperty("Id").GetString()!).ToList();
        Assert.That(first, Has.Count.EqualTo(1), "the first sync creates exactly one playlist");

        await sync.SyncPlaylistAsync(PlaylistOf(new[] { ("Alpha Song", false), ("Beta Song", false) }),
            HostOptions());
        var second = (await _server.PlaylistsNamedAsync("MB Live Mix"))
            .Select(p => p.GetProperty("Id").GetString()!).ToList();

        Assert.That(second, Is.EquivalentTo(first),
            "the second sync updates the same playlist, never a second one");
        var names = await _server.PlaylistItemNamesAsync(second.Single());
        Assert.That(names, Is.EquivalentTo(new[] { "Alpha Song", "Beta Song" }),
            "the update must replace the member list");
    }

    [Test]
    public async Task Sync_LikedTrack_BecomesFavorite()
    {
        var sync = NewSync(_server.Token);
        await sync.SyncPlaylistAsync(PlaylistOf(new[] { ("Beta Song", true) }), HostOptions());

        var userId = (await _server.Api.GetFromJsonAsync<JsonElement>(JellyfinLiveServer.BaseUrl + "/Users"))
            .EnumerateArray().First().GetProperty("Id").GetString();
        var items = await _server.Api.GetFromJsonAsync<JsonElement>(
            $"{JellyfinLiveServer.BaseUrl}/Users/{userId}/Items?Filters=IsFavorite&Recursive=true&IncludeItemTypes=Audio");
        var names = string.Join(",", items.GetProperty("Items").EnumerateArray()
            .Select(i => i.GetProperty("Name").GetString()));
        Assert.That(names, Does.Contain("Beta Song"), "the liked track is a favorite");
    }

    [Test]
    public async Task Sync_BadToken_ReportsErrorNotCrash()
    {
        var sync = NewSync("not-a-real-token");
        await sync.SyncPlaylistAsync(PlaylistOf(new[] { ("Alpha Song", false) }), HostOptions());

        Assert.That(sync.LastReport!.ResolvedCount, Is.EqualTo(0),
            "with a bad token nothing resolves; the report explains why");
    }
}
