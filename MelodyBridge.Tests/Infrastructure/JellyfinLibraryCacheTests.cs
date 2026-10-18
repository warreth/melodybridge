using System.Net;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.MediaServers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The Jellyfin library walk is shared by the playlists of one sync job:
/// a job runs one sync per playlist back to back, and walking a large
/// library once per playlist would cost more than the per-track lookups
/// this replaced. The cache is keyed on the connection, short-lived, and
/// never caches a failed walk.
/// </summary>
[TestFixture]
public class JellyfinLibraryCacheTests
{
    private sealed class FixedSettings(string apiKey = "test-key") : IJellyfinSettings
    {
        public Task<string> GetBaseUrlAsync(CancellationToken ct = default) => Task.FromResult("http://jellyfin:8096");
        public Task<string> GetApiKeyAsync(CancellationToken ct = default) => Task.FromResult(apiKey);
        public Task<string?> GetUserIdAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
    }

    [TearDown]
    public void TearDown() => JellyfinSync.LibraryIndexTtl = TimeSpan.FromSeconds(60);

    private static JellyfinSync NewSync(ScriptedHandler handler, IJellyfinSettings? settings = null)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://jellyfin:8096") },
            NullLogger<JellyfinSync>.Instance, settings ?? new FixedSettings());

    private static Playlist OneTrackPlaylist(string name)
        => new()
        {
            Name = name,
            Tracks = new List<Track>
            {
                new() { Title = "Alpha", CurrentTrackLocation = new FileLocation("/media/alpha.flac") },
            },
        };

    private static int LibraryWalks(ScriptedHandler handler)
        => handler.Requests.Count(r =>
            r.Url.Contains("IncludeItemTypes=Audio&Fields=Path&EnableTotalRecordCount=false"));

    [Test]
    public async Task TwoPlaylists_ShareOneLibraryWalk()
    {
        var handler = new ScriptedHandler()
            .On("IncludeItemTypes=Audio", """{"Items":[{"Id":"item-1","Name":"Alpha","Path":"/media/alpha.flac"}]}""")
            .On("IncludeItemTypes=Playlist", """{"Items":[]}""")
            .On("/Playlists", """{"Id":"pl","Name":"List"}""");
        var sync = NewSync(handler);

        await sync.SyncPlaylistAsync(OneTrackPlaylist("List A"), new PlaylistOutputOptions("/tmp/out.m3u", false, null));
        await sync.SyncPlaylistAsync(OneTrackPlaylist("List B"), new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.Multiple(() =>
        {
            Assert.That(LibraryWalks(handler), Is.EqualTo(1),
                "the second playlist of the same job reuses the walk");
            Assert.That(sync.LastReport!.ResolvedCount, Is.EqualTo(1),
                "and the path still resolves for it");
        });
    }

    [Test]
    public async Task ExpiredIndex_IsWalkedAgain()
    {
        var handler = new ScriptedHandler()
            .On("IncludeItemTypes=Audio", """{"Items":[]}""")
            .On("IncludeItemTypes=Playlist", """{"Items":[]}""")
            .On("/Playlists", """{"Id":"pl","Name":"List"}""");
        var sync = NewSync(handler);
        JellyfinSync.LibraryIndexTtl = TimeSpan.Zero;

        await sync.SyncPlaylistAsync(OneTrackPlaylist("List A"), new PlaylistOutputOptions("/tmp/out.m3u", false, null));
        await sync.SyncPlaylistAsync(OneTrackPlaylist("List B"), new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.That(LibraryWalks(handler), Is.EqualTo(2), "an expired index is fetched again");
    }

    [Test]
    public async Task FailedWalk_IsNotCached()
    {
        var handler = new ScriptedHandler()
            .On("IncludeItemTypes=Audio", "{}", HttpStatusCode.BadRequest)
            .On("IncludeItemTypes=Playlist", """{"Items":[]}""")
            .On("/Playlists", """{"Id":"pl","Name":"List"}""");
        var sync = NewSync(handler);

        await sync.SyncPlaylistAsync(OneTrackPlaylist("List A"), new PlaylistOutputOptions("/tmp/out.m3u", false, null));
        await sync.SyncPlaylistAsync(OneTrackPlaylist("List B"), new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.That(LibraryWalks(handler), Is.EqualTo(2),
            "a server that just refused is asked again for the next playlist");
    }

    [Test]
    public async Task DifferentCredentials_DoNotShareTheIndex()
    {
        var handler = new ScriptedHandler()
            .On("IncludeItemTypes=Audio", """{"Items":[]}""")
            .On("IncludeItemTypes=Playlist", """{"Items":[]}""")
            .On("/Playlists", """{"Id":"pl","Name":"List"}""");

        await NewSync(handler, new FixedSettings("key-one"))
            .SyncPlaylistAsync(OneTrackPlaylist("List A"), new PlaylistOutputOptions("/tmp/out.m3u", false, null));
        await NewSync(handler, new FixedSettings("key-two"))
            .SyncPlaylistAsync(OneTrackPlaylist("List B"), new PlaylistOutputOptions("/tmp/out.m3u", false, null));

        Assert.That(LibraryWalks(handler), Is.EqualTo(2),
            "a library read with one token says nothing about another server or account");
    }
}
