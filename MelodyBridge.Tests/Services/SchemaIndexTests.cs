using System.Runtime.CompilerServices;
using MelodyBridge.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Indexes the hot lookup paths read: a fresh SQLite file gets them from
/// the model, an older one from the patcher on the next start.
/// </summary>
[TestFixture]
public class SchemaIndexTests
{
    private static readonly string[] Names =
    [
        "IX_Tracks_CurrentPath",
        "IX_Tracks_ArchivePath",
        "IX_SyncJobRuns_Timestamp",
        "IX_SyncJobRuns_SyncJobId_Timestamp",
    ];

    private static string NewDbPath([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), $"mb-schema-index-{test}-{Guid.NewGuid():N}.db");

    private static DbContextOptions<MelodyBridgeDbContext> Options(string path)
        => new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False").Options;

    private static async Task<bool> HasIndexAsync(string path, string name)
    {
        await using var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync();
        await using var cmd = new SqliteCommand(
            "SELECT count(*) FROM sqlite_master WHERE type='index' AND name=$name", conn);
        cmd.Parameters.AddWithValue("$name", name);
        return (long)(await cmd.ExecuteScalarAsync())! > 0;
    }

    [Test]
    public async Task AFreshDatabase_HasThePathAndRunIndexes()
    {
        var path = NewDbPath();
        try
        {
            await using (var db = new MelodyBridgeDbContext(Options(path)))
                await db.Database.EnsureCreatedAsync();

            foreach (var name in Names)
                Assert.That(await HasIndexAsync(path, name), Is.True, name);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Test]
    public async Task AnOlderDatabase_GetsThemFromThePatcher()
    {
        var path = NewDbPath();
        try
        {
            await using (var db = new MelodyBridgeDbContext(Options(path)))
                await db.Database.EnsureCreatedAsync();
            await using (var conn = new SqliteConnection($"Data Source={path}"))
            {
                await conn.OpenAsync();
                foreach (var name in Names)
                {
                    await using var drop = new SqliteCommand(
                        $"DROP INDEX IF EXISTS \"{name}\"", conn);
                    await drop.ExecuteNonQueryAsync();
                }
            }
            foreach (var name in Names)
                Assert.That(await HasIndexAsync(path, name), Is.False, name);

            await using (var db = new MelodyBridgeDbContext(Options(path)))
                await SchemaPatcher.PatchAsync(db);

            foreach (var name in Names)
                Assert.That(await HasIndexAsync(path, name), Is.True, name);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
