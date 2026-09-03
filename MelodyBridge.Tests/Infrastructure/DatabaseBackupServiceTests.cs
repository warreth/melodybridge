using System.IO.Compression;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Backup/restore against a real SQLite file: export produces a valid zip
/// with a readable database inside; import swaps the live file and parks
/// the old one. No mocks anywhere.
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
    public async Task Import_Swaps_Database_And_Parks_The_Old_One()
    {
        // Build a "foreign" database with different content, then take a
        // VACUUM INTO snapshot the way a real export does (a raw copy of a
        // WAL-mode file has no schema: see the reject test below).
        var foreignPath = Path.Combine(_dir, "foreign.db");
        var snapshotPath = Path.Combine(_dir, "foreign-snapshot.db");
        var foreignFactory = new TestSqliteFactory(foreignPath);
        using (var db = foreignFactory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "p2", Name = "Foreign", SourceUrl = "https://y", TrackCount = 7,
            });
            db.SaveChanges();
            await db.Database.ExecuteSqlRawAsync($"VACUUM INTO '{snapshotPath.Replace("'", "''")}'");
        }

        var zipPath = Path.Combine(_dir, "upload.zip");
        using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            archive.CreateEntryFromFile(snapshotPath, "melodybridge.db");
        }

        await using var stream = File.OpenRead(zipPath);
        await _service.ImportZipAsync(stream);

        Assert.That(File.Exists(_dbPath), Is.True);
        Assert.That(Directory.GetFiles(_dir, "*.preimport.bak").Length, Is.EqualTo(1),
            "the old database is parked, not deleted");

        var restored = new TestSqliteFactory(_dbPath);
        using (var db = restored.CreateDbContext())
        {
            Assert.That(db.Playlists.Count(p => p.Name == "Foreign"), Is.EqualTo(1),
                "the restored database replaced the old content");
            Assert.That(db.Playlists.Count(p => p.Name == "Original"), Is.EqualTo(0));
        }
    }

    [Test]
    public async Task Import_Rejects_Schema_Less_Database_With_Clear_Message()
    {
        // A "database" with no tables at all (the classic broken backup:
        // someone zipped an empty or WAL-orphaned file). The import must
        // refuse it loudly and leave the live database untouched.
        var emptyPath = Path.Combine(_dir, "empty.db");
        using (var connection = new SqliteConnection($"Data Source={emptyPath}"))
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
    }

    [Test]
    public async Task Import_Rejects_Zip_Without_Database()
    {
        var zipPath = Path.Combine(_dir, "empty.zip");
        using (ZipFile.Open(zipPath, ZipArchiveMode.Create)) { /* empty archive */ }

        await using var stream = File.OpenRead(zipPath);
        Assert.ThrowsAsync<InvalidOperationException>(() => _service.ImportZipAsync(stream));
    }
}
