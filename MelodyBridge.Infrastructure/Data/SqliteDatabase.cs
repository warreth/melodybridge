using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MelodyBridge.Infrastructure.Data;

/// <summary>
/// SQLite settings the connection string cannot express. The journal mode
/// is stored in the database file, so one call at startup switches every
/// later connection.
/// </summary>
public static class SqliteDatabase
{
    /// <summary>Seconds a writer waits for another writer before failing.</summary>
    public const int BusyTimeoutSeconds = 30;

    /// <summary>
    /// Switches the database to write-ahead logging. In the default
    /// rollback journal a read transaction blocks the writer and a writer
    /// blocks every reader: a page that reads a large playlist while a
    /// download run writes track status waits out the busy timeout and
    /// fails with "database is locked". WAL lets readers and the one
    /// writer proceed together. Returns the mode SQLite reports.
    /// </summary>
    public static async Task<string> EnableWalAsync(
        string databasePath, CancellationToken ct = default)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false,
            DefaultTimeout = BusyTimeoutSeconds,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL;";
        var mode = await command.ExecuteScalarAsync(ct);
        return Convert.ToString(mode, CultureInfo.InvariantCulture) ?? string.Empty;
    }

    /// <summary>
    /// The files that belong to a WAL database besides the database
    /// itself: the write-ahead log and the shared-memory index. They hold
    /// committed pages that the database file does not have yet, so they
    /// must travel with the database or be removed with it.
    /// </summary>
    public static string[] SidecarPaths(string databasePath)
        => [databasePath + "-wal", databasePath + "-shm"];

    /// <summary>
    /// Removes the write-ahead log and the shared-memory index next to a
    /// database. Only safe on a database that was checkpointed, or on one
    /// that is about to be replaced.
    /// </summary>
    public static void DeleteSidecars(string databasePath)
    {
        foreach (var path in SidecarPaths(databasePath))
        {
            try { File.Delete(path); }
            catch { /* best effort: a locked sidecar is cleaned up later */ }
        }
    }

    /// <summary>
    /// True when the exception is SQLite's busy or locked verdict (error
    /// codes 5 and 6), including when EF wraps it in a DbUpdateException.
    /// </summary>
    public static bool IsBusy(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException { SqliteErrorCode: 5 or 6 })
                return true;
        }
        return false;
    }

    /// <summary>
    /// How long a write may keep retrying a busy database before it
    /// gives up. The command timeout already waits 30 seconds per
    /// attempt, so the first attempt alone can take that long; the budget
    /// bounds the retries on top of it. Without a budget three attempts
    /// would block a worker for 90 seconds, and a cancel would wait for
    /// all of it.
    /// </summary>
    public static readonly TimeSpan BusyRetryBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Runs a write and retries a few times when SQLite reports a busy
    /// database. The command timeout already waits 30 seconds; this covers
    /// the overlap with another writer without turning it into a stuck
    /// track row. The retries stop at <see cref="BusyRetryBudget"/>, so a
    /// database that stays locked costs one budget, not one budget per
    /// attempt.
    /// </summary>
    public static async Task<T> RetryBusyAsync<T>(
        Func<Task<T>> action,
        int attempts = 3,
        CancellationToken ct = default,
        TimeSpan? budget = null)
    {
        var deadline = DateTime.UtcNow + (budget ?? BusyRetryBudget);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (attempt < attempts && IsBusy(ex))
            {
                if (DateTime.UtcNow >= deadline)
                    throw;
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
            }
        }
    }
}
