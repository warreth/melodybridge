using System.Net;
using System.Text;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Accounts;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Accounts;

/// <summary>
/// The unofficial Spotify connection: a session cookie drives Spotify's
/// internal web-player API. Everything here runs against a fake transport
/// with the exact JSON shapes captured from the live API, plus the TOTP
/// derivation pinned against an independent implementation.
/// </summary>
[TestFixture]
public class SpotifyUnofficialAccountTests
{
    // ── the TOTP the token endpoint wants ───────────────────────────────

    [TestCase(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 1_700_000_000L, "678875")]
    [TestCase(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 1_700_000_030L, "222010")]
    [TestCase(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, 1_730_000_000L, "451155")]
    [TestCase(new byte[] { 255, 0, 128, 64, 9 }, 1_700_000_000L, "999322")]
    public void Totp_MatchesTheIndependentImplementation(byte[] secret, long time, string expected)
    {
        Assert.That(SpotifyWebSession.GenerateTotp(secret, time), Is.EqualTo(expected),
            "the web player's key derivation is pinned to a known answer");
    }

    [Test]
    public void Secrets_NewestVersionWins()
    {
        const string json = """
        [
          { "version": 3, "secret": [1, 2, 3] },
          { "version": 61, "secret": [9, 8, 7, 6] }
        ]
        """;

        var newest = SpotifyWebSession.ParseNewestSecret(json);

        Assert.That(newest, Is.Not.Null);
        Assert.That(newest!.Value.Version, Is.EqualTo(61));
        Assert.That(newest.Value.Secret, Is.EqualTo(new byte[] { 9, 8, 7, 6 }));
    }

    [TestCase("[{\"version\":1}]", TestName = "Secrets_EntryWithoutASecret_IsNull")]
    [TestCase("{\"not\":\"an array\"}", TestName = "Secrets_ObjectInsteadOfArray_IsNull")]
    [TestCase("not json at all", TestName = "Secrets_Garbage_IsNull")]
    [TestCase("[]", TestName = "Secrets_EmptyList_IsNull")]
    public void Secrets_UnusableAnswers_AreNull(string json)
    {
        Assert.That(SpotifyWebSession.ParseNewestSecret(json), Is.Null);
    }

    // ── the internal API shapes, taken from the live answers ────────────

    [Test]
    public void LikedTrackItem_MapsEverythingThePayloadCarries()
    {
        using var doc = JsonDocument.Parse(LikedTrackItem);
        Assert.That(SpotifyWebGraphQl.TryGetTrackNode(doc.RootElement, out var uri, out var data),
            Is.True);

        var track = SpotifyWebGraphQl.MapTrack(data, uri, isLiked: true);

        Assert.That(track, Is.Not.Null);
        Assert.That(track!.Title, Is.EqualTo("Daar Gaat Ze"));
        Assert.That(track.Artist, Is.EqualTo("Clouseau"));
        Assert.That(track.Album, Is.EqualTo("Hoezo?"));
        Assert.That(track.AlbumArtist, Is.EqualTo("Clouseau"));
        Assert.That(track.Duration, Is.EqualTo(TimeSpan.FromMilliseconds(229466)));
        Assert.That(track.SongID, Is.EqualTo(new SongID(Platform.Spotify, "2vFzC766RC2m36ywFyj09S")),
            "the uri lives on the wrapper for liked songs");
        Assert.That(track.CoverUrl, Is.EqualTo("https://i.scdn.co/image/640"),
            "the largest cover wins");
        Assert.That(track.IsLiked, Is.True);
        Assert.That(track.Isrc, Is.Null,
            "this API does not carry ISRCs; the download pipeline resolves them");
    }

    [Test]
    public void PlaylistItem_MapsTheItemV2Shape()
    {
        using var doc = JsonDocument.Parse(PlaylistItem);
        Assert.That(SpotifyWebGraphQl.TryGetTrackNode(doc.RootElement, out var uri, out var data),
            Is.True);

        var track = SpotifyWebGraphQl.MapTrack(data, uri, isLiked: false);

        Assert.That(track, Is.Not.Null);
        Assert.That(track!.Title, Is.EqualTo("Bonzai Channel One - Original Remastered Mix"));
        Assert.That(track.SongID,
            Is.EqualTo(new SongID(Platform.Spotify, "1pdoK6VJe8wwp86SrfByx0")),
            "playlist items carry the uri inside the data");
        Assert.That(track.Duration, Is.EqualTo(TimeSpan.FromMilliseconds(251793)));
        Assert.That(track.IsLiked, Is.False);
    }

    [Test]
    public void PodcastEpisode_IsSkipped()
    {
        using var doc = JsonDocument.Parse(
            """{ "itemV2": { "data": { "__typename": "Episode", "name": "Talk", "uri": "spotify:episode:x" } } }""");
        Assert.That(SpotifyWebGraphQl.TryGetTrackNode(doc.RootElement, out var uri, out var data), Is.True);

        Assert.That(SpotifyWebGraphQl.MapTrack(data, uri, isLiked: false), Is.Null);
    }

    [Test]
    public void LibraryPlaylist_CarriesNameOwnerCountAndCover()
    {
        using var doc = JsonDocument.Parse(LibraryPlaylistItem);

        var playlist = SpotifyWebGraphQl.MapLibraryPlaylist(doc.RootElement);

        Assert.That(playlist, Is.Not.Null);
        Assert.That(playlist!.Id, Is.EqualTo("55V41RYWVdALRiiN1onkUr"));
        Assert.That(playlist.Name, Is.EqualTo("Techno"));
        Assert.That(playlist.Owner, Is.EqualTo("Warre"));
        Assert.That(playlist.TrackCount, Is.EqualTo(104));
        Assert.That(playlist.CoverImageUrl, Is.EqualTo("https://i.scdn.co/image/playlist640"));
    }

    // ── the provider against a fake Spotify ─────────────────────────────

    private string _dbPath = null!;
    private AccountTokenStore _tokens = null!;
    private IDbContextFactory<MelodyBridgeDbContext> _dbFactory = null!;

    [SetUp]
    public async Task SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-web-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddDbContextFactory<MelodyBridgeDbContext>(
            o => o.UseSqlite($"Data Source={_dbPath};Pooling=False"));
        var provider = services.BuildServiceProvider();
        _dbFactory = provider.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        await using (var db = _dbFactory.CreateDbContext())
            await db.Database.EnsureCreatedAsync();
        _tokens = new AccountTokenStore(_dbFactory, NullLogger<AccountTokenStore>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
    }

    private SpotifyUnofficialAccountProvider ProviderWith(FakeSpotifyWeb web)
    {
        var provider = new SpotifyUnofficialAccountProvider(
            _tokens,
            new SpotifyWebSession(web.Client, NullLogger<SpotifyWebSession>.Instance),
            web.Client,
            NullLogger<SpotifyUnofficialAccountProvider>.Instance)
        {
            // The 250 ms pacing is right for Spotify, pointless in tests.
            PageDelay = TimeSpan.Zero,
        };
        return provider;
    }

    [Test]
    public async Task NotEnabled_IsNotConnected_AndNothingIsFetched()
    {
        var web = new FakeSpotifyWeb();
        var provider = ProviderWith(web);

        Assert.That(await provider.IsConnectedAsync(), Is.False);
        Assert.That(await provider.GetLikedPlaylistAsync().ContinueWith(t => t.IsFaulted),
            Is.True, "an unconfigured connection must not pretend to fetch");
    }

    [Test]
    public async Task Enabled_Session_MintsATokenAndReadsLikedSongs()
    {
        var web = new FakeSpotifyWeb { LikedTotal = 2 };
        var provider = ProviderWith(web);
        await provider.SaveSettingAsync("cookie", "real-cookie-value");
        await provider.SetEnabledAsync(true);

        Assert.That(await provider.IsConnectedAsync(), Is.True);

        var liked = await provider.GetLikedPlaylistAsync();

        Assert.That(liked.Id, Is.EqualTo(SpotifyUnofficialAccountProvider.LikedId));
        Assert.That(liked.SourceUrl, Is.EqualTo(SpotifyUnofficialAccountProvider.LikedSourceUrl));
        Assert.That(liked.Tracks, Has.Count.EqualTo(2));
        Assert.That(liked.Tracks!.All(t => t.IsLiked), Is.True);
        Assert.That(liked.Tracks!.All(t => t.SongID is not null), Is.True);

        // The token request carries a six digit TOTP and the newest secret
        // version, and the cookie travels as the sp_dc cookie.
        Assert.That(web.TokenRequests, Has.Count.EqualTo(1));
        var tokenUrl = web.TokenRequests[0].Url;
        Assert.That(tokenUrl, Does.Contain("totpVer=61"));
        Assert.That(System.Text.RegularExpressions.Regex.IsMatch(tokenUrl, @"totp=\d{6}"), Is.True,
            "the TOTP is a six digit code");
        Assert.That(web.TokenRequests[0].Cookie, Is.EqualTo("sp_dc=real-cookie-value"));
    }

    [Test]
    public async Task LikedSongs_ClampedPages_ArePagedByWhatCameBack()
    {
        // Spotify answers 20 per page although 50 were asked: stepping by 50
        // would silently skip 30 tracks on every page.
        var web = new FakeSpotifyWeb { LikedTotal = 60, LikedPageCap = 20 };
        var provider = ProviderWith(web);
        await provider.SaveSettingAsync("cookie", "cookie");
        await provider.SetEnabledAsync(true);

        var liked = await provider.GetLikedPlaylistAsync();

        Assert.That(liked.Tracks, Has.Count.EqualTo(60));
        Assert.That(web.PageRequests, Is.EqualTo(new[] { (0, 50), (20, 50), (40, 50) }),
            "the cursor advances by the page the API returned");
        Assert.That(liked.Warning, Is.Null, "a clamped but complete answer is not partial");
    }

    [Test]
    public async Task LikedSongs_ShortAnswer_IsMarkedPartial()
    {
        var web = new FakeSpotifyWeb { LikedTotal = 60, LikedStopAfter = 20 };
        var provider = ProviderWith(web);
        await provider.SaveSettingAsync("cookie", "cookie");
        await provider.SetEnabledAsync(true);

        var liked = await provider.GetLikedPlaylistAsync();

        Assert.That(liked.Tracks, Has.Count.EqualTo(20));
        Assert.That(liked.Warning, Is.Not.Null,
            "a short answer must not look like a small library");
        Assert.That(liked.Warning, Does.Contain("20 of 60"));
    }

    [Test]
    public async Task LibraryAtAnExactPageBoundary_DoesNotAskForAnEmptyPage()
    {
        // 50 library items including the liked-songs row: the old loop
        // compared the kept playlists (49) with the total and asked again.
        var web = new FakeSpotifyWeb { LibraryTotal = 50 };
        var provider = ProviderWith(web);
        await provider.SaveSettingAsync("cookie", "cookie");
        await provider.SetEnabledAsync(true);

        var playlists = await provider.GetUserPlaylistsAsync();

        Assert.That(playlists, Has.Count.EqualTo(49));
        Assert.That(web.LibraryRequests, Is.EqualTo(new[] { (0, 50) }),
            "the liked row counts toward the page, so no second request is owed");
    }

    [Test]
    public async Task Playlist_ShortAnswer_IsMarkedPartial()
    {
        var web = new FakeSpotifyWeb { PlaylistTotal = 60, PlaylistStopAfter = 20 };
        var provider = ProviderWith(web);
        await provider.SaveSettingAsync("cookie", "cookie");
        await provider.SetEnabledAsync(true);

        var playlist = await provider.TryGetPlaylistViaAccountAsync("55V41RYWVdALRiiN1onkUr");

        Assert.That(playlist, Is.Not.Null);
        Assert.That(playlist!.Tracks, Has.Count.EqualTo(20));
        Assert.That(playlist.Warning, Does.Contain("20 of 60"),
            "the store turns this into a partial snapshot instead of a clean one");
    }

    [Test]
    public async Task RateLimitedQuery_IsRetriedOnce()
    {
        var web = new FakeSpotifyWeb { RateLimitOnce = true };
        var provider = ProviderWith(web);
        await provider.SaveSettingAsync("cookie", "cookie");
        await provider.SetEnabledAsync(true);

        var liked = await provider.GetLikedPlaylistAsync();

        Assert.That(liked.Tracks, Has.Count.EqualTo(2), "the retry fetched the page");
        Assert.That(web.LikedAttempts, Is.EqualTo(2),
            "one 429 is retried once, not surfaced as a failed import");
    }

    [Test]
    public async Task LikedSongs_PagePastTheFirstOne_IsFetchedToo()
    {
        var web = new FakeSpotifyWeb { LikedTotal = 60 };
        var provider = ProviderWith(web);
        await provider.SaveSettingAsync("cookie", "cookie");
        await provider.SetEnabledAsync(true);

        var liked = await provider.GetLikedPlaylistAsync();

        Assert.That(liked.Tracks, Has.Count.EqualTo(60),
            "a library larger than one page arrives whole");
        Assert.That(web.PageRequests, Is.EqualTo(new[] { (0, 50), (50, 50) }),
            "the second page asks for the next offset and stops on the short page");
    }

    [Test]
    public async Task AnonymousToken_IsAnHonestError_NotAQuietEmptyImport()
    {
        var web = new FakeSpotifyWeb { Anonymous = true };
        var provider = ProviderWith(web);
        await provider.SaveSettingAsync("cookie", "expired-cookie");
        await provider.SetEnabledAsync(true);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetLikedPlaylistAsync())!;
        Assert.That(ex.Message, Does.Contain("anonymous"),
            "an expired cookie must say so instead of importing nothing");
        Assert.That(ex.Message, Does.Contain("open.spotify.com"),
            "the message tells the user where to get a fresh cookie");
    }

    [Test]
    public async Task Playlists_AreListed_AndTheLikedSongsRowIsNotOne()
    {
        var web = new FakeSpotifyWeb();
        var provider = ProviderWith(web);
        await provider.SaveSettingAsync("cookie", "cookie");
        await provider.SetEnabledAsync(true);

        var playlists = await provider.GetUserPlaylistsAsync();

        Assert.That(playlists, Has.Count.EqualTo(1), "the liked-songs row is skipped");
        Assert.That(playlists[0].Id, Is.EqualTo("55V41RYWVdALRiiN1onkUr"));
        Assert.That(playlists[0].Name, Is.EqualTo("Techno"));
    }

    [Test]
    public async Task PlaylistFetch_MapsMetadataAndTracks()
    {
        var web = new FakeSpotifyWeb();
        var provider = ProviderWith(web);
        await provider.SaveSettingAsync("cookie", "cookie");
        await provider.SetEnabledAsync(true);

        var playlist = await provider.TryGetPlaylistViaAccountAsync("55V41RYWVdALRiiN1onkUr");

        Assert.That(playlist, Is.Not.Null);
        Assert.That(playlist!.Name, Is.EqualTo("Techno"));
        Assert.That(playlist.Description, Is.EqualTo("hard and fast"));
        Assert.That(playlist.Owner, Is.EqualTo("Warre"));
        Assert.That(playlist.CoverImageUrl, Is.EqualTo("https://i.scdn.co/image/playlist640"));
        Assert.That(playlist.Tracks, Has.Count.EqualTo(1));
        Assert.That(playlist.SourceUrl,
            Is.EqualTo("https://open.spotify.com/playlist/55V41RYWVdALRiiN1onkUr"));
    }

    [Test]
    public async Task PlaylistFetch_PagesUntilTheAdvertisedTotal()
    {
        var web = new FakeSpotifyWeb { PlaylistTotal = 104 };
        var provider = ProviderWith(web);
        await provider.SaveSettingAsync("cookie", "cookie");
        await provider.SetEnabledAsync(true);

        var playlist = await provider.TryGetPlaylistViaAccountAsync("55V41RYWVdALRiiN1onkUr");

        Assert.That(playlist, Is.Not.Null);
        Assert.That(playlist!.Tracks, Has.Count.EqualTo(104),
            "a playlist longer than one page arrives whole");
        Assert.That(web.PlaylistPageRequests, Is.EqualTo(new[] { (0, 50), (50, 50), (100, 50) }),
            "the offsets follow the page size and the last page stops the loop");
    }

    [Test]
    public async Task PlaylistFetch_WithoutASession_IsNull_SoThePublicPathRuns()
    {
        var web = new FakeSpotifyWeb();
        var provider = ProviderWith(web);
        await provider.SaveSettingAsync("cookie", "cookie");
        await provider.SetEnabledAsync(true);

        // Spotify answering with errors (a private playlist, a shape change)
        // must degrade to null, never to a broken snapshot.
        web.BreakPlaylistQuery = true;

        Assert.That(await provider.TryGetPlaylistViaAccountAsync("private-id"), Is.Null);
    }

    [Test]
    public async Task Disconnect_ForgetsTheCookie()
    {
        var web = new FakeSpotifyWeb();
        var provider = ProviderWith(web);
        await provider.SaveSettingAsync("cookie", "cookie");
        await provider.SetEnabledAsync(true);

        await provider.LogoutAsync();

        Assert.That(await provider.IsConnectedAsync(), Is.False);
        Assert.That(await provider.GetSettingAsync("cookie"), Is.Null,
            "disconnecting removes the stored credential");
    }

    [Test]
    public void PasswordLogin_IsNotPartOfThisFlow()
    {
        var provider = ProviderWith(new FakeSpotifyWeb());

        Assert.ThrowsAsync<InvalidOperationException>(() => provider.BeginLoginAsync("http://x/cb"));
        Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteLoginAsync("?code=x", "http://x/cb"));
    }

    // ── the store path: liked songs and private playlists ───────────────

    /// <summary>The store with the unofficial session as the only account.</summary>
    private PlaylistStore StoreWith(
        SpotifyUnofficialAccountProvider account, ISourceProvider[]? sources = null)
        => StoreWith(new IAccountSourceProvider[] { account }, sources);

    private PlaylistStore StoreWith(
        IAccountSourceProvider[] accounts, ISourceProvider[]? sources = null)
        => new(_dbFactory!,
            sources ?? Array.Empty<ISourceProvider>(),
            new MelodyBridge.Application.Services.DownloadManager(
                new EmptyRegistry(),
                NullLogger<MelodyBridge.Application.Services.DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance,
            accounts);

    [Test]
    public async Task LikedImport_ThroughTheStore_PersistsAndUpdates()
    {
        var web = new FakeSpotifyWeb { LikedTotal = 3 };
        var provider = ProviderWith(web);
        await provider.SaveSettingAsync("cookie", "cookie");
        await provider.SetEnabledAsync(true);
        var store = StoreWith(provider);

        var added = await store.AddOrRefreshLikedAsync(SpotifyUnofficialAccountProvider.ProviderName);
        Assert.That(added.SourcePlatform, Is.EqualTo(Platform.Spotify));

        await using (var db = _dbFactory!.CreateDbContext())
        {
            var saved = await db.Playlists
                .Include(p => p.Tracks)
                .SingleAsync(p => p.ExternalId == SpotifyUnofficialAccountProvider.LikedId);
            Assert.That(saved.SourceUrl, Is.EqualTo(SpotifyUnofficialAccountProvider.LikedSourceUrl));
            Assert.That(saved.Tracks, Has.Count.EqualTo(3));
            Assert.That(saved.Tracks.All(t => t.IsLiked), Is.True,
                "liked songs stay flagged for the media-server favorites");
            Assert.That(saved.IsManualImport, Is.False,
                "an account import is not a file snapshot");
        }

        await store.AddOrRefreshLikedAsync(SpotifyUnofficialAccountProvider.ProviderName);
        await using (var db = _dbFactory!.CreateDbContext())
        {
            var count = await db.Playlists.CountAsync(
                p => p.ExternalId == SpotifyUnofficialAccountProvider.LikedId);
            Assert.That(count, Is.EqualTo(1), "re-import updates, never duplicates");
        }
    }

    [Test]
    public async Task PrivatePlaylistImport_PrefersTheSessionOverPublicScraping()
    {
        var web = new FakeSpotifyWeb();
        var provider = ProviderWith(web);
        await provider.SaveSettingAsync("cookie", "cookie");
        await provider.SetEnabledAsync(true);

        // The public path answers nothing: if the import succeeds, the
        // session served it, which is what makes private playlists work.
        var unreachable = new HttpClient(new NotFoundHandler());
        var publicSpotify = new MelodyBridge.Infrastructure.Services.SpotifySourceProvider(
            NullLogger<MelodyBridge.Infrastructure.Services.SpotifySourceProvider>.Instance,
            null, unreachable);

        // The official connection is registered too and comes first, but it
        // has no tokens: the store must skip it and try the session.
        var official = new SpotifyAccountProvider(
            _tokens, NullLogger<SpotifyAccountProvider>.Instance);
        var store = StoreWith(
            new IAccountSourceProvider[] { official, provider },
            new ISourceProvider[] { publicSpotify });

        var saved = await store.AddOrRefreshAsync(
            "https://open.spotify.com/playlist/55V41RYWVdALRiiN1onkUr");

        Assert.That(saved.Name, Is.EqualTo("Techno"));
        Assert.That(saved.SourcePlatform, Is.EqualTo(Platform.Spotify));
        Assert.That(saved.TrackCount, Is.EqualTo(1));

        await using var db = _dbFactory!.CreateDbContext();
        var tracks = await db.Tracks.Where(t => t.PlaylistEntityId == saved.Id).ToListAsync();
        Assert.That(tracks, Has.Count.EqualTo(1));
        Assert.That(tracks[0].ExternalId, Is.EqualTo("track-0"));
    }

    /// <summary>Answers 404 everywhere: the public scrape cannot help.</summary>
    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class EmptyRegistry : IDownloaderRegistry
    {
        public IReadOnlyList<IDownloader> GetAll() => Array.Empty<IDownloader>();
        public IDownloader? Get(string id) => null;
        public IReadOnlyList<IDownloader> GetEnabled() => Array.Empty<IDownloader>();
        public Task SetEnabledAsync(string id, bool enabled) => Task.CompletedTask;
        public bool IsEnabled(string id) => false;
        public Task<int> GetPriorityAsync(string id, CancellationToken ct = default) => Task.FromResult(0);
        public Task SetPriorityAsync(string id, int priority, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetConfigAsync(string id, string key, CancellationToken ct = default) => Task.FromResult("");
        public Task SetConfigAsync(string id, string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    // ── fake transport ──────────────────────────────────────────────────

    /// <summary>
    /// Answers like Spotify's web endpoints: the secret list, the server
    /// clock, the token endpoint and the three GraphQL queries. The payloads
    /// are trimmed copies of live answers.
    /// </summary>
    private sealed class FakeSpotifyWeb : HttpMessageHandler
    {
        public int LikedTotal { get; set; } = 2;
        public int LibraryTotal { get; set; } = 2;
        public int PlaylistTotal { get; set; } = 1;

        /// <summary>Clamps what a liked-songs page returns, to mimic a tight API.</summary>
        public int LikedPageCap { get; set; }

        /// <summary>Stops serving liked songs after this many, leaving a short answer.</summary>
        public int LikedStopAfter { get; set; } = int.MaxValue;

        /// <summary>Stops serving playlist items after this many.</summary>
        public int PlaylistStopAfter { get; set; } = int.MaxValue;

        /// <summary>Answers the first liked-songs query with a 429.</summary>
        public bool RateLimitOnce { get; set; }

        public bool Anonymous { get; set; }
        public bool BreakPlaylistQuery { get; set; }
        public List<(string Url, string? Cookie)> TokenRequests { get; } = new();

        /// <summary>Offset and limit of every liked-songs page request.</summary>
        public List<(int Offset, int Limit)> PageRequests { get; } = new();

        /// <summary>Offset and limit of every library page request.</summary>
        public List<(int Offset, int Limit)> LibraryRequests { get; } = new();

        /// <summary>Offset and limit of every playlist page request.</summary>
        public List<(int Offset, int Limit)> PlaylistPageRequests { get; } = new();

        /// <summary>Every liked-songs query, including rate-limited retries.</summary>
        public int LikedAttempts { get; private set; }

        private bool _rateLimited;

        public HttpClient Client => new(this) { Timeout = TimeSpan.FromSeconds(10) };

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;

            if (uri.Host == "raw.githubusercontent.com")
            {
                return await Json("""
                [
                  { "version": 3, "secret": [1, 2, 3] },
                  { "version": 61, "secret": [9, 8, 7, 6, 5] }
                ]
                """);
            }

            if (request.Method == HttpMethod.Head && uri.Host == "open.spotify.com")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Date = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
                return response;
            }

            if (uri.AbsolutePath == "/api/token")
            {
                TokenRequests.Add((uri.ToString(),
                    request.Headers.TryGetValues("Cookie", out var cookies)
                        ? string.Join(";", cookies)
                        : null));

                return await Json(Anonymous
                    ? """{ "accessToken": "anon", "accessTokenExpirationTimestampMs": 0, "isAnonymous": true }"""
                    : """{ "accessToken": "session-token", "accessTokenExpirationTimestampMs": 9999999999999, "isAnonymous": false }""");
            }

            if (uri.Host == "api-partner.spotify.com")
            {
                var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var operation = doc.RootElement.GetProperty("operationName").GetString();
                var variables = doc.RootElement.TryGetProperty("variables", out var v)
                    ? v
                    : default;
                var offset = variables.ValueKind == JsonValueKind.Object
                    && variables.TryGetProperty("offset", out var o)
                        ? o.GetInt32()
                        : 0;
                var limit = variables.ValueKind == JsonValueKind.Object
                    && variables.TryGetProperty("limit", out var l)
                        ? l.GetInt32()
                        : 50;
                if (operation == "fetchLibraryTracks")
                {
                    PageRequests.Add((offset, limit));
                    LikedAttempts++;
                }
                if (operation == "fetchPlaylist") PlaylistPageRequests.Add((offset, limit));
                if (operation == "libraryV3") LibraryRequests.Add((offset, limit));

                // One rate-limited attempt, with a Retry-After the provider
                // has to honor (zero, so the test does not wait).
                if (RateLimitOnce && !_rateLimited && operation == "fetchLibraryTracks")
                {
                    _rateLimited = true;
                    var limited = new HttpResponseMessage((HttpStatusCode)429);
                    limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                    return limited;
                }

                if (!request.Headers.Authorization?.Parameter?.Equals("session-token") ?? true)
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);

                return operation switch
                {
                    "fetchLibraryTracks" => await Json(LikedTracksResponse(
                        LikedTotal, offset, limit, LikedPageCap, LikedStopAfter)),
                    "libraryV3" => await Json(LibraryResponse(LibraryTotal, offset, limit)),
                    "fetchPlaylist" => BreakPlaylistQuery
                        ? await Json("""{ "errors": [ { "message": "not found" } ] }""")
                        : await Json(PlaylistResponse(PlaylistTotal, offset, limit, PlaylistStopAfter)),
                    _ => new HttpResponseMessage(HttpStatusCode.BadRequest),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static string LikedTracksResponse(
            int total, int offset, int limit, int pageCap, int stopAfter)
        {
            var pageLimit = pageCap > 0 ? Math.Min(limit, pageCap) : limit;
            var start = Math.Min(offset, stopAfter);
            var end = Math.Min(Math.Min(offset + pageLimit, total), stopAfter);
            var count = Math.Max(0, end - start);
            var items = string.Join(",", Enumerable.Range(start, count).Select(i => $$"""
              { "addedAt": { "isoString": "2026-04-16T13:29:22Z" },
                "track": { "_uri": "spotify:track:track-{{i}}",
                           "data": { "__typename": "Track", "name": "Daar Gaat Ze {{i}}",
                                     "duration": { "totalMilliseconds": 229466 },
                                     "albumOfTrack": { "name": "Hoezo?",
                                                       "artists": { "items": [ { "profile": { "name": "Clouseau" } } ] },
                                                       "coverArt": { "sources": [ { "url": "https://i.scdn.co/image/640", "width": 640, "height": 640 } ] } },
                                     "artists": { "items": [ { "profile": { "name": "Clouseau" } } ] } } } }
            """));

            return $$"""
            {
              "data": { "me": { "library": { "tracks": {
                "totalCount": {{total}},
                "items": [ {{items}} ]
              } } } }
            }
            """;
        }

        private static string LibraryResponse(int total, int offset, int limit)
        {
            var count = Math.Clamp(total - offset, 0, limit);
            var items = string.Join(",", Enumerable.Range(offset, count).Select(i => i switch
            {
                0 => """{ "item": { "_uri": "spotify:collection:tracks", "data": { "name": "Liked Songs", "count": 1844 } } }""",
                1 => """{ "item": { "_uri": "spotify:playlist:55V41RYWVdALRiiN1onkUr", "data": { "name": "Techno", "description": "hard and fast", "count": 104, "ownerV2": { "data": { "name": "Warre", "username": "warre" } }, "images": { "items": [ { "sources": [ { "url": "https://i.scdn.co/image/playlist64", "width": 64, "height": 64 }, { "url": "https://i.scdn.co/image/playlist640", "width": 640, "height": 640 } ] } ] } } } }""",
                _ => $$"""{ "item": { "_uri": "spotify:playlist:playlist-{{i}}", "data": { "name": "Playlist {{i}}", "count": {{i}} } } }""",
            }));

            return $$"""
            {
              "data": { "me": { "libraryV3": {
                "totalCount": {{total}},
                "items": [ {{items}} ]
              } } }
            }
            """;
        }

        private static string PlaylistResponse(int total, int offset, int limit, int stopAfter)
        {
            var start = Math.Min(offset, stopAfter);
            var end = Math.Min(Math.Min(offset + limit, total), stopAfter);
            var count = Math.Max(0, end - start);
            var items = string.Join(",", Enumerable.Range(start, count).Select(i => $$"""
              { "itemV2": { "__typename": "TrackResponseWrapper",
                            "data": { "__typename": "Track", "name": "Bonzai Channel One {{i}}",
                                      "uri": "spotify:track:track-{{i}}",
                                      "trackDuration": { "totalMilliseconds": 251793 },
                                      "albumOfTrack": { "name": "Bonzai Channel One",
                                                        "artists": { "items": [ { "profile": { "name": "Thunderball" } } ] },
                                                        "coverArt": { "sources": [ { "url": "https://i.scdn.co/image/track640", "width": 640, "height": 640 } ] } },
                                      "artists": { "items": [ { "profile": { "name": "Thunderball" } } ] } } } }
            """));

            return $$"""
            {
              "data": { "playlistV2": {
                "name": "Techno", "description": "hard and fast", "uri": "spotify:playlist:55V41RYWVdALRiiN1onkUr",
                "ownerV2": { "data": { "name": "Warre", "username": "warre" } },
                "images": { "items": [ { "sources": [
                  { "url": "https://i.scdn.co/image/playlist64", "width": 64, "height": 64 },
                  { "url": "https://i.scdn.co/image/playlist640", "width": 640, "height": 640 } ] } ] },
                "content": { "totalCount": {{total}}, "items": [ {{items}} ] }
              } }
            }
            """;
        }

        private static Task<HttpResponseMessage> Json(string body)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private const string LikedTrackItem = """
    {
      "__typename": "UserLibraryTrackResponse",
      "addedAt": { "isoString": "2026-04-16T13:29:22Z" },
      "track": {
        "_uri": "spotify:track:2vFzC766RC2m36ywFyj09S",
        "data": {
          "__typename": "Track",
          "albumOfTrack": {
            "artists": { "items": [ { "profile": { "name": "Clouseau" } } ] },
            "coverArt": { "sources": [
              { "url": "https://i.scdn.co/image/64", "width": 64, "height": 64 },
              { "url": "https://i.scdn.co/image/640", "width": 640, "height": 640 } ] },
            "name": "Hoezo?"
          },
          "artists": { "items": [ { "profile": { "name": "Clouseau" } } ] },
          "duration": { "totalMilliseconds": 229466 },
          "name": "Daar Gaat Ze",
          "trackNumber": 4
        }
      }
    }
    """;

    private const string PlaylistItem = """
    {
      "addedAt": { "isoString": "2026-03-24T14:54:08.711Z" },
      "itemV2": {
        "__typename": "TrackResponseWrapper",
        "data": {
          "__typename": "Track",
          "albumOfTrack": { "name": "Bonzai Channel One" },
          "trackDuration": { "totalMilliseconds": 251793 },
          "name": "Bonzai Channel One - Original Remastered Mix",
          "uri": "spotify:track:1pdoK6VJe8wwp86SrfByx0"
        }
      },
      "uid": "06477eae51c12945"
    }
    """;

    private const string LibraryPlaylistItem = """
    {
      "item": {
        "_uri": "spotify:playlist:55V41RYWVdALRiiN1onkUr",
        "data": {
          "__typename": "Playlist",
          "name": "Techno",
          "count": 104,
          "ownerV2": { "data": { "name": "Warre", "username": "warre" } },
          "images": { "items": [ { "sources": [
            { "url": "https://i.scdn.co/image/playlist64", "width": 64, "height": 64 },
            { "url": "https://i.scdn.co/image/playlist640", "width": 640, "height": 640 } ] } ] }
        }
      }
    }
    """;
}
