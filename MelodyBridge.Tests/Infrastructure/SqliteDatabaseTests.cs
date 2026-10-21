using MelodyBridge.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// WAL and busy handling against real SQLite files. The point of WAL is
/// that a reader must not stop a writer, which is exactly what the
/// download run needs while a playlist page is open.
/// </summary>
[TestFixture]
public class SqliteDatabaseTests
{
    private static string NewDbPath()
        => Path.Combine(Path.GetTempPath(), $"mb-sqlite-{Guid.NewGuid():N}.db");

    private static void Delete(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(path + suffix); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task EnableWalAsync_SetsAModeThatSurvivesOnANewConnection()
    {
        var path = NewDbPath();
        try
        {
            var mode = await SqliteDatabase.EnableWalAsync(path);
            Assert.That(mode, Is.EqualTo("wal").IgnoreCase);

            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            Assert.That(await command.ExecuteScalarAsync(), Is.EqualTo("wal").IgnoreCase,
                "the journal mode is stored in the database file");
        }
        finally { Delete(path); }
    }

    [Test]
    public async Task Wal_AllowsAWriteWhileAnotherConnectionHoldsAReadTransaction()
    {
        var path = NewDbPath();
        try
        {
            await SqliteDatabase.EnableWalAsync(path);
            await using (var setup = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                await setup.OpenAsync();
                await using var create = setup.CreateCommand();
                create.CommandText =
                    "CREATE TABLE Tracks (Id INTEGER PRIMARY KEY, Title TEXT);"
                    + "INSERT INTO Tracks (Title) VALUES ('one'), ('two');";
                await create.ExecuteNonQueryAsync();
            }

            // Hold a read transaction open, the way a page query does
            // while it materializes a large playlist.
            await using var reader = new SqliteConnection($"Data Source={path};Pooling=False");
            await reader.OpenAsync();
            // Deferred: the SELECT starts a read transaction, it does not
            // take the write lock up front.
            using var readTransaction = reader.BeginTransaction(deferred: true);
            await using (var read = reader.CreateCommand())
            {
                read.Transaction = readTransaction;
                read.CommandText = "SELECT COUNT(*) FROM Tracks;";
                Assert.That(await read.ExecuteScalarAsync(), Is.EqualTo(2L));
            }

            // A second connection must still be able to write. Without WAL
            // this waits for the busy timeout and fails with SQLITE_BUSY.
            await using var writer = new SqliteConnection(
                $"Data Source={path};Pooling=False;Default Timeout=2");
            await writer.OpenAsync();
            await using var write = writer.CreateCommand();
            write.CommandText = "INSERT INTO Tracks (Title) VALUES ('three');";
            Assert.DoesNotThrowAsync(async () => await write.ExecuteNonQueryAsync());

            readTransaction.Rollback();
            await using var check = new SqliteConnection($"Data Source={path};Pooling=False");
            await check.OpenAsync();
            await using var count = check.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM Tracks;";
            Assert.That(await count.ExecuteScalarAsync(), Is.EqualTo(3L));
        }
        finally { Delete(path); }
    }

    [Test]
    public async Task EnableWalAsync_OnAFreshFile_ThenEnsureCreated_KeepsTheSchema()
    {
        var path = NewDbPath();
        try
        {
            await SqliteDatabase.EnableWalAsync(path);
            var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
                .UseSqlite($"Data Source={path};Pooling=False")
                .Options;
            await using (var db = new MelodyBridgeDbContext(options))
            {
                Assert.That(await db.Database.EnsureCreatedAsync(), Is.True);
                Assert.That(await db.Tracks.CountAsync(), Is.Zero);
            }

            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            Assert.That(await command.ExecuteScalarAsync(), Is.EqualTo("wal").IgnoreCase);
        }
        finally { Delete(path); }
    }

    [Test]
    public void IsBusy_RecognizesSqliteBusyAndLocked_AlsoWhenWrapped()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SqliteDatabase.IsBusy(new SqliteException("database is locked", 5)), Is.True);
            Assert.That(SqliteDatabase.IsBusy(
                new DbUpdateException("save failed", new SqliteException("database is locked", 6))),
                Is.True);
            Assert.That(SqliteDatabase.IsBusy(new InvalidOperationException("not a database problem")),
                Is.False);
        });
    }

    [Test]
    public async Task RetryBusyAsync_RetriesABusyWriteUntilItSucceeds()
    {
        var calls = 0;
        var result = await SqliteDatabase.RetryBusyAsync(async () =>
        {
            calls++;
            await Task.Yield();
            if (calls < 3)
                throw new DbUpdateException("save failed", new SqliteException("database is locked", 5));
            return 42;
        }, attempts: 3);

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.EqualTo(42));
            Assert.That(calls, Is.EqualTo(3));
        });
    }

    [Test]
    public void RetryBusyAsync_DoesNotRetryAnOrdinaryFailure()
    {
        var calls = 0;
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await SqliteDatabase.RetryBusyAsync<int>(() =>
            {
                calls++;
                return Task.FromException<int>(new InvalidOperationException("not a lock"));
            }));
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public void DeleteSidecars_RemovesTheWriteAheadLogAndTheSharedMemoryFile()
    {
        var path = NewDbPath();
        try
        {
            File.WriteAllText(path, "database");
            File.WriteAllText(path + "-wal", "log");
            File.WriteAllText(path + "-shm", "index");

            SqliteDatabase.DeleteSidecars(path);

            Assert.Multiple(() =>
            {
                Assert.That(File.Exists(path + "-wal"), Is.False);
                Assert.That(File.Exists(path + "-shm"), Is.False);
                Assert.That(File.Exists(path), Is.True, "the database itself stays");
            });
            Assert.That(SqliteDatabase.SidecarPaths(path), Is.EqualTo(new[] { path + "-wal", path + "-shm" }));
        }
        finally { Delete(path); }
    }
}
