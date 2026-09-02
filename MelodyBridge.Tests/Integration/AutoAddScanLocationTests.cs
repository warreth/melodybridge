using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// New playlist folders must become Library scan locations automatically,
/// exactly once per folder (case-insensitive dedupe), with the manual
/// defaults. Real SQLite, real PlaylistStore, stub source provider.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class AutoAddScanLocationTests
{
    private static string NewDbPath([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), $"mb-scanloc-{test}-{Guid.NewGuid():N}.db");

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
        IDbContextFactory<MelodyBridgeDbContext> factory, ISourceProvider provider)
        => new(factory,
            new[] { provider },
            new Application.Services.DownloadManager(
                new EmptyRegistryStub(),
                NullLogger<Application.Services.DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance);

    /// <summary>Stub provider serving a stable one-track playlist.</summary>
    private sealed class StubSourceProvider : ISourceProvider
    {
        public string Name => "Stub";
        public Platform Platform => Platform.Spotify;
        public bool CanHandle(string sourceIdentifier) => sourceIdentifier.Contains("stub:");

        public Task<Playlist> GetPlaylistAsync(string sourceIdentifier)
            => Task.FromResult(new Playlist
            {
                Id = "stub-playlist",
                Name = "Stub Playlist",
                Tracks = new List<Track>
                {
                    new()
                    {
                        Title = "Song",
                        Artist = "Artist",
                        SongID = new SongID(Platform.Spotify, "t1"),
                        PlatformSongID = new SongID(Platform.Spotify, "t1"),
                    },
                },
            });

        public Task<string?> ResolveTrackUrlAsync(string query) => Task.FromResult<string?>(null);
    }

    private sealed class EmptyRegistryStub : IDownloaderRegistry
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

    private static async Task<List<string>> LocationsAsync(IDbContextFactory<MelodyBridgeDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.ScanLocations.AsNoTracking().Select(l => l.Path).ToListAsync();
    }

    private static void TryDelete(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { System.IO.File.Delete(dbPath + suffix); } catch { }
    }

    [Test]
    public async Task NewPlaylist_FolderBecomesScanLocation_Once()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new StubSourceProvider());
            var folder = Path.Combine(Path.GetTempPath(), $"mb-scanloc-a-{Guid.NewGuid():N}");

            await store.AddOrRefreshAsync("stub:pl", targetDirectory: folder);

            var locations = await LocationsAsync(factory);
            Assert.That(locations, Is.EqualTo(new[] { folder }),
                "the playlist folder must appear as a scan location exactly once");
        }
        finally { TryDelete(dbPath); }
    }

    [Test]
    public async Task Resync_UpdatedFolder_StillOneRow()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new StubSourceProvider());
            var folderA = Path.Combine(Path.GetTempPath(), $"mb-scanloc-b-{Guid.NewGuid():N}");
            var folderB = Path.Combine(Path.GetTempPath(), $"mb-scanloc-c-{Guid.NewGuid():N}");

            var first = await store.AddOrRefreshAsync("stub:pl", targetDirectory: folderA);
            // Update the folder through the refresh path with a new target.
            await store.RefreshAsync(first.Id, folderB);

            // Only NEW playlist folders auto-add: a re-sync must not create
            // a second row (folder changes stay a Library-page concern).
            var locations = await LocationsAsync(factory);
            Assert.That(locations, Is.EqualTo(new[] { folderA }),
                "a re-sync with an updated folder must not add scan locations");
        }
        finally { TryDelete(dbPath); }
    }

    [Test]
    public async Task SecondPlaylist_SameFolder_StillOneLocation()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new StubSourceProvider());
            var shared = Path.Combine(Path.GetTempPath(), $"mb-scanloc-d-{Guid.NewGuid():N}");

            await store.AddOrRefreshAsync("stub:pl", targetDirectory: shared);
            // A second playlist needs a second provider identity: point the
            // stub at another source identifier serving a different id.
            var other = new OtherStubProvider();
            var store2 = NewStore(factory, other);
            await store2.AddOrRefreshAsync("stub:pl2", targetDirectory: shared);

            var locations = await LocationsAsync(factory);
            Assert.That(locations.Count(l => l.Equals(shared, StringComparison.OrdinalIgnoreCase)), Is.EqualTo(1),
                "two playlists sharing a folder must yield exactly one location");
        }
        finally { TryDelete(dbPath); }
    }

    [Test]
    public async Task CaseInsensitiveDedupe_NoSecondRow()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new StubSourceProvider());
            var folder = Path.Combine(Path.GetTempPath(), $"mb-scanloc-e-{Guid.NewGuid():N}");

            await store.AddOrRefreshAsync("stub:pl", targetDirectory: folder);
            // Pre-seed a differently-cased duplicate path directly, then add
            // another playlist with the original casing: must not add a row.
            var other = new OtherStubProvider();
            await NewStore(factory, other).AddOrRefreshAsync("stub:pl2", targetDirectory: folder.ToUpperInvariant());

            var locations = await LocationsAsync(factory);
            Assert.That(locations.Count, Is.EqualTo(1),
                "the same folder in different casing must not create a second location");
        }
        finally { TryDelete(dbPath); }
    }

    [Test]
    public async Task NewPlaylist_MusicPathPrefill_AlsoBecomesLocation()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var musicDir = Path.Combine(Path.GetTempPath(), $"mb-scanloc-f-{Guid.NewGuid():N}");
            await new SettingsStore(factory).SetAsync("music_path", musicDir);
            var store = NewStore(factory, new StubSourceProvider());

            // No explicit folder: SaveSnapshotAsync prefills music_path, which
            // must then become a scan location too.
            await store.AddOrRefreshAsync("stub:pl");

            var locations = await LocationsAsync(factory);
            Assert.That(locations, Is.EqualTo(new[] { musicDir }),
                "the prefilled music_path must appear as a scan location");
        }
        finally { TryDelete(dbPath); }
    }

    /// <summary>Same stub, different playlist id, so the second save creates a new playlist row.</summary>
    private sealed class OtherStubProvider : ISourceProvider
    {
        public string Name => "Stub";
        public Platform Platform => Platform.Spotify;
        public bool CanHandle(string sourceIdentifier) => sourceIdentifier.Contains("stub:");

        public Task<Playlist> GetPlaylistAsync(string sourceIdentifier)
            => Task.FromResult(new Playlist
            {
                Id = "stub-playlist-2",
                Name = "Stub Playlist 2",
                Tracks = new List<Track>(),
            });

        public Task<string?> ResolveTrackUrlAsync(string query) => Task.FromResult<string?>(null);
    }
}
