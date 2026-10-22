using Microsoft.Data.Sqlite;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The database restore at startup, against real SQLite files.
/// <para>
/// The point of these tests is the write-ahead log. A SQLite database in
/// WAL mode is three files: the database, its -wal and its -shm. Replace
/// only the database and leave the log behind, and the next connection
/// replays the old log over the new file: the restored rows are gone
/// and the old ones are back, with "integrity_check" reporting that
/// everything is fine.
/// </para>
/// </summary>
[TestFixture]
[Category("Integration")]
public class DatabaseRestoreTests
{
    private string _dir = null!;
    private string _dbPath = null!;
    private DatabaseRestore _restore = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mb-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "melodybridge.db");
        _restore = new DatabaseRestore();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public void Apply_Without_A_Staged_Database_Changes_Nothing()
    {
        WriteItems(_dbPath, "old", "Old");

        var result = _restore.Apply(_dbPath);

        Assert.Multiple(() =>
        {
            Assert.That(result.Applied, Is.False);
            Assert.That(result.Error, Is.Null);
            Assert.That(File.Exists(_dbPath), Is.True);
        });
        Assert.That(ReadNames(_dbPath), Is.EqualTo(new[] { "Old" }),
            "the live database is untouched");
    }

    [Test]
    public void Apply_Swaps_The_Staged_Database_And_Parks_The_Old_One()
    {
        WriteItems(_dbPath, "old", "Old");
        StageItems("new", "New");

        var result = _restore.Apply(_dbPath);

        Assert.Multiple(() =>
        {
            Assert.That(result.Applied, Is.True, result.Error);
            Assert.That(result.ParkedPath, Is.Not.Null.And.Not.Empty);
            Assert.That(File.Exists(result.ParkedPath!), Is.True,
                "the previous database is parked, not deleted");
        });
        Assert.That(ReadNames(_dbPath), Is.EqualTo(new[] { "New" }),
            "the staged database took the place of the live one");
        Assert.That(ReadNames(result.ParkedPath!), Is.EqualTo(new[] { "Old" }),
            "the parked database still holds the old rows");
    }

    [Test]
    public void Apply_With_A_Stale_Wal_Sidecar_Does_Not_Bring_The_Old_Rows_Back()
    {
        // The live database in WAL mode, with a write-ahead log beside it
        // that still holds committed pages. This is the state a running
        // app leaves behind.
        var staleWal = Path.Combine(_dir, "stale.db-wal");
        WriteItemsInWalMode(_dbPath, "old", "Old",
            () => File.Copy(_dbPath + "-wal", staleWal, overwrite: true));
        // Closing the connection checkpoints the log into the database and
        // removes it, so the saved copy is put back by hand: the live file
        // now has a write-ahead log that belongs to an older version of
        // the data.
        File.Copy(staleWal, _dbPath + "-wal", overwrite: true);

        // The imported database, staged for the next start.
        StageItems("new", "New");

        var result = _restore.Apply(_dbPath);

        Assert.That(result.Applied, Is.True, result.Error);
        Assert.That(ReadNames(_dbPath), Is.EqualTo(new[] { "New" }),
            "the imported rows are the ones that are there: the stale log did not replay");
        Assert.That(IntegrityCheck(_dbPath), Is.EqualTo("ok"),
            "the restored database is intact");
        Assert.That(File.Exists(_dbPath + "-wal"), Is.False,
            "the write-ahead log of the old database is gone");
        Assert.That(File.Exists(_dbPath + "-shm"), Is.False,
            "the shared-memory file of the old database is gone");
    }

    [Test]
    public void A_Stale_Wal_Sidecar_Replays_Over_A_Replaced_Database()
    {
        // The reason the restore happens at startup, documented as a test:
        // replacing the database file while its write-ahead log stays
        // behind brings the old rows back, and SQLite calls the result
        // healthy. If this ever stops being true, the restore may be
        // able to run while the app is up.
        var staleWal = Path.Combine(_dir, "stale.db-wal");
        WriteItemsInWalMode(_dbPath, "old", "Old",
            () => File.Copy(_dbPath + "-wal", staleWal, overwrite: true));
        DeleteSidecars(_dbPath);

        // The old restore: only the database file is replaced, the log
        // stays where it was.
        var foreign = Path.Combine(_dir, "foreign.db");
        WriteItems(foreign, "new", "New");
        File.Copy(foreign, _dbPath, overwrite: true);
        File.Copy(staleWal, _dbPath + "-wal", overwrite: true);
        var names = ReadNames(_dbPath);
        Assert.That(names, Is.EqualTo(new[] { "Old" }),
            "the old write-ahead log replayed over the replaced database");
        Assert.That(IntegrityCheck(_dbPath), Is.EqualTo("ok"),
            "and SQLite calls the result healthy");
    }

    [Test]
    public void Apply_Rejects_A_Staged_Database_Without_Schema()
    {
        WriteItems(_dbPath, "old", "Old");
        // A file with no tables: the classic broken backup.
        var pending = DatabaseRestore.PendingPath(_dbPath);
        using (var connection = new SqliteConnection($"Data Source={pending};Pooling=False"))
        {
            connection.Open();
        }

        var result = _restore.Apply(_dbPath);

        Assert.Multiple(() =>
        {
            Assert.That(result.Applied, Is.False);
            Assert.That(result.Error, Does.Contain("no tables"));
            Assert.That(File.Exists(_dbPath), Is.True,
                "the live database is left alone");
        });
        Assert.That(ReadNames(_dbPath), Is.EqualTo(new[] { "Old" }));
    }

    [Test]
    public void Apply_Puts_The_Original_Back_When_The_Swap_Fails()
    {
        WriteItems(_dbPath, "old", "Old");
        StageItems("new", "New");

        var failing = new FailingRestore();
        var result = failing.Apply(_dbPath);

        Assert.Multiple(() =>
        {
            Assert.That(result.Applied, Is.False);
            Assert.That(result.Error, Does.Contain("simulated restore failure"));
            Assert.That(File.Exists(_dbPath), Is.True,
                "a failed restore never leaves the app without a database");
        });
        Assert.That(ReadNames(_dbPath), Is.EqualTo(new[] { "Old" }),
            "the original content is back");
    }

    /// <summary>Fails the final replace step, after the live file is parked.</summary>
    private sealed class FailingRestore : DatabaseRestore
    {
        protected override void MoveIntoPlace(string pendingPath, string databasePath)
            => throw new IOException("simulated restore failure");
    }

    // ── the open handle ───────────────────────────────────────────

    /// <summary>
    /// The schema check must not leave the file open. Microsoft.Data.Sqlite
    /// pools by default, so a pooled connection goes back to the pool with
    /// the file handle still open. The swap is a rename of that same file,
    /// and Windows refuses to rename a file that is still open: the import
    /// would never apply and every start would try again and fail.
    /// </summary>
    [Test]
    public void The_Schema_Check_Opens_The_File_Without_Pooling()
    {
        WriteItems(_dbPath, "old", "Old");
        var capturing = new CapturingRestore();

        var hasSchema = capturing.HasSchema(_dbPath);

        Assert.Multiple(() =>
        {
            Assert.That(hasSchema, Is.True);
            Assert.That(capturing.ConnectionString, Is.Not.Null,
                "the schema check went through the connection seam");
            Assert.That(
                new SqliteConnectionStringBuilder(capturing.ConnectionString!).Pooling,
                Is.False,
                "a pooled connection keeps the file open and Windows then refuses the rename");
        });
    }

    /// <summary>
    /// The same thing, seen from the operating system: after the schema
    /// check nothing holds the file open. Linux keeps the name open even
    /// for a deleted file, so the count of open descriptors is the proof.
    /// </summary>
    [Test]
    public void The_Schema_Check_Leaves_No_Handle_On_The_File()
    {
        if (!Directory.Exists("/proc/self/fd"))
            Assert.Ignore("this platform does not expose its open files");

        WriteItems(_dbPath, "old", "Old");

        var before = OpenHandles(_dbPath);
        _restore.HasSchema(_dbPath);
        var after = OpenHandles(_dbPath);

        Assert.That(after, Is.EqualTo(before),
            "the schema check left a handle on the file");
    }

    /// <summary>
    /// The file the import renames has to be renameable right after the
    /// schema check. On Windows this is where a leftover handle shows up.
    /// </summary>
    [Test]
    public void The_Staged_File_Can_Be_Renamed_After_The_Schema_Check()
    {
        var pending = DatabaseRestore.PendingPath(_dbPath);
        WriteItems(pending, "new", "New");

        _restore.HasSchema(pending);

        var moved = pending + ".moved";
        File.Move(pending, moved);
        File.Move(moved, pending);

        Assert.That(File.Exists(pending), Is.True,
            "the staged database is still there, under its own name");
    }

    /// <summary>Captures the connection the schema check opens.</summary>
    private sealed class CapturingRestore : DatabaseRestore
    {
        public string? ConnectionString { get; private set; }

        protected override SqliteConnection OpenForSchemaCheck(string path)
        {
            var connection = base.OpenForSchemaCheck(path);
            ConnectionString = connection.ConnectionString;
            return connection;
        }
    }

    /// <summary>How many open descriptors point at this file.</summary>
    private static int OpenHandles(string path)
    {
        var target = Path.GetFullPath(path);
        return Directory.GetFiles("/proc/self/fd").Count(fd =>
        {
            try
            {
                var link = File.ResolveLinkTarget(fd, true)?.FullName;
                return link != null && string.Equals(link, target, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        });
    }

    // ── helpers ──────────────────────────────────────────────────

    /// <summary>A database with one table and one row.</summary>
    private void WriteItems(string path, string id, string name)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        Execute(connection, "CREATE TABLE IF NOT EXISTS Items (Id TEXT PRIMARY KEY, Name TEXT);");
        Execute(connection, $"INSERT OR REPLACE INTO Items (Id, Name) VALUES ('{id}', '{name}');");
    }

    /// <summary>The same, in WAL mode, so a write-ahead log is left behind.</summary>
    private void WriteItemsInWalMode(string path, string id, string name, Action? whileOpen = null)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        Execute(connection, "PRAGMA journal_mode=WAL;");
        Execute(connection, "CREATE TABLE IF NOT EXISTS Items (Id TEXT PRIMARY KEY, Name TEXT);");
        Execute(connection, $"INSERT OR REPLACE INTO Items (Id, Name) VALUES ('{id}', '{name}');");
        // The log only exists while a connection is open: closing the last
        // one checkpoints it into the database and removes it.
        whileOpen?.Invoke();
    }

    /// <summary>Stages a database for the next start, the way an import does.</summary>
    private void StageItems(string id, string name)
    {
        var pending = DatabaseRestore.PendingPath(_dbPath);
        WriteItems(pending, id, name);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string[] ReadNames(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM Items ORDER BY Name;";
        var names = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names.ToArray();
    }

    private static string IntegrityCheck(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        return Convert.ToString(command.ExecuteScalar()) ?? "";
    }

    private static void DeleteSidecars(string path)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            try { File.Delete(path + suffix); } catch { /* best effort */ }
        }
    }
}
