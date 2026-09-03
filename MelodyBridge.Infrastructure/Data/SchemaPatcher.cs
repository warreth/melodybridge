using MelodyBridge.Core;
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
        ("SyncJobRuns", "WarningDetails", "TEXT NULL"),
        ("Playlists", "ScheduleCron", "TEXT NULL"),
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

        // One-time backfill: playlists used to store auto-sync as a boolean
        // plus a minutes column; schedules are one cron string now. A row
        // that already carries a schedule is left alone.
        var legacy = await db.Playlists
            .Where(p => p.ScheduleCron == null && p.AutoSyncEnabled)
            .ToListAsync(ct);
        if (legacy.Count > 0)
        {
            foreach (var playlist in legacy)
                playlist.ScheduleCron = $"*/{playlist.AutoSyncIntervalMinutes ?? 60} * * * *";
            await db.SaveChangesAsync(ct);
        }

        // One-time backfill: file imports (Exportify CSV, Spotify privacy
        // export) used to be stored as Spotify playlists with a
        // spotify:import: URL. They are manual snapshots with no live
        // source: the platform moves to Unknown so the UI labels them
        // Imported and stops offering refresh and auto-sync.
        var imports = await db.Playlists
            .Where(p => p.SourceUrl.StartsWith("spotify:import:")
                        && p.SourcePlatform == Platform.Spotify)
            .ToListAsync(ct);
        if (imports.Count > 0)
        {
            foreach (var playlist in imports)
                playlist.SourcePlatform = Platform.Unknown;
            await db.SaveChangesAsync(ct);
        }

        // One-time backfill: rows that carry an external id but no MelodyId
        // (rare; mostly restored or hand-edited databases) get the same
        // deterministic id a fresh snapshot would produce. Rows that already
        // have an id keep it - their files carry that tag on disk.
        var untagged = await db.Tracks
            .Where(t => t.MelodyId == null && t.ExternalId != null)
            .ToListAsync(ct);
        if (untagged.Count > 0)
        {
            foreach (var track in untagged)
                track.MelodyId = MelodyIds.For(track.ExternalPlatform, track.ExternalId);
            await db.SaveChangesAsync(ct);
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
