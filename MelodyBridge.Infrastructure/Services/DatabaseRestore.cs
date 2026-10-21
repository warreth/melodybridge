using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using MelodyBridge.Infrastructure.Data;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>What a staged import did when it was applied at startup.</summary>
/// <param name="Applied">True when the staged database took the place of the live one.</param>
/// <param name="ParkedPath">Where the previous database was parked, if there was one.</param>
/// <param name="Error">Why the restore did not happen, in a sentence the user can read.</param>
public sealed record DatabaseRestoreResult(bool Applied, string? ParkedPath, string? Error);

/// <summary>
/// The database restore, split in two halves that never meet while the
/// app is running: the import stages a validated database next to the
/// live file, and the next start swaps it in.
/// <para>
/// The swap has to happen at startup, with no connection open. A SQLite
/// database in write-ahead logging is three files: the database, its
/// -wal and its -shm. Replacing only the database and leaving the log
/// behind means the next connection replays the old log over the new
/// file: the restored rows disappear and the old ones come back, with
/// "integrity_check" saying everything is fine.
/// </para>
/// </summary>
public class DatabaseRestore
{
    /// <summary>The suffix of the staged database, next to the live file.</summary>
    public const string PendingSuffix = ".pending-restore";

    /// <summary>The path of the staged database for a given live file.</summary>
    public static string PendingPath(string databasePath) => databasePath + PendingSuffix;

    /// <summary>True when an import is waiting for the next start.</summary>
    public static bool HasPendingRestore(string databasePath) => File.Exists(PendingPath(databasePath));

    /// <summary>
    /// True when the file carries a schema. A zip with a raw copy of a
    /// WAL database has no tables in the file itself: they live in the
    /// -wal that was never zipped.
    /// </summary>
    public static bool HasSchema(string path)
    {
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        return Convert.ToInt64(command.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// Swaps a staged import into place. Called at startup, before any
    /// connection opens: the live database is folded back into one file,
    /// the write-ahead log beside it is removed, the live file is parked
    /// as .preimport.bak and the staged file takes its place.
    /// </summary>
    public virtual DatabaseRestoreResult Apply(string databasePath, ILogger? logger = null)
    {
        var pending = PendingPath(databasePath);
        if (!File.Exists(pending))
            return new(false, null, null);

        try
        {
            return ApplyCore(pending, databasePath, logger);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "The staged database could not be restored");
            return new(false, null, "The restore failed: " + ex.Message);
        }
    }

    private DatabaseRestoreResult ApplyCore(string pending, string databasePath, ILogger? logger)
    {
        if (!HasSchema(pending))
        {
            // The staged file cannot be used. Keep it beside the database
            // under a name that says so, and leave the live one alone.
            var rejected = pending + ".rejected";
            TryMove(pending, rejected);
            logger?.LogError(
                "The staged database import has no tables and was rejected; the live database was left alone");
            return new(false, null,
                "The imported database carries no tables. The live database was left alone.");
        }

        // The staged file must be one file: fold its log into it and drop
        // the sidecars, so the file that moves into place is complete.
        try
        {
            Checkpoint(pending);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "The staged database could not be checkpointed; the restore is postponed");
            return new(false, null,
                "The imported database could not be prepared; the restore is postponed to the next start.");
        }
        SqliteDatabase.DeleteSidecars(pending);

        string? parked = null;
        if (File.Exists(databasePath))
        {
            // The live database first: folded into one file, so the parked
            // copy holds every committed transaction.
            try
            {
                Checkpoint(databasePath);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "The live database could not be checkpointed; the restore is postponed");
                return new(false, null,
                    "The live database could not be prepared; the restore is postponed to the next start.");
            }

            parked = Park(databasePath, logger);
        }

        // The sidecars of the live file go with it. Left behind, the next
        // connection to the restored file replays them and brings the old
        // rows back.
        SqliteDatabase.DeleteSidecars(databasePath);

        try
        {
            MoveIntoPlace(pending, databasePath);
        }
        catch (Exception ex)
        {
            // The live database is parked: put it back, so a failed
            // restore never leaves the app without one.
            if (parked is not null)
                TryMove(parked, databasePath);
            SqliteDatabase.DeleteSidecars(databasePath);
            logger?.LogError(ex, "The staged database could not be moved into place");
            return new(false, null, "The restore failed: " + ex.Message);
        }

        SqliteDatabase.DeleteSidecars(databasePath);
        logger?.LogInformation(
            "Restored the staged database; the previous one is parked at {Path}", parked);
        return new(true, parked, null);
    }

    /// <summary>Test seam: the final replace step of a restore.</summary>
    protected virtual void MoveIntoPlace(string pendingPath, string databasePath)
        => File.Move(pendingPath, databasePath, overwrite: true);

    /// <summary>
    /// Folds the write-ahead log back into the database file. A
    /// checkpointed database is one file again: no -wal and no -shm.
    /// </summary>
    private static void Checkpoint(string path)
    {
        if (!File.Exists(path)) return;
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            DefaultTimeout = SqliteDatabase.BusyTimeoutSeconds,
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Parks the live database next to itself (a unique name, so two
    /// restores in one second cannot collide) and returns the parked path.
    /// </summary>
    private static string? Park(string databasePath, ILogger? logger)
    {
        var backupPath = $"{databasePath}.{DateTime.UtcNow:yyyyMMdd_HHmmss}.{Guid.NewGuid():N}.preimport.bak";
        File.Move(databasePath, backupPath);
        logger?.LogInformation("Pre-import database parked at {Path}", backupPath);
        return backupPath;
    }

    private static void TryMove(string from, string to)
    {
        try { File.Move(from, to, overwrite: true); }
        catch { /* best effort */ }
    }
}
