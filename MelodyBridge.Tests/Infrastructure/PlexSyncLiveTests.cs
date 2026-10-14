using System.Net.Http.Json;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.MediaServers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Live Plex tests. An unclaimed headless Plex answers /identity without
/// any token: that is the reachability probe both the wizard's Test
/// button and PlexSync use: and token-gated routes accept requests while
/// the server is unclaimed, so the directory's Test step works against a
/// real container. Full playlist sync needs a claimed server with a music
/// library; when PLEX_TOKEN is provided in the environment the deeper
/// sync test runs, otherwise it ignores itself honestly.
/// </summary>
[TestFixture]
[Category("Live")]
[Category("ReleaseGate")]
public class PlexSyncLiveTests
{
    private PlexLiveServer _server = null!;

    [OneTimeSetUp]
    public async Task SetUp()
    {
        Assume.That(LiveFixtureHelpers.DockerImageReady("plexinc/pms-docker:latest"),
            "plex image or docker daemon unavailable");
        _server = await PlexLiveServer.StartAsync();
    }

    [OneTimeTearDown]
    public void TearDown() => _server?.Dispose();

    private sealed class FixedSettings : IPlexSettings
    {
        public Task<string> GetBaseUrlAsync(CancellationToken ct = default)
            => Task.FromResult(PlexLiveServer.BaseUrl);
        public Task<string> GetApiKeyAsync(CancellationToken ct = default)
            => Task.FromResult(Environment.GetEnvironmentVariable("PLEX_TOKEN") ?? "unclaimed-token");
    }

    // ── tests ───────────────────────────────────────────────────────

    [Test]
    public async Task Identity_AnswersWithMachineIdentifier()
    {
        var identity = await _server.Http.GetFromJsonAsync<JsonElement>(PlexLiveServer.BaseUrl + "/identity");
        Assert.That(identity.GetProperty("MediaContainer").TryGetProperty("machineIdentifier", out _),
            Is.True, "the sync requires the machine id for its server:// uris");
    }

    [Test]
    public async Task Directory_TestConnection_SucceedsAgainstRealServer()
    {
        var directory = new PlexDirectory(new HttpClient());
        Assert.That(await directory.TestConnectionAsync(
            new MediaServerConnection(PlexLiveServer.BaseUrl, "any-token")), Is.True,
            "token-gated routes answer on a real unclaimed server");
    }

    [Test]
    public async Task Sync_NoMusicLibrary_ReportsItInsteadOfFailing()
    {
        var sync = new PlexSync(new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            NullLogger<PlexSync>.Instance, new FixedSettings());

        var playlist = new Playlist
        {
            Name = "MB Live Mix",
            Tracks = new List<Track>
            {
                new() { Title = "Alpha Song", CurrentTrackLocation = new FileLocation("/media/alpha.mp3") },
            },
        };
        await sync.SyncPlaylistAsync(playlist, new PlaylistOutputOptions("/tmp/ignored.m3u", false, null));

        Assert.That(sync.LastReport!.Message, Does.Contain("music"),
            "a fresh server without a music library explains itself in the report");
    }

    [Test]
    public async Task Sync_WithClaimToken_IfProvided()
    {
        var token = Environment.GetEnvironmentVariable("PLEX_TOKEN");
        Assume.That(string.IsNullOrWhiteSpace(token) is false,
            "PLEX_TOKEN not set; claimed-server sync skipped");

        var sync = new PlexSync(new HttpClient { Timeout = TimeSpan.FromSeconds(30) },
            NullLogger<PlexSync>.Instance, new FixedSettings());
        var playlist = new Playlist
        {
            Name = "MB Live Mix",
            Tracks = new List<Track>
            {
                new() { Title = "Alpha Song", CurrentTrackLocation = new FileLocation("/media/alpha.mp3") },
            },
        };
        await sync.SyncPlaylistAsync(playlist, new PlaylistOutputOptions("/tmp/ignored.m3u", false, null));

        Assert.That(sync.LastReport!.Message,
            Is.AnyOf("Created playlist", "Updated existing playlist", "Created playlist (no id)"),
            "a claimed-server sync must create or update the playlist, not fail");
    }
}
