using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Playlist card data through a real store and a real SQLite file:
/// download stats (counts + bytes on disk, downloaded files only) and
/// the saved-playlist lookup the import table marks duplicates with.
/// The stub provider only feeds playlist snapshots; every assertion
/// reads back through a fresh DbContext.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class PlaylistStoreStatsTests
{
    private static string NewDbPath([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), $"mb-stats-{test}-{Guid.NewGuid():N}.db");

    private static async Task<IDbContextFactory<MelodyBridgeDbContext>> NewDbFactory(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<MelodyBridgeDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        return factory;
    }

    private static PlaylistStore NewStore(
        IDbContextFactory<MelodyBridgeDbContext> factory, params ISourceProvider[] providers)
        => new(factory, providers,
            new Application.Services.DownloadManager(
                new EmptyRegistry(),
                NullLogger<Application.Services.DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance);

    private sealed class StubSourceProvider : ISourceProvider
    {
        public string Name => "Stub";
        public Platform Platform => Platform.Spotify;

        public bool CanHandle(string sourceIdentifier)
            => sourceIdentifier.Contains("stub:", StringComparison.Ordinal);

        public Task<Playlist> GetPlaylistAsync(string sourceIdentifier)
            => Task.FromResult(new Playlist
            {
                Id = "stub-playlist",
                Name = "Stub Playlist",
                Tracks = new List<Track>
                {
                    new()
                    {
                        Title = "One", SongID = new SongID(Platform.Spotify, "t1"),
                        PlatformSongID = new SongID(Platform.Spotify, "t1"),
                    },
                    new()
                    {
                        Title = "Two", SongID = new SongID(Platform.Spotify, "t2"),
                        PlatformSongID = new SongID(Platform.Spotify, "t2"),
                    },
                },
            });

        public Task<string?> ResolveTrackUrlAsync(string query) => Task.FromResult<string?>(null);
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

    [Test]
    public async Task DownloadStats_CountAndBytes_OnlyDownloadedTracks()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new StubSourceProvider());
            var (playlist, newlySaved) = await store.AddOrRefreshDetailedAsync("stub:playlist");
            Assert.That(newlySaved, Is.True, "first add reports a new playlist");

            // Realistic disk state: one file downloaded with a size, one
            // failed with a partial size, one still pending with none.
            await using (var db = await factory.CreateDbContextAsync())
            {
                var tracks = db.Tracks.ToList();
                tracks.First(t => t.ExternalId == "t1").DownloadStatus = "downloaded";
                tracks.First(t => t.ExternalId == "t1").FileSizeBytes = 10_000_000;
                tracks.First(t => t.ExternalId == "t2").DownloadStatus = "failed";
                tracks.First(t => t.ExternalId == "t2").FileSizeBytes = 123;
                await db.SaveChangesAsync();
            }

            var stats = await store.GetDownloadStatsAsync();
            var (downloaded, failed, total, sizeBytes) = stats[playlist.Id];

            Assert.That(downloaded, Is.EqualTo(1));
            Assert.That(failed, Is.EqualTo(1));
            Assert.That(total, Is.EqualTo(2));
            Assert.That(sizeBytes, Is.EqualTo(10_000_000),
                "the failed file's bytes must not count as size on disk");
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task DetailedAdd_SecondImport_ReportsUpdateNotNew()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new StubSourceProvider());

            var (first, firstNew) = await store.AddOrRefreshDetailedAsync("stub:playlist");
            var (second, secondNew) = await store.AddOrRefreshDetailedAsync("stub:playlist");

            Assert.That(firstNew, Is.True);
            Assert.That(secondNew, Is.False,
                "the second add must say it updated, not saved - the add form says so");
            Assert.That(second.Id, Is.EqualTo(first.Id),
                "the same playlist row is reused, not duplicated");

            var saved = await store.GetSavedExternalIdsAsync(Platform.Spotify);
            Assert.That(saved.Contains("stub-playlist"), Is.True);
            Assert.That(saved.Count, Is.EqualTo(1), "one playlist in, one external id known");
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task SavedExternalIds_ArePerPlatform()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new StubSourceProvider());
            await store.AddOrRefreshAsync("stub:playlist");

            var spotifyIds = await store.GetSavedExternalIdsAsync(Platform.Spotify);
            var youtubeIds = await store.GetSavedExternalIdsAsync(Platform.YouTubeMusic);

            Assert.That(spotifyIds.Contains("stub-playlist"), Is.True);
            Assert.That(youtubeIds.Count, Is.EqualTo(0),
                "a Spotify playlist must not mark a YouTube import as saved");
        }
        finally
        {
            File.Delete(dbPath);
        }
    }
}
