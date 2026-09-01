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
public sealed class DatabaseBackupService(
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
    /// Restores the melodybridge.db inside the uploaded zip, backing up the
    /// current file first. The caller must warn the user that a restart
    /// is required for the old connection to let go of the file.
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

            using (var archive = ZipFile.OpenRead(zipPath))
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

                entry.ExtractToFile(Path.Combine(tempDir, "restored.db"), overwrite: true);
            }

            // Refuse zips whose database carries no schema (copied without
            // its -wal sidecar) before touching the live file.
            RequireUsableDatabase(Path.Combine(tempDir, "restored.db"));

            var dbPath = await GetDatabasePathAsync(ct);

            // Park open pooled connections so the file is replaceable on
            // all platforms (Windows locks open files).
            SqliteConnection.ClearAllPools();

            BackupCurrent(dbPath);
            File.Move(Path.Combine(tempDir, "restored.db"), dbPath, overwrite: true);

            logger.LogInformation("Database restored from uploaded zip; restart required");
        }
        finally
        {
            TryDelete(tempDir);
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
        using var connection = new SqliteConnection($"Data Source={path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
        var tables = Convert.ToInt64(command.ExecuteScalar());
        if (tables == 0)
            throw new InvalidOperationException(
                "The database in this zip has no tables. It was probably copied without its -wal file. Export again from a running MelodyBridge.");
    }

    private static string ZipName()
        => $"melodybridge_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip";

    private void BackupCurrent(string dbPath)
    {
        if (!File.Exists(dbPath)) return;
        var backupPath = $"{dbPath}.{DateTime.UtcNow:yyyyMMdd_HHmmss}.preimport.bak";
        File.Move(dbPath, backupPath);
        logger.LogInformation("Pre-import database parked at {Path}", backupPath);
    }

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
