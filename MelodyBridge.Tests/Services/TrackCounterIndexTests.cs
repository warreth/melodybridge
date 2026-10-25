using System.Runtime.CompilerServices;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The index the per-playlist counters read, end to end on a real SQLite
/// file: a fresh install gets it from the model, an older database gets it
/// from the patcher on the next start, and the counters keep reading the
/// same numbers with it in place.
/// </summary>
[TestFixture]
public class TrackCounterIndexTests
{
    private const string IndexName = "IX_Tracks_PlaylistEntityId_DownloadStatus_FileSizeBytes_ReviewKind";

    private static string NewDbPath([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), $"mb-counters-index-{test}-{Guid.NewGuid():N}.db");

    private static DbContextOptions<MelodyBridgeDbContext> Options(string path)
        => new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;

    [Test]
    public async Task AFreshDatabase_HasTheCounterIndex()
    {
        var path = NewDbPath();
        try
        {
            await using (var db = new MelodyBridgeDbContext(Options(path)))
                await db.Database.EnsureCreatedAsync();

            Assert.That(await IndexSqlAsync(path), Is.Not.Null,
                "a new install must have the index the counters read");
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task AnOlderDatabase_GetsTheIndexFromThePatcher()
    {
        var path = NewDbPath();
        try
        {
            await using (var db = new MelodyBridgeDbContext(Options(path)))
                await db.Database.EnsureCreatedAsync();

            // The shape of a database from before the index existed.
            await using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                await conn.OpenAsync();
                var drop = new SqliteCommand($"DROP INDEX IF EXISTS \"{IndexName}\"", conn);
                await drop.ExecuteNonQueryAsync();
            }
            Assert.That(await IndexSqlAsync(path), Is.Null, "the index is gone");

            await using (var db = new MelodyBridgeDbContext(Options(path)))
                await SchemaPatcher.PatchAsync(db);

            Assert.That(await IndexSqlAsync(path), Is.Not.Null,
                "the patcher must add the index on the next start");
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task ThePatcher_LeavesTheIndexAlone_OnASecondStart()
    {
        var path = NewDbPath();
        try
        {
            await using (var db = new MelodyBridgeDbContext(Options(path)))
                await db.Database.EnsureCreatedAsync();
            await using (var db = new MelodyBridgeDbContext(Options(path)))
                await SchemaPatcher.PatchAsync(db);
            var first = await IndexSqlAsync(path);

            await using (var db = new MelodyBridgeDbContext(Options(path)))
                await SchemaPatcher.PatchAsync(db);
            var second = await IndexSqlAsync(path);

            Assert.That(second, Is.EqualTo(first), "the index must survive a second start");
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task TheCounters_ReadTheSameNumbers_WithTheIndex()
    {
        var path = NewDbPath();
        try
        {
            await using (var db = new MelodyBridgeDbContext(Options(path)))
            {
                await db.Database.EnsureCreatedAsync();
                db.Playlists.Add(new PlaylistEntity
                {
                    Id = "pl-counters",
                    Name = "Counters",
                    SourceUrl = "https://example.com/counters",
                    Tracks = new List<TrackEntity>
                    {
                        new() { MelodyId = "m1", Title = "One", Position = 0, DownloadStatus = "downloaded", FileSizeBytes = 100 },
                        new() { MelodyId = "m2", Title = "Two", Position = 1, DownloadStatus = "failed" },
                        new() { MelodyId = "m3", Title = "Three", Position = 2, DownloadStatus = "pending" },
                    },
                });
                await db.SaveChangesAsync();
            }

            var factory = new TestSqliteFactory(path);
            var store = new PlaylistStore(factory, Array.Empty<MelodyBridge.Core.ISourceProvider>(),
                new Application.Services.DownloadManager(
                    new EmptyDownloaderRegistry(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<Application.Services.DownloadManager>.Instance),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<PlaylistStore>.Instance);

            var stats = await store.GetDownloadStatsAsync();

            Assert.That(stats["pl-counters"].downloaded, Is.EqualTo(1));
            Assert.That(stats["pl-counters"].failed, Is.EqualTo(1));
            Assert.That(stats["pl-counters"].total, Is.EqualTo(3));
            Assert.That(stats["pl-counters"].sizeBytes, Is.EqualTo(100));
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(path + suffix); } catch { /* best effort */ }
        }
    }

    private static async Task<string?> IndexSqlAsync(string path)
    {
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync();
        var cmd = new SqliteCommand(
            $"SELECT sql FROM sqlite_master WHERE type='index' AND name='{IndexName}'", conn);
        return await cmd.ExecuteScalarAsync() as string;
    }
}
