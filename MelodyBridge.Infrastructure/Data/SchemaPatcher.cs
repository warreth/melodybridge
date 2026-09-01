using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Data;

/// <summary>
/// Lightweight schema migration for the SQLite database. EnsureCreated
/// builds a fresh schema but never upgrades an existing one, so releases
/// that add columns need this. Each step checks SQLite's table_info and
/// only alters when the column is missing, making it idempotent and cheap
/// to call on every boot.
/// </summary>
public static class SchemaPatcher
{
    /// <summary>
    /// Every column ever added after first release, in order.
    /// (table, column, column definition)
    /// </summary>
    private static readonly (string Table, string Column, string Definition)[] Columns =
    {
        ("Tracks", "SampleRateHz", "INTEGER NULL"),
        ("Tracks", "FileSizeBytes", "INTEGER NULL"),
        ("Tracks", "PlaylistEntityId", "TEXT NULL"),
    };

    public static async Task PatchAsync(MelodyBridgeDbContext db, CancellationToken ct = default)
    {
        foreach (var (table, column, definition) in Columns)
        {
            var existing = await GetColumnsAsync(db, table, ct);
            if (existing.Contains(column, StringComparer.OrdinalIgnoreCase))
                continue;
            await db.Database.ExecuteSqlRawAsync($"ALTER TABLE {table} ADD COLUMN {column} {definition}", ct);
        }
    }

    private static async Task<HashSet<string>> GetColumnsAsync(
        MelodyBridgeDbContext db, string table, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            columns.Add(reader.GetString(1));
        return columns;
    }
}
