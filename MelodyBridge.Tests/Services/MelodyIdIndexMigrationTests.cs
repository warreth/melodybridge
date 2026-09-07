using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MelodyBridge.Infrastructure.Data;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The MelodyId uniqueness migration, end to end on a real SQLite file:
/// a database created before dedup carries a UNIQUE index on
/// Tracks.MelodyId (one row per source track library-wide), which made
/// the same track in two playlists impossible. The patcher must replace
/// it with a plain index on the next startup, keep every existing row,
/// and stay idempotent for every start after that.
/// </summary>
[TestFixture]
[Category("Dedup")]
public class MelodyIdIndexMigrationTests
{
    private static string NewDbPath([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), $"mb-melodyid-{test}-{Guid.NewGuid():N}.db");

    private static async Task<MelodyBridgeDbContext> CreatePreDedupDatabaseAsync(string path)
    {
        // 1. Create today's schema.
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseSqlite($"Data Source={path}").Options;
        var db = new MelodyBridgeDbContext(options);
        await db.Database.EnsureCreatedAsync();
        await db.DisposeAsync();

        // 2. Regress it to the pre-dedup shape: the UNIQUE index.
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync();
        var drop = new SqliteCommand("DROP INDEX IF EXISTS \"IX_Tracks_MelodyId\"", conn);
        await drop.ExecuteNonQueryAsync();
        var create = new SqliteCommand(
            "CREATE UNIQUE INDEX \"IX_Tracks_MelodyId\" ON \"Tracks\" (\"MelodyId\")", conn);
        await create.ExecuteNonQueryAsync();
        await conn.CloseAsync();

        // 3. One row behind the unique index, exactly like production.
        var db2 = new MelodyBridgeDbContext(options);
        db2.Playlists.Add(new PlaylistEntity
        {
            Id = "pl-old", Name = "Old", SourceUrl = "https://example.com/old",
            Tracks = new List<TrackEntity>
            {
                new() { MelodyId = "spotify:old-1", Title = "Old Song", Artist = "A", Position = 0 },
            },
        });
        await db2.SaveChangesAsync();
        await db2.DisposeAsync();
        return new MelodyBridgeDbContext(options);
    }

    [Test]
    public async Task Patcher_DropsUniqueIndex_AllowsSameMelodyIdTwice()
    {
        var path = NewDbPath();
        try
        {
            await using (var db = await CreatePreDedupDatabaseAsync(path))
            {
                await SchemaPatcher.PatchAsync(db);

                var indexSql = await IndexSqlAsync(path);
                Assert.That(indexSql, Is.Not.Null, "the index still exists");
                Assert.That(indexSql, Does.Not.Contain("UNIQUE"),
                    "the index must be a plain one now: duplicates allowed");
            }

            // The whole point: the same source track can now be a row in
            // two playlists, the basis of cross-playlist dedup.
            var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
                .UseSqlite($"Data Source={path}").Options;
            await using var db2 = new MelodyBridgeDbContext(options);
            db2.Playlists.AddRange(
                new PlaylistEntity
                {
                    Id = "pl-a", Name = "A", SourceUrl = "https://example.com/a",
                    Tracks = new List<TrackEntity>
                    {
                        new() { MelodyId = "spotify:old-1", Title = "Old Song", Artist = "A", Position = 0 },
                    },
                },
                new PlaylistEntity
                {
                    Id = "pl-b", Name = "B", SourceUrl = "https://example.com/b",
                    Tracks = new List<TrackEntity>
                    {
                        new() { MelodyId = "spotify:old-1", Title = "Old Song", Artist = "A", Position = 0 },
                    },
                });
            await db2.SaveChangesAsync();

            var count = await db2.Tracks.CountAsync(t => t.MelodyId == "spotify:old-1");
            Assert.That(count, Is.EqualTo(3),
                "the pre-existing row plus two new ones all persist");
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Patcher_IsIdempotent_AcrossRepeatedStartups()
    {
        var path = NewDbPath();
        try
        {
            await using (var db = await CreatePreDedupDatabaseAsync(path))
                await SchemaPatcher.PatchAsync(db);
            await using (var db = new MelodyBridgeDbContext(
                new DbContextOptionsBuilder<MelodyBridgeDbContext>()
                    .UseSqlite($"Data Source={path}").Options))
                await SchemaPatcher.PatchAsync(db); // second startup

            var indexSql = await IndexSqlAsync(path);
            Assert.That(indexSql, Does.Not.Contain("UNIQUE"),
                "the second run must not resurrect the unique index");
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    private static async Task<string?> IndexSqlAsync(string path)
    {
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync();
        var cmd = new SqliteCommand(
            "SELECT sql FROM sqlite_master WHERE type='index' AND name='IX_Tracks_MelodyId'", conn);
        return await cmd.ExecuteScalarAsync() as string;
    }
}
