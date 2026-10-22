using System.IO.Compression;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Backup/restore against a real SQLite file: export produces a valid zip
/// with a readable database inside; an import stages that database next
/// to the live file and leaves the running app alone. The swap itself is
/// covered by <see cref="DatabaseRestoreTests"/>.
/// No mocks anywhere.
/// </summary>
[TestFixture]
[Category("Integration")]
public class DatabaseBackupServiceTests
{
    private string _dir = null!;
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;
    private DatabaseBackupService _service = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mb-backup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "melodybridge.db");
        _factory = new TestSqliteFactory(_dbPath);
        using (var db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "p1", Name = "Original", SourceUrl = "https://x", TrackCount = 3,
            });
            db.SaveChanges();
        }
        _service = new DatabaseBackupService(_factory,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabaseBackupService>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task Export_Produces_Zip_With_Queryable_Database()
    {
        var zip = await _service.ExportZipAsync();

        // Unpack with the framework's own reader and inspect the snapshot.
        using var archive = new ZipArchive(new MemoryStream(zip));
        var entry = archive.GetEntry("melodybridge.db");
        Assert.That(entry, Is.Not.Null, "the zip must contain melodybridge.db");

        var extracted = Path.Combine(_dir, "check.db");
        entry!.ExtractToFile(extracted, overwrite: true);

        var check = new TestSqliteFactory(extracted);
        using (var db = check.CreateDbContext())
        {
            Assert.That(db.Playlists.Count(p => p.Name == "Original"), Is.EqualTo(1),
                "the snapshot holds the real data");
        }
    }

    [Test]
    public async Task Import_Stages_The_Database_And_Leaves_The_Live_File_Alone()
    {
        // The import must not touch the live database: the swap happens at
        // the next start, with no connection open. Touching it here would
        // leave the write-ahead log of the running app beside the
        // restored file, and the next connection would replay it.
        var zipPath = BuildForeignZip();

        await using var stream = File.OpenRead(zipPath);
        await _service.ImportZipAsync(stream);

        var pending = DatabaseRestore.PendingPath(_dbPath);
        Assert.That(File.Exists(pending), Is.True,
            "the imported database waits next to the live one");
        Assert.That(await _service.HasPendingRestoreAsync(), Is.True);

        // The live database still holds the running app's data.
        var live = new TestSqliteFactory(_dbPath);
        using (var db = live.CreateDbContext())
        {
            Assert.That(db.Playlists.Count(p => p.Name == "Original"), Is.EqualTo(1),
                "the live database is untouched while the app runs");
            Assert.That(db.Playlists.Count(p => p.Name == "Foreign"), Is.Zero);
        }

        // And the staged file holds the imported one.
        var staged = new TestSqliteFactory(pending);
        using (var db = staged.CreateDbContext())
        {
            Assert.That(db.Playlists.Count(p => p.Name == "Foreign"), Is.EqualTo(1),
                "the staged database holds the imported content");
        }
    }

    [Test]
    public async Task Import_Can_Be_Discarded_Before_The_Next_Start()
    {
        // The user imported the wrong file. The live database was never
        // touched, so throwing the staged copy away is the way back.
        var zipPath = BuildForeignZip();
        await using var stream = File.OpenRead(zipPath);
        await _service.ImportZipAsync(stream);
        Assert.That(await _service.HasPendingRestoreAsync(), Is.True);

        Assert.That(await _service.DiscardPendingRestoreAsync(), Is.True);

        var stillStaged = await _service.HasPendingRestoreAsync();
        var stagedFile = File.Exists(DatabaseRestore.PendingPath(_dbPath));
        Assert.Multiple(() =>
        {
            Assert.That(stillStaged, Is.False);
            Assert.That(stagedFile, Is.False);
        });
        var live = new TestSqliteFactory(_dbPath);
        using (var db = live.CreateDbContext())
        {
            Assert.That(db.Playlists.Count(p => p.Name == "Original"), Is.EqualTo(1),
                "the live database never changed");
        }
    }

    [Test]
    public async Task Discarding_Nothing_Is_Not_An_Error()
    {
        Assert.That(await _service.DiscardPendingRestoreAsync(), Is.False,
            "there was no staged import to throw away");
    }

    [Test]
    public async Task Import_Rejects_Schema_Less_Database_With_Clear_Message()
    {
        // A "database" with no tables at all (the classic broken backup:
        // someone zipped an empty or WAL-orphaned file). The import must
        // refuse it loudly and leave the live database untouched.
        var emptyPath = Path.Combine(_dir, "empty.db");
        // Pooling off: a pooled connection keeps the file handle alive and
        // Windows then refuses the teardown delete.
        using (var connection = new SqliteConnection($"Data Source={emptyPath};Pooling=False"))
        {
            connection.Open(); // creates the file, no schema
        }

        var zipPath = Path.Combine(_dir, "empty.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(emptyPath, "melodybridge.db");
        }

        await using var stream = File.OpenRead(zipPath);
        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => _service.ImportZipAsync(stream));
        Assert.That(ex!.Message, Does.Contain("no tables"),
            "the message tells the user why the zip is unusable");
        // And the live database survived untouched:
        var live = new TestSqliteFactory(_dbPath);
        using (var db = live.CreateDbContext())
        {
            Assert.That(db.Playlists.Count(p => p.Name == "Original"), Is.EqualTo(1));
        }
        Assert.That(File.Exists(DatabaseRestore.PendingPath(_dbPath)), Is.False,
            "a rejected import leaves nothing staged");
    }

    [Test]
    public async Task Import_Rejects_Zip_Without_Database()
    {
        var zipPath = Path.Combine(_dir, "empty.zip");
        using (ZipFile.Open(zipPath, ZipArchiveMode.Create)) { /* empty archive */ }

        await using var stream = File.OpenRead(zipPath);
        Assert.ThrowsAsync<InvalidOperationException>(() => _service.ImportZipAsync(stream));
    }

    [Test]
    public async Task Import_Rejects_A_File_That_Is_Not_A_Zip_With_A_Readable_Message()
    {
        // A text file renamed .zip: the zip reader's own message ("End of
        // Central Directory record could not be found") means nothing to
        // a user, so the service says it in plain words.
        var notZip = Path.Combine(_dir, "notes.zip");
        await File.WriteAllTextAsync(notZip, "this is not a zip archive");

        await using var stream = File.OpenRead(notZip);
        var ex = Assert.ThrowsAsync<InvalidDataException>(() => _service.ImportZipAsync(stream));
        Assert.That(ex!.Message, Does.Contain("not a readable zip archive"),
            "the message says what is wrong in user language");

        // And the live database survived untouched:
        var live = new TestSqliteFactory(_dbPath);
        using (var db = live.CreateDbContext())
        {
            Assert.That(db.Playlists.Count(p => p.Name == "Original"), Is.EqualTo(1));
        }
    }

    /// <summary>A zip holding a real, schema-complete foreign database.</summary>
    private string BuildForeignZip()
    {
        var foreignPath = Path.Combine(_dir, $"foreign-{Guid.NewGuid():N}.db");
        var snapshotPath = Path.Combine(_dir, $"foreign-snapshot-{Guid.NewGuid():N}.db");
        var foreignFactory = new TestSqliteFactory(foreignPath);
        using (var db = foreignFactory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "p2", Name = "Foreign", SourceUrl = "https://y", TrackCount = 7,
            });
            db.SaveChanges();
            db.Database.ExecuteSqlRaw($"VACUUM INTO '{snapshotPath.Replace("'", "''")}'");
        }

        var zipPath = Path.Combine(_dir, $"upload-{Guid.NewGuid():N}.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(snapshotPath, "melodybridge.db");
        }
        return zipPath;
    }
}
