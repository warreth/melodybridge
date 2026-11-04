using System.Net;
using System.Text;
using MelodyBridge.Infrastructure.Accounts;
using MelodyBridge.Infrastructure.AppleMusic;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Accounts;

[TestFixture]
public class AppleMusicAccountProviderTests
{
    private string _root = null!;
    private TestSqliteFactory _factory = null!;
    private AccountTokenStore _tokens = null!;
    private ScriptedHandler _handler = null!;
    private AppleMusicAccountProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-apple-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _factory = new TestSqliteFactory(Path.Combine(_root, "apple.db"));
        using (var db = _factory.CreateDbContext()) db.Database.EnsureCreated();
        _tokens = new AccountTokenStore(_factory, NullLogger<AccountTokenStore>.Instance);

        _handler = new ScriptedHandler()
            .On("/assets/index", AppleMusicTestData.Bundle)
            .On("/", AppleMusicTestData.HomeHtml);
        var tokenProvider = AppleMusicTestData.TokenProvider(_handler);
        var api = new AppleMusicApiClient(
            new HttpClient(_handler), tokenProvider,
            NullLogger<AppleMusicApiClient>.Instance)
        {
            MaxAttempts = 1,
            RetryBaseDelay = TimeSpan.Zero,
        };
        _provider = new AppleMusicAccountProvider(
            _tokens, api, NullLogger<AppleMusicAccountProvider>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _handler.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private async Task ConnectAsync()
    {
        await _provider.SaveSettingAsync(AppleMusicAccountProvider.TokenKey, "media-token");
        await _provider.SetEnabledAsync(true);
    }

    [Test]
    public async Task IsConnected_NeedsBothTheSwitchAndAToken()
    {
        Assert.That(await _provider.IsConnectedAsync(), Is.False);

        await _provider.SaveSettingAsync(AppleMusicAccountProvider.TokenKey, "media-token");
        Assert.That(await _provider.IsConnectedAsync(), Is.False, "a token without the switch is not connected");

        await _provider.SetEnabledAsync(true);
        Assert.That(await _provider.IsConnectedAsync(), Is.True);

        await _provider.SetEnabledAsync(false);
        Assert.That(await _provider.IsConnectedAsync(), Is.False);
    }

    [Test]
    public async Task GetUserPlaylists_MapsGlobalIdAndLibraryId()
    {
        await ConnectAsync();
        _handler.On("/v1/me/storefront", """{"data":[{"id":"gb","type":"storefronts"}]}""");
        _handler.On("/v1/me/library/playlists", AppleMusicTestData.LibraryPlaylistsJson(
            next: null,
            ("p.one", "pl.one", "Shared List", "Owner", 3, true),
            ("p.two", null, "Private List", "Owner", 0, false)));

        var playlists = await _provider.GetUserPlaylistsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(playlists, Has.Count.EqualTo(2));
            Assert.That(playlists[0].Id, Is.EqualTo("pl.one"),
                "a catalog-linked playlist keeps the public id for dedup");
            Assert.That(playlists[0].Name, Is.EqualTo("Shared List"));
            Assert.That(playlists[0].IsOwned, Is.True);
            Assert.That(playlists[0].Storefront, Is.EqualTo("gb"),
                "the account storefront travels with the playlist");
            Assert.That(playlists[1].Id, Is.EqualTo("p.two"));
            Assert.That(playlists[1].TrackCount, Is.EqualTo(0));
            Assert.That(playlists[1].IsOwned, Is.False);
        });
    }

    [Test]
    public async Task GetLikedPlaylist_UsesLibrarySongsAndNeverMarksFavourites()
    {
        await ConnectAsync();
        _handler.On("/v1/me/library/songs", AppleMusicTestData.LibrarySongsJson(
            next: null,
            ("i.one", "Saved One", "31", "USCCC0000031"),
            ("i.two", "Saved Two", "32", "USCCC0000032")));

        var playlist = await _provider.GetLikedPlaylistAsync();

        Assert.Multiple(() =>
        {
            Assert.That(playlist.Name, Is.EqualTo("Library songs"));
            Assert.That(playlist.Id, Is.EqualTo(AppleMusicAccountProvider.LikedId));
            Assert.That(playlist.SourceUrl, Is.EqualTo(AppleMusicAccountProvider.LikedSourceUrl));
            Assert.That(playlist.Tracks, Has.Count.EqualTo(2));
            Assert.That(playlist.Tracks!.All(t => !t.IsLiked), Is.True,
                "Apple has no liked-song list, so these stay unmarked");
            Assert.That(playlist.Tracks[0].PlatformSongID!.ID, Is.EqualTo("31"),
                "the catalog id is used when Apple reports one");
            Assert.That(playlist.Tracks[0].Isrc, Is.EqualTo("USCCC0000031"));
        });
    }

    [Test]
    public async Task Test_ReportsTheLibraryTotalsWithoutWalkingIt()
    {
        await ConnectAsync();
        _handler.On("/v1/me/library/playlists", """{"data":[],"meta":{"total":4}}""");
        _handler.On("/v1/me/library/songs", """{"data":[],"meta":{"total":9}}""");

        var message = await _provider.TestAsync();

        Assert.That(message, Does.Contain("4 playlists"));
        Assert.That(message, Does.Contain("9 library songs"));
        Assert.That(_handler.Requests.Count(r => r.Url.Contains("/v1/me/library")), Is.EqualTo(2),
            "the test uses two one-row reads, not the whole library");
    }

    [Test]
    public async Task TryGetPlaylist_ViaLibrary_MapsTheCatalogIdAndTracks()
    {
        await ConnectAsync();
        _handler.On("/v1/me/storefront", """{"data":[{"id":"gb","type":"storefronts"}]}""");
        _handler.On("/v1/me/library/playlists/p.one",
            AppleMusicTestData.LibraryPlaylistJson("p.one"));

        var playlist = await _provider.TryGetPlaylistViaAccountAsync("p.one");

        Assert.That(playlist, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(playlist!.Id, Is.EqualTo("pl.one"));
            Assert.That(playlist.Name, Is.EqualTo("Private List"));
            Assert.That(playlist.SourceUrl, Is.EqualTo("applemusic:library:p.one"));
            Assert.That(playlist.Tracks, Has.Count.EqualTo(1));
            Assert.That(playlist.Tracks![0].Title, Is.EqualTo("Library One"));
            Assert.That(playlist.Tracks[0].PlatformSongID!.ID, Is.EqualTo("22"));
            Assert.That(playlist.Tracks[0].Isrc, Is.EqualTo("USBBB0000001"));
        });
    }

    [Test]
    public async Task TryGetPlaylist_RejectedSession_ReturnsNullForThePublicPath()
    {
        await ConnectAsync();
        _handler.On("/v1/me/library/playlists/p.one",
            _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    """{"errors":[{"detail":"Invalid authentication"}]}""",
                    Encoding.UTF8, "application/json"),
            });

        var playlist = await _provider.TryGetPlaylistViaAccountAsync("p.one");
        Assert.That(playlist, Is.Null);
    }

    [Test]
    public async Task Test_RejectedSession_SaysToPasteAFreshCookie()
    {
        await ConnectAsync();
        _handler.On("/v1/me/library/playlists",
            _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    """{"errors":[{"detail":"Invalid authentication"}]}""",
                    Encoding.UTF8, "application/json"),
            });

        var ex = Assert.ThrowsAsync<AppleMusicApiException>(() => _provider.TestAsync());
        Assert.That(ex!.Message, Does.Contain("fresh media-user-token"));
    }

    [Test]
    public async Task TryGetPlaylist_NonUsLink_PreservesTheLinkStorefront()
    {
        await ConnectAsync();
        _handler.On("/v1/me/storefront", """{"data":[{"id":"us","type":"storefronts"}]}""");
        _handler.On("/v1/catalog/gb/playlists/pl.gb",
            AppleMusicTestData.PlaylistApiJson(
                "pl.gb", null, ("11", "One", "Artist", "USAAA0000011")));

        var playlist = await _provider.TryGetPlaylistViaAccountAsync(
            "https://music.apple.com/gb/playlist/name/pl.gb");

        Assert.That(playlist, Is.Not.Null);
        Assert.That(playlist!.SourceUrl, Does.Contain("/gb/playlist/pl.gb"));
        Assert.That(_handler.Requests.Any(r =>
                r.Url.Contains("/v1/catalog/gb/playlists/pl.gb")), Is.True,
            "the gb link is fetched from the gb catalog, not the account's us catalog");
    }

    [Test]
    public async Task GetUserPlaylists_PagesThroughNextAndTotal()
    {
        await ConnectAsync();
        _handler.On("/v1/me/storefront", """{"data":[{"id":"gb","type":"storefronts"}]}""");
        _handler.On("/v1/me/library/playlists?limit=100&offset=0",
            """
            {"data":[{"id":"p.one","type":"library-playlists","attributes":{"name":"One","curatorName":"Owner"}}],
             "meta":{"total":2},
             "next":"/v1/me/library/playlists?limit=100&offset=1"}
            """);
        _handler.On("/v1/me/library/playlists?limit=100&offset=1",
            """
            {"data":[{"id":"p.two","type":"library-playlists","attributes":{"name":"Two","curatorName":"Owner"}}],
             "meta":{"total":2}}
            """);

        var playlists = await _provider.GetUserPlaylistsAsync();

        Assert.That(playlists.Select(p => p.Name), Is.EqualTo(new[] { "One", "Two" }));
        Assert.That(_handler.Requests.Count(r =>
                r.Url.Contains("/v1/me/library/playlists?limit=100&offset=")), Is.EqualTo(2),
            "the second page is requested through the next offset");
    }

    [Test]
    public void GetUserPlaylists_WithoutAToken_ExplainsWhatToPaste()
    {
        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => _provider.GetUserPlaylistsAsync());
        Assert.That(ex!.Message, Does.Contain("media-user-token"));
    }
}
