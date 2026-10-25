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
        ("Tracks", "IsHardLink", "INTEGER NOT NULL DEFAULT 0"),
        ("SyncJobRuns", "WarningDetails", "TEXT NULL"),
        ("Playlists", "ScheduleCron", "TEXT NULL"),
        // Secondary archive target: one download, two library files.
        ("Tracks", "ArchivePath", "TEXT NULL"),
        // The size of the archive copy, so the Library page does not stat
        // every archive file on every visit.
        ("Tracks", "ArchiveSizeBytes", "INTEGER NULL"),
        ("Playlists", "ArchiveDirectory", "TEXT NULL"),
        ("Playlists", "ArchiveFormat", "TEXT NULL"),
        // Partial-sync honesty: the reason a sync was not a clean Completed.
        ("Playlists", "SyncWarning", "TEXT NULL"),
        // ISRC for exact matching on catalogs that index it.
        ("Tracks", "Isrc", "TEXT NULL"),
        // One-shot "Download new" decision from a dedup review.
        ("Tracks", "SkipDedup", "INTEGER NOT NULL DEFAULT 0"),
        // Download metadata that makes the file usable in a media server:
        // the release credit, the cover to embed and the resolved lyrics.
        ("Tracks", "AlbumArtist", "TEXT NULL"),
        ("Tracks", "CoverUrl", "TEXT NULL"),
        ("Tracks", "Lyrics", "TEXT NULL"),
        // Opt-in: download new tracks right after an auto-sync refresh.
        ("Playlists", "AutoDownloadEnabled", "INTEGER NOT NULL DEFAULT 0"),
        // Quality strictness per playlist: strict delivers the requested
        // quality or nothing; non-strict steps down the declared ladder,
        // optionally changing the container.
        ("Playlists", "StrictQuality", "INTEGER NOT NULL DEFAULT 1"),
        ("Playlists", "AllowContainerChange", "INTEGER NOT NULL DEFAULT 1"),
        // Last-run resolved counts so the job card can flag a "Completed"
        // run that actually resolved nothing.
        ("SyncJobs", "LastRunResolvedTracks", "INTEGER NOT NULL DEFAULT 0"),
        ("SyncJobs", "LastRunTotalTracks", "INTEGER NOT NULL DEFAULT 0"),
        // Flagged quality reviews: a file that failed the post-download
        // length or spectrum check stays in the library and waits for the
        // user's Keep or Retry. SourcePluginId remembers who delivered it;
        // AvoidPluginId is the one-shot "not from here" hint of Retry.
        ("Tracks", "SourcePluginId", "TEXT NULL"),
        ("Tracks", "ReviewKind", "TEXT NULL"),
        ("Tracks", "ReviewText", "TEXT NULL"),
        ("Tracks", "AvoidPluginId", "TEXT NULL"),
        // Per-playlist archive switch: off skips the copy even when the
        // app-wide archive path is set.
        ("Playlists", "ArchiveEnabled", "INTEGER NOT NULL DEFAULT 1"),
        // Sync jobs select a media server profile instead of carrying
        // raw credentials; legacy inline values still work.
        ("SyncJobs", "MediaServerProfileId", "TEXT NULL"),
        // First-run anchor: a new scheduled job waits for its next fire.
        // Rows from before this column keep their old semantics (no anchor
        // at all means due immediately on the next poll).
        ("SyncJobs", "CreatedAt", "TEXT NULL"),
        // Multi-target jobs: the list of M3U paths and media server
        // profiles as JSON. Null rows keep the legacy single target.
        ("SyncJobs", "TargetsJson", "TEXT NULL"),
        // M3U entries relative to the output file instead of absolute paths.
        ("SyncJobs", "M3uRelativePaths", "INTEGER NOT NULL DEFAULT 0"),
        // Auto-sync attempt stamp: a failed refresh waits for the next
        // interval instead of retrying on every scheduler poll.
        ("Playlists", "LastAttemptAt", "TEXT NULL"),
        // A spectrum review the user settled with Keep: recompute must not
        // reopen it for the same file.
        ("Tracks", "SpectrumAccepted", "INTEGER NOT NULL DEFAULT 0"),
        // Pinned playlists stay at the top of every sort order.
        ("Playlists", "IsPinned", "INTEGER NOT NULL DEFAULT 0"),
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

        // One-time rebuild: the MelodyId index used to be unique, which
        // broke the moment one track appeared in two playlists (dedup
        // joins rows across playlists on this id). SQLite cannot drop
        // the uniqueness of an existing index, so the old index goes and
        // a plain one takes its place.
        await DropIndexIfUniqueAsync(db, "IX_Tracks_MelodyId", ct);

        // The per-playlist counters read four columns of every track row.
        // An index that holds exactly those turns the group-by into a
        // walk over the index, which is a lot cheaper than over the
        // table once a library holds tens of thousands of tracks.
        await CreateIndexIfMissingAsync(
            db,
            "IX_Tracks_PlaylistEntityId_DownloadStatus_FileSizeBytes_ReviewKind",
            "CREATE INDEX IX_Tracks_PlaylistEntityId_DownloadStatus_FileSizeBytes_ReviewKind "
                + "ON Tracks (PlaylistEntityId, DownloadStatus, FileSizeBytes, ReviewKind)",
            ct);

        // Ownership checks (dedup link, replaced file, delete) ask by path
        // on every download, so a full track-table scan per check is the
        // cost this removes.
        await CreateIndexIfMissingAsync(
            db, "IX_Tracks_CurrentPath",
            "CREATE INDEX IX_Tracks_CurrentPath ON Tracks (CurrentPath)", ct);
        await CreateIndexIfMissingAsync(
            db, "IX_Tracks_ArchivePath",
            "CREATE INDEX IX_Tracks_ArchivePath ON Tracks (ArchivePath)", ct);

        // The dashboard reads the newest runs every refresh and a job's log
        // filters by job; the table only grows over an install's life.
        await CreateIndexIfMissingAsync(
            db, "IX_SyncJobRuns_Timestamp",
            "CREATE INDEX IX_SyncJobRuns_Timestamp ON SyncJobRuns (Timestamp)", ct);
        await CreateIndexIfMissingAsync(
            db, "IX_SyncJobRuns_SyncJobId_Timestamp",
            "CREATE INDEX IX_SyncJobRuns_SyncJobId_Timestamp "
                + "ON SyncJobRuns (SyncJobId, Timestamp)", ct);
    }

    /// <summary>
    /// Creates an index when the database does not have it yet. Idempotent:
    /// a fresh install gets the index from the model, an older one gets it
    /// here the first time it starts.
    /// </summary>
    private static async Task CreateIndexIfMissingAsync(
        MelodyBridgeDbContext db, string indexName, string createSql, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using (var check = connection.CreateCommand())
        {
            check.CommandText = $"SELECT sql FROM sqlite_master WHERE type='index' AND name='{indexName}'";
            if (await check.ExecuteScalarAsync(ct) is not null) return;
        }

        await db.Database.ExecuteSqlRawAsync(createSql, ct);
    }

    /// <summary>
    /// Rebuilds the MelodyId index as non-unique when the existing one
    /// still carries the UNIQUE constraint. Idempotent: a database that
    /// never had the unique form (fresh installs) skips the rebuild.
    /// </summary>
    private static async Task DropIndexIfUniqueAsync(
        MelodyBridgeDbContext db, string indexName, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);

        string? createSql = null;
        await using (var check = connection.CreateCommand())
        {
            check.CommandText = $"SELECT sql FROM sqlite_master WHERE type='index' AND name='{indexName}'";
            createSql = await check.ExecuteScalarAsync(ct) as string;
        }

        if (createSql is null || !createSql.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase))
            return;

        await db.Database.ExecuteSqlRawAsync($"DROP INDEX {indexName}", ct);
        await db.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IX_Tracks_MelodyId ON Tracks (MelodyId)", ct);
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
