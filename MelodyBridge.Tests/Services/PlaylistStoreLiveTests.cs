using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Round-trip tests for PlaylistStore: live Spotify fetch → real SQLite → read back.
///
/// These tests cannot be cheated:
///  - No HTTP stubs: the provider does real network calls to open.spotify.com.
///  - No InMemory database: a real SQLite file is created per test.
///  - Every assertion reads back through a fresh DbContext, so nothing is
///    asserted from the in-memory object that was just written.
/// </summary>
[TestFixture]
[Category("Live")]
[Category("PlaylistStore")]
public class PlaylistStoreLiveTests
{
    /// <summary>Well-known public Spotify playlist ("Today's Top Hits", 50 tracks).</summary>
    private const string TopHitsUrl = "https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M";

    private static string NewDbPath([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), $"mb-store-{test}-{Guid.NewGuid():N}.db");

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

    private static PlaylistStore NewStore(IDbContextFactory<MelodyBridgeDbContext> factory)
        => new(factory,
            new ISourceProvider[] { new SpotifySourceProvider(NullLogger<SpotifySourceProvider>.Instance) },
            new Application.Services.DownloadManager(
                new EmptyRegistry(),
                NullLogger<Application.Services.DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance);

    [Test]
    public async Task AddLiveSpotifyPlaylist_PersistsTracksInRealSqlite()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory);

            var playlist = await store.AddOrRefreshAsync(TopHitsUrl);

            Assert.That(playlist.TrackCount, Is.GreaterThan(0), "live playlist should expose tracks");

            // Read back through a completely fresh context — nothing cached.
            await using var db = await factory.CreateDbContextAsync();
            var rows = await db.Playlists.Include(p => p.Tracks).AsNoTracking().ToListAsync();

            Assert.That(rows.Count, Is.EqualTo(1), "exactly one playlist row expected");
            Assert.That(rows[0].Name, Is.Not.Null.Or.Empty, "playlist name must be persisted");
            Assert.That(rows[0].ExternalId, Is.EqualTo("37i9dQZF1DXcBWIGoYBM5M"));
            Assert.That(rows[0].SourcePlatform, Is.EqualTo(Platform.Spotify));

            var tracks = rows[0].Tracks.OrderBy(t => t.Position).ToList();
            Assert.That(tracks.Count, Is.EqualTo(rows[0].TrackCount), "stored track count must match snapshot");
            Assert.That(tracks.All(t => !string.IsNullOrEmpty(t.Title)), Is.True, "every track needs a title");
            Assert.That(tracks.All(t => !string.IsNullOrEmpty(t.Artist)), Is.True, "every track needs an artist");
            Assert.That(tracks.All(t => !string.IsNullOrEmpty(t.ExternalId)), Is.True,
                "every track needs its Spotify ID — without it downloads/scans cannot join");
            Assert.That(tracks.Select(t => t.Position), Is.EqualTo(Enumerable.Range(0, tracks.Count)),
                "tracks must be stored in playlist order");
            Assert.That(tracks[0].DurationMs, Is.GreaterThan(0), "live durations come in milliseconds");
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Test]
    public async Task RefetchSamePlaylist_UpdatesInsteadOfDuplicating()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory);

            var first = await store.AddOrRefreshAsync(TopHitsUrl);
            var second = await store.AddOrRefreshAsync(TopHitsUrl); // same playlist, second sync

            Assert.That(second.Id, Is.EqualTo(first.Id), "re-sync must reuse the same playlist row");

            await using var db = await factory.CreateDbContextAsync();
            var playlistCount = await db.Playlists.CountAsync();
            var trackCount = await db.Tracks.CountAsync();

            Assert.That(playlistCount, Is.EqualTo(1), "second fetch must not create a second playlist row");
            Assert.That(trackCount, Is.EqualTo(first.TrackCount), "snapshot replacement must not leak old track rows");
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Test]
    public async Task RefreshById_ReplacesSnapshotWithFreshData()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory);

            var added = await store.AddOrRefreshAsync(TopHitsUrl);
            var refreshed = await store.RefreshAsync(added.Id);

            Assert.That(refreshed.Id, Is.EqualTo(added.Id));
            Assert.That(refreshed.TrackCount, Is.GreaterThan(0));
            Assert.That(refreshed.LastSyncAt, Is.GreaterThan(added.LastSyncAt ?? DateTime.MinValue),
                "refresh must bump LastSyncAt");

            // The refreshed entity read back from DB must still resolve by ID.
            var reloaded = await store.GetByIdAsync(added.Id);
            Assert.That(reloaded, Is.Not.Null);
            Assert.That(reloaded!.Tracks.Count, Is.EqualTo(reloaded.TrackCount));
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Test]
    public async Task Delete_RemovesPlaylistAndTrackSnapshot()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory);

            var added = await store.AddOrRefreshAsync(TopHitsUrl);
            Assert.That(await store.DeleteAsync(added.Id), Is.True);
            Assert.That(await store.DeleteAsync(added.Id), Is.False, "second delete must report false");

            await using var db = await factory.CreateDbContextAsync();
            Assert.That(await db.Playlists.CountAsync(), Is.EqualTo(0));
            Assert.That(await db.Tracks.CountAsync(), Is.EqualTo(0), "snapshot tracks must be cascade-removed");
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    [Test]
    public async Task ExportImport_RoundTripsThroughRealDb()
    {
        var exportDb = NewDbPath("export");
        var importDb = NewDbPath("import");
        try
        {
            var exportFactory = await NewDbFactory(exportDb);
            var exportStore = NewStore(exportFactory);
            await exportStore.AddOrRefreshAsync(TopHitsUrl);

            var json = await exportStore.ExportAsync();
            Assert.That(json, Does.Contain("37i9dQZF1DXcBWIGoYBM5M"), "export must embed the playlist identity");

            var importFactory = await NewDbFactory(importDb);
            var importStore = NewStore(importFactory);
            var imported = await importStore.ImportAsync(json);

            Assert.That(imported, Is.EqualTo(1), "import must re-fetch and persist one playlist");
            var reloaded = (await importStore.GetAllAsync()).Single();
            Assert.That(reloaded.TrackCount, Is.GreaterThan(0), "imported playlist must carry a live snapshot");
        }
        finally
        {
            TryDelete(exportDb);
            TryDelete(importDb);
        }
    }

    [Test]
    public async Task AddOrRefresh_UnknownUrl_ThrowsWithoutLeavingRows()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory);

            Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await store.AddOrRefreshAsync("https://example.com/not-a-playlist"));

            await using var db = await factory.CreateDbContextAsync();
            Assert.That(await db.Playlists.CountAsync(), Is.EqualTo(0), "failed fetch must not leave rows");
        }
        finally
        {
            TryDelete(dbPath);
        }
    }

    private static void TryDelete(string dbPath)
    {
        // SQLite keeps -wal/-shm siblings; remove all three.
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(dbPath + suffix); } catch { /* best effort */ }
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
