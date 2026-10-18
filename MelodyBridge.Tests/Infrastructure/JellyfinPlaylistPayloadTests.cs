using MelodyBridge.Core;
using MelodyBridge.Infrastructure.MediaServers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The playlist payload Jellyfin actually accepts: the member item ids
/// live in "Ids" (CreatePlaylistDto/UpdatePlaylistDto), the update is a
/// POST to /Playlists/{id} (there is no PUT route), and nothing may
/// create a second playlist around an existing one.
///
/// The old client sent "PlaylistItems" and used PUT, so Jellyfin ignored
/// the ids (empty playlists) and answered 405 (duplicate playlists).
/// </summary>
[TestFixture]
public class JellyfinPlaylistPayloadTests
{
    private sealed class FixedSettings : IJellyfinSettings
    {
        public Task<string> GetBaseUrlAsync(CancellationToken ct = default) => Task.FromResult("http://jellyfin:8096");
        public Task<string> GetApiKeyAsync(CancellationToken ct = default) => Task.FromResult("test-key");
        public Task<string?> GetUserIdAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    private static JellyfinSync NewSync(ScriptedHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://jellyfin:8096") },
            NullLogger<JellyfinSync>.Instance, new FixedSettings());

    private static Playlist OneTrackPlaylist()
        => new()
        {
            Name = "List",
            Tracks = new List<Track>
            {
                new() { Title = "Alpha", CurrentTrackLocation = new FileLocation("/media/alpha.flac") },
            },
        };

    [Test]
    public async Task Create_SendsTheItemIdsInIds()
    {
        // The library walk resolves the track by path: no per-track
        // lookup requests at all.
        var handler = new ScriptedHandler()
            .On("IncludeItemTypes=Audio", """{"Items":[{"Id":"item-1","Name":"Alpha","Path":"/media/alpha.flac"}]}""")
            .On("IncludeItemTypes=Playlist", """{"Items":[]}""")
            .On("/Playlists", """{"Id":"new-id","Name":"List"}""");

        await NewSync(handler).SyncPlaylistAsync(OneTrackPlaylist(),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        var create = handler.Recorded.Single(r => r.Method == "POST" && r.Url == "/Playlists");
        Assert.Multiple(() =>
        {
            Assert.That(create.Body, Does.Contain("\"ids\"").IgnoreCase,
                "Jellyfin binds the playlist members from Ids");
            Assert.That(create.Body, Does.Contain("item-1"),
                "the resolved item id must travel in the payload");
            Assert.That(create.Body, Does.Not.Contain("PlaylistItems").IgnoreCase,
                "PlaylistItems is an unknown property Jellyfin ignores");
            Assert.That(handler.Requests.Any(r => r.Url.Contains("ByPath")), Is.False,
                "current Jellyfin has no ByPath route; it must not be probed when the library was read");
            Assert.That(handler.Requests.Any(r => r.Url.Contains("SearchTerm")), Is.False,
                "a path the library holds needs no search");
        });
    }

    [Test]
    public async Task Update_PostsTheIdsToThePlaylistRoute()
    {
        var handler = new ScriptedHandler()
            .On("IncludeItemTypes=Audio", """{"Items":[{"Id":"item-1","Name":"Alpha","Path":"/media/alpha.flac"}]}""")
            .On("IncludeItemTypes=Playlist", """{"Items":[{"Id":"existing-id","Name":"List"}]}""")
            // The read answers a different membership, so the update runs.
            .On("/Playlists/existing-id", """{"ItemIds":["stale-id"]}""");

        await NewSync(handler).SyncPlaylistAsync(OneTrackPlaylist(),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        var update = handler.Recorded
            .Single(r => r.Method == "POST" && r.Url.Contains("/Playlists/existing-id"));
        Assert.Multiple(() =>
        {
            Assert.That(update.Method, Is.EqualTo("POST"),
                "Jellyfin updates a playlist with POST /Playlists/{id}");
            Assert.That(update.Body, Does.Contain("\"ids\"").IgnoreCase);
            Assert.That(handler.Recorded.Any(r => r.Method == "PUT"), Is.False,
                "no PUT route exists");
            Assert.That(handler.Recorded.Any(r => r.Method == "POST" && r.Url == "/Playlists"), Is.False,
                "an update must never create a second playlist");
        });
    }

    [Test]
    public async Task Update_WhenTheMembersAreUnchanged_SkipsTheWrite()
    {
        var handler = new ScriptedHandler()
            .On("IncludeItemTypes=Audio", """{"Items":[{"Id":"item-1","Name":"Alpha","Path":"/media/alpha.flac"}]}""")
            .On("IncludeItemTypes=Playlist", """{"Items":[{"Id":"existing-id","Name":"List"}]}""")
            .On("/Playlists/existing-id", """{"ItemIds":["item-1"]}""");

        var sync = NewSync(handler);
        await sync.SyncPlaylistAsync(OneTrackPlaylist(),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.Multiple(() =>
        {
            Assert.That(handler.Recorded.Any(r => r.Method == "POST" && r.Url.Contains("/Playlists")), Is.False,
                "an unchanged playlist is not rewritten on every run");
            Assert.That(sync.LastReport!.Message, Does.Contain("unchanged"));
            Assert.That(sync.LastReport.Succeeded, Is.True);
        });
    }
}
