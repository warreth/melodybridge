using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Accounts;
using MelodyBridge.Infrastructure.AppleMusic;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Accounts;

/// <summary>
/// The store-level routing for Apple Music: a library marker must use the
/// account, the liked marker must save under Platform.AppleMusic, and a
/// public URL must reach the account with the full link so its storefront
/// survives.
/// </summary>
[TestFixture]
[Category("Unit")]
public class AppleMusicStoreRoutingTests
{
    private string _root = null!;
    private TestSqliteFactory _factory = null!;
    private AccountTokenStore _tokens = null!;
    private FakeAppleAccount _account = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-apple-store-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _factory = new TestSqliteFactory(Path.Combine(_root, "store.db"));
        using (var db = _factory.CreateDbContext()) db.Database.EnsureCreated();
        _tokens = new AccountTokenStore(_factory, NullLogger<AccountTokenStore>.Instance);

        var tokenProvider = new AppleMusicWebTokenProvider(
            new HttpClient(), NullLogger<AppleMusicWebTokenProvider>.Instance);
        var api = new AppleMusicApiClient(
            new HttpClient(), tokenProvider, NullLogger<AppleMusicApiClient>.Instance);
        _account = new FakeAppleAccount(_tokens, api);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task LibraryMarker_SavesThroughTheAccount()
    {
        var store = Store(new FakeAppleSource());

        var saved = await store.AddOrRefreshAsync("applemusic:library:p.one");

        Assert.Multiple(() =>
        {
            Assert.That(saved.ExternalId, Is.EqualTo("pl.one"));
            Assert.That(saved.SourcePlatform, Is.EqualTo(Platform.AppleMusic));
            Assert.That(saved.Tracks, Has.Count.EqualTo(1));
        });
        Assert.That(_account.LastSource, Is.EqualTo("p.one"));
    }

    [Test]
    public async Task LikedMarker_SavesLibrarySongsWithoutFavoriteFlags()
    {
        var store = Store(new FakeAppleSource());

        var saved = await store.AddOrRefreshLikedAsync("Apple Music");

        Assert.Multiple(() =>
        {
            Assert.That(saved.ExternalId, Is.EqualTo(AppleMusicAccountProvider.LikedId));
            Assert.That(saved.SourcePlatform, Is.EqualTo(Platform.AppleMusic));
            Assert.That(saved.Tracks, Has.Count.EqualTo(1));
            Assert.That(saved.Tracks.All(t => !t.IsLiked), Is.True);
        });
    }

    [Test]
    public async Task PublicAppleLink_ReachesTheAccountWithTheFullStorefrontUrl()
    {
        var source = new FakeAppleSource();
        var store = Store(source);
        const string url = "https://music.apple.com/gb/playlist/name/pl.test";

        var saved = await store.AddOrRefreshAsync(url);

        Assert.That(_account.LastSource, Is.EqualTo(url),
            "the account fetch sees the full link, not a bare id");
        Assert.That(source.Calls, Is.Zero, "the connected account served the playlist");
        Assert.That(saved.ExternalId, Is.EqualTo("pl.test"));
    }

    private PlaylistStore Store(ISourceProvider source)
        => new(
            _factory,
            new[] { source },
            new DownloadManager(new EmptyDownloaderRegistry(), NullLogger<DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance,
            accountProviders: new IAccountSourceProvider[] { _account });

    private sealed class FakeAppleSource : ISourceProvider
    {
        public string Name => "Apple Music";
        public Platform Platform => Platform.AppleMusic;
        public int Calls { get; private set; }
        public bool CanHandle(string sourceIdentifier) => true;

        public Task<Playlist> GetPlaylistAsync(string sourceIdentifier)
        {
            Calls++;
            return Task.FromResult(PublicPlaylist());
        }
    }

    private sealed class FakeAppleAccount : AppleMusicAccountProvider
    {
        public FakeAppleAccount(AccountTokenStore settings, AppleMusicApiClient api)
            : base(settings, api, NullLogger<AppleMusicAccountProvider>.Instance) { }

        public string? LastSource { get; private set; }

        public override Task<bool> IsConnectedAsync(CancellationToken ct = default)
            => Task.FromResult(true);

        public override Task<Playlist?> TryGetPlaylistViaAccountAsync(
            string sourceIdentifier, CancellationToken ct = default)
        {
            LastSource = sourceIdentifier;
            var playlist = sourceIdentifier.StartsWith("p.", StringComparison.Ordinal)
                ? LibraryPlaylist()
                : PublicPlaylist();
            return Task.FromResult<Playlist?>(playlist);
        }

        public override Task<Playlist> GetLikedPlaylistAsync(CancellationToken ct = default)
            => Task.FromResult(new Playlist
            {
                Id = AppleMusicAccountProvider.LikedId,
                Name = "Library songs",
                SourceUrl = AppleMusicAccountProvider.LikedSourceUrl,
                Tracks = new List<Track>
                {
                    new()
                    {
                        Title = "Saved",
                        Artist = "Artist",
                        SongID = new SongID(Platform.AppleMusic, "saved"),
                        PlatformSongID = new SongID(Platform.AppleMusic, "saved"),
                        SourcePlatform = Platform.AppleMusic,
                        IsLiked = false,
                    },
                },
                TrackCount = 1,
            });
    }

    private static Playlist PublicPlaylist() => new()
    {
        Id = "pl.test",
        Name = "Public",
        SourceUrl = "https://music.apple.com/gb/playlist/name/pl.test",
        Tracks = new List<Track>
        {
            new()
            {
                Title = "Public Track",
                Artist = "Artist",
                SongID = new SongID(Platform.AppleMusic, "pub"),
                PlatformSongID = new SongID(Platform.AppleMusic, "pub"),
                SourcePlatform = Platform.AppleMusic,
            },
        },
        TrackCount = 1,
    };

    private static Playlist LibraryPlaylist() => new()
    {
        Id = "pl.one",
        Name = "Library",
        SourceUrl = "applemusic:library:p.one",
        Tracks = new List<Track>
        {
            new()
            {
                Title = "Library Track",
                Artist = "Artist",
                SongID = new SongID(Platform.AppleMusic, "lib"),
                PlatformSongID = new SongID(Platform.AppleMusic, "lib"),
                SourcePlatform = Platform.AppleMusic,
            },
        },
        TrackCount = 1,
    };
}
