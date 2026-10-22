using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MelodyBridge.Infrastructure.Data;
using Microsoft.Extensions.Logging;
using System.IO.Compression;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Real SQLite backup/restore. Reads/writes the actual database file
/// behind the DbContext, not EF snapshots, so the result is a faithful
/// byte copy of the app's state.
/// </summary>
public class DatabaseBackupService(
    IDbContextFactory<MelodyBridgeDbContext> dbFactory,
    ILogger<DatabaseBackupService> logger)
{
    /// <summary>
    /// Streams a consistent zip of the database. SQLite in WAL mode can be
    /// mid-write, so this uses VACUUM INTO (a proper online snapshot)
    /// rather than copying the live file.
    /// </summary>
    public async Task<byte[]> ExportZipAsync(CancellationToken ct = default)
    {
        var dbPath = await GetDatabasePathAsync(ct);
        if (!File.Exists(dbPath))
            throw new FileNotFoundException("Database file not found.", dbPath);

        var tempDir = Path.Combine(Path.GetTempPath(), $"mb-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var snapshot = Path.Combine(tempDir, "snapshot.db");
            await using (var db = await dbFactory.CreateDbContextAsync(ct))
            {
                // Online snapshot: safe while the app is writing.
                await db.Database.ExecuteSqlRawAsync(
                    $"VACUUM INTO '{snapshot.Replace("'", "''")}'", ct);
            }

            var zipPath = Path.Combine(tempDir, ZipName());
            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(snapshot, "melodybridge.db",
                    CompressionLevel.Optimal);
            }

            return await File.ReadAllBytesAsync(zipPath, ct);
        }
        finally
        {
            TryDelete(tempDir);
        }
    }

    /// <summary>
    /// Validates the database in the uploaded zip and stages it next to
    /// the live file. The swap happens at the next start, with no
    /// connection open: replacing a live WAL database while connections
    /// are open leaves the old write-ahead log behind, and the next
    /// connection replays it over the restored file.
    /// The caller must tell the user that a restart applies the import.
    /// </summary>
    public async Task ImportZipAsync(Stream zipStream, CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"mb-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var zipPath = Path.Combine(tempDir, "upload.zip");
            await using (var file = File.Create(zipPath))
            {
                await zipStream.CopyToAsync(file, ct);
            }

            var restored = Path.Combine(tempDir, "restored.db");
            using (var archive = OpenZipArchive(zipPath))
            {
                // Only unpack a plain .db entry from the top level: no
                // arbitrary extraction paths, no zip bombs beyond one file.
                var entry = archive.Entries
                    .Where(e => e.FullName == "melodybridge.db"
                        || (!e.FullName.Contains('/') && e.Name.EndsWith(".db", StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(e => e.FullName.Length)
                    .FirstOrDefault()
                    ?? throw new InvalidOperationException(
                        "The zip file does not contain a melodybridge.db.");

                entry.ExtractToFile(restored, overwrite: true);
            }

            // Refuse zips whose database carries no schema (copied without
            // its -wal sidecar) before the import is staged.
            RequireUsableDatabase(restored);

            // One file, no sidecars: the staged database must carry every
            // committed page on its own.
            await MakeSelfContainedAsync(restored, ct);

            var dbPath = await GetDatabasePathAsync(ct);
            var pending = DatabaseRestore.PendingPath(dbPath);
            // The staged file sits next to the live database, so the swap
            // at startup is a rename on the same filesystem.
            File.Copy(restored, pending, overwrite: true);

            logger.LogInformation(
                "Database import staged at {Path}; it is applied on the next start", pending);
        }
        finally
        {
            TryDelete(tempDir);
        }
    }

    /// <summary>
    /// True when an imported database is waiting for the next start.
    /// </summary>
    public async Task<bool> HasPendingRestoreAsync(CancellationToken ct = default)
    {
        var dbPath = await GetDatabasePathAsync(ct);
        return DatabaseRestore.HasPendingRestore(dbPath);
    }

    /// <summary>
    /// Throws the staged import away. The live database is untouched, so
    /// this is the way back from an import the user did not mean to make.
    /// Returns false when there was nothing staged.
    /// </summary>
    public async Task<bool> DiscardPendingRestoreAsync(CancellationToken ct = default)
    {
        var dbPath = await GetDatabasePathAsync(ct);
        var pending = DatabaseRestore.PendingPath(dbPath);
        if (!File.Exists(pending)) return false;
        File.Delete(pending);
        logger.LogInformation("Staged database import discarded");
        return true;
    }

    /// <summary>
    /// Folds the write-ahead log into the file and removes its sidecars,
    /// so the file that is staged carries every committed page.
    /// </summary>
    private static async Task MakeSelfContainedAsync(string path, CancellationToken ct)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            DefaultTimeout = SqliteDatabase.BusyTimeoutSeconds,
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await command.ExecuteNonQueryAsync(ct);
        }
        SqliteDatabase.DeleteSidecars(path);
    }

    /// <summary>
    /// Opens the uploaded file as a zip, turning the reader's developer
    /// phrasing ("End of Central Directory record could not be found")
    /// into a sentence the settings page can show as-is.
    /// </summary>
    private ZipArchive OpenZipArchive(string path)
    {
        try
        {
            return ZipFile.OpenRead(path);
        }
        catch (InvalidDataException ex)
        {
            logger.LogWarning(ex, "Import rejected: the uploaded file is not a readable zip");
            throw new InvalidDataException("That file is not a readable zip archive.", ex);
        }
    }

    /// <summary>
    /// A zip whose melodybridge.db was copied raw from a WAL-mode database
    /// can lack its schema entirely (the tables live in the .db-wal that
    /// was never zipped). Fail early with a clear message instead of
    /// bricking the app on next boot.
    /// </summary>
    private static void RequireUsableDatabase(string path)
    {
        if (DatabaseRestore.HasSchema(path)) return;
        throw new InvalidOperationException(
            "The database in this zip has no tables. It was probably copied without its -wal file. Export again from a running MelodyBridge.");
    }

    private static string ZipName()
        => $"melodybridge_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip";

    private async Task<string> GetDatabasePathAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var connection = db.Database.GetConnectionString();
        // Data Source=/path/db;... -> /path/db
        var path = connection?.Split(';')
            .FirstOrDefault(s => s.TrimStart().StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
            ?.Replace("Data Source=", "", StringComparison.OrdinalIgnoreCase)
            .Trim();
        return string.IsNullOrWhiteSpace(path)
            ? throw new InvalidOperationException("Could not find the SQLite database path.")
            : path;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch { /* temp cleanup is best effort */ }
    }
}
