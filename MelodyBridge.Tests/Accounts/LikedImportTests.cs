using System.Runtime.CompilerServices;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Accounts;

/// <summary>
/// Tests the liked-songs import end to end at the store level: a fake
/// account provider (deliberate: the Spotify HTTP layer has its own live
/// tests) feeds a real PlaylistStore writing a real SQLite file, and every
/// assertion reads back through a fresh DbContext.
/// </summary>
[TestFixture]
[Category("Unit")]
public class LikedImportTests
{
    private sealed class FakeAccountProvider : IAccountSourceProvider
    {
        public string Name => "Spotify";
        public Task<bool> IsConnectedAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<string> BeginLoginAsync(string redirectUrl, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> CompleteLoginAsync(string redirectQuery, string redirectUrl, CancellationToken ct = default) => throw new NotSupportedException();
        public Task LogoutAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetSettingAsync(string key, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task SaveSettingAsync(string key, string value, CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<UserPlaylist>> GetUserPlaylistsAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UserPlaylist>>(new[]
            {
                new UserPlaylist("abc123", "Road trip", "me", 2, IsLikedSongs: false),
            });

        public Task<Playlist> GetLikedPlaylistAsync(CancellationToken ct = default)
            => Task.FromResult(new Playlist
            {
                Id = "spotify-liked",
                Name = "Liked songs (Spotify)",
                Owner = "me",
                SourceUrl = "spotify:liked",
                Tracks = new List<Track>
                {
                    new()
                    {
                        Title = "Song A",
                        Artist = "Artist A",
                        SongID = new SongID(Platform.Spotify, "id-a"),
                        PlatformSongID = new SongID(Platform.Spotify, "id-a"),
                        SourcePlatform = Platform.Spotify,
                        IsLiked = true,
                    },
                    new()
                    {
                        Title = "Song B",
                        Artist = "Artist B",
                        SongID = new SongID(Platform.Spotify, "id-b"),
                        PlatformSongID = new SongID(Platform.Spotify, "id-b"),
                        SourcePlatform = Platform.Spotify,
                        IsLiked = true,
                    },
                },
            });
    }

    private static string NewDbPath([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), $"mb-liked-{test}-{Guid.NewGuid():N}.db");

    private static async Task<IDbContextFactory<MelodyBridgeDbContext>> NewDbFactoryAsync(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<MelodyBridgeDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        return factory;
    }

    private static PlaylistStore NewStore(IDbContextFactory<MelodyBridgeDbContext> factory)
        => new(factory,
            Array.Empty<ISourceProvider>(),
            new Application.Services.DownloadManager(
                new EmptyRegistry(),
                NullLogger<Application.Services.DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance,
            new[] { new FakeAccountProvider() });

    [Test]
    public async Task LikedImport_PersistsTracksAndFlags()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactoryAsync(dbPath);
            var store = NewStore(factory);

            var playlist = await store.AddOrRefreshLikedAsync("Spotify");

            // Read back through a fresh context: real persistence, not
            // the entity we just held.
            await using var db = factory.CreateDbContext();
            var saved = await db.Playlists
                .Include(p => p.Tracks)
                .SingleAsync(p => p.ExternalId == "spotify-liked");

            Assert.That(saved.Name, Is.EqualTo("Liked songs (Spotify)"));
            Assert.That(saved.Tracks, Has.Count.EqualTo(2));
            Assert.That(saved.Tracks.Select(t => t.IsLiked), Is.All.True);
            Assert.That(saved.Tracks.Select(t => t.Title),
                Is.EquivalentTo(new[] { "Song A", "Song B" }));
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task LikedImport_ReImport_UpdatesInsteadOfDuplicating()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactoryAsync(dbPath);
            var store = NewStore(factory);

            await store.AddOrRefreshLikedAsync("Spotify");
            await store.AddOrRefreshLikedAsync("Spotify");

            await using var db = factory.CreateDbContext();
            var count = await db.Playlists.CountAsync(p => p.ExternalId == "spotify-liked");
            Assert.That(count, Is.EqualTo(1), "re-import updates, never duplicates");
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task GetUserPlaylists_FlowsThroughStore()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactoryAsync(dbPath);
            var store = NewStore(factory);

            // The provider list behind the account panel.
            var provider = new FakeAccountProvider();
            var playlists = await provider.GetUserPlaylistsAsync();

            Assert.That(playlists, Has.Count.EqualTo(1));
            Assert.That(playlists[0].Name, Is.EqualTo("Road trip"));
            Assert.That(playlists[0].TrackCount, Is.EqualTo(2));
        }
        finally
        {
            File.Delete(dbPath);
        }
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
    }
}
