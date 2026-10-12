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
        var handler = new ScriptedHandler()
            .On("/Items/ByPath", """{"Id":"item-1","Name":"Alpha"}""")
            .On("/Items?Recursive=true&IncludeItemTypes=Playlist", """{"Items":[]}""")
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
        });
    }

    [Test]
    public async Task Update_PostsTheIdsToThePlaylistRoute()
    {
        var handler = new ScriptedHandler()
            .On("/Items/ByPath", """{"Id":"item-1","Name":"Alpha"}""")
            .On("/Items?Recursive=true&IncludeItemTypes=Playlist", """{"Items":[{"Id":"existing-id","Name":"List"}]}""")
            .On("/Playlists/existing-id", "");

        await NewSync(handler).SyncPlaylistAsync(OneTrackPlaylist(),
            new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        var update = handler.Recorded.Single(r => r.Url.Contains("/Playlists/existing-id"));
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
}
