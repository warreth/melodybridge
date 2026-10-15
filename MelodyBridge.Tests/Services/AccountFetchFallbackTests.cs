using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Accounts;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The account-first fetch path: a connected account that returns nothing
/// or fails must never fail the sync, the public source provider takes
/// over. Real SQLite, no mocks on the persistence path.
/// </summary>
[TestFixture]
public class AccountFetchFallbackTests
{
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-acct-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private sealed class FakeSourceProvider : ISourceProvider
    {
        public string Name => "Spotify";
        public Platform Platform => Platform.Spotify;
        public int Calls;

        public bool CanHandle(string sourceIdentifier) => true;

        public Task<Playlist> GetPlaylistAsync(string sourceIdentifier)
        {
            Calls++;
            return Task.FromResult(new Playlist
            {
                Id = "from-public",
                Name = "Public result",
                Tracks = new List<Track>
                {
                    new()
                    {
                        Title = "Song",
                        Artist = "Artist",
                        Duration = TimeSpan.FromSeconds(200),
                        SongID = new SongID(Platform.Spotify, "t1"),
                        PlatformSongID = new SongID(Platform.Spotify, "t1"),
                        SourcePlatform = Platform.Spotify,
                    },
                },
                TrackCount = 1,
            });
        }

        public Task<string?> ResolveTrackUrlAsync(string query) => Task.FromResult<string?>(null);
    }

    /// <summary>Connected account that returns nothing or throws on demand.</summary>
    private sealed class FakeAccount(AccountTokenStore tokens, bool fail)
        : SpotifyAccountProvider(tokens, NullLogger<SpotifyAccountProvider>.Instance)
    {
        public override Task<bool> IsConnectedAsync(CancellationToken ct = default)
            => Task.FromResult(true);

        public override Task<Playlist?> TryGetPlaylistViaAccountAsync(
            string playlistId, CancellationToken ct = default)
            => fail
                ? throw new InvalidOperationException("account fetch boom")
                : Task.FromResult<Playlist?>(null);
    }

    private PlaylistStore Store(FakeSourceProvider source, bool accountFails)
    {
        var tokens = new AccountTokenStore(_factory, NullLogger<AccountTokenStore>.Instance);
        return new PlaylistStore(_factory,
            new ISourceProvider[] { source },
            new DownloadManager(new EmptyDownloaderRegistry(), NullLogger<DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance,
            accountProviders: new IAccountSourceProvider[] { new FakeAccount(tokens, accountFails) });
    }

    [Test]
    public async Task AccountReturnsNothing_PublicProviderTakesOver()
    {
        var source = new FakeSourceProvider();
        var playlist = await Store(source, accountFails: false)
            .AddOrRefreshAsync("https://open.spotify.com/playlist/abc");

        Assert.That(source.Calls, Is.EqualTo(1), "the public path fetched the playlist");
        Assert.That(playlist.Name, Is.EqualTo("Public result"));
        Assert.That(playlist.ExternalId, Is.EqualTo("from-public"));
    }

    [Test]
    public async Task AccountThrows_PublicProviderTakesOver()
    {
        var source = new FakeSourceProvider();
        var playlist = await Store(source, accountFails: true)
            .AddOrRefreshAsync("https://open.spotify.com/playlist/abc");

        Assert.That(source.Calls, Is.EqualTo(1),
            "an account failure falls back instead of failing the sync");
        Assert.That(playlist.Name, Is.EqualTo("Public result"));
    }
}
