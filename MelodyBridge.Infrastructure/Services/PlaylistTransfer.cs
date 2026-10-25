using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Backup and restore for stored playlists: the JSON export the About tab
/// offers, the CSV export of one playlist's snapshot and the import of a
/// previous JSON export (which refreshes existing playlists instead of
/// duplicating them).
/// </summary>
internal sealed class PlaylistTransfer
{
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly PlaylistQueries _queries;
    private readonly PlaylistSnapshots _snapshots;
    private readonly ILogger<PlaylistTransfer> _logger;

    public PlaylistTransfer(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        PlaylistQueries queries,
        PlaylistSnapshots snapshots,
        ILogger<PlaylistTransfer>? logger = null)
    {
        _dbFactory = dbFactory;
        _queries = queries;
        _snapshots = snapshots;
        _logger = logger ?? NullLogger<PlaylistTransfer>.Instance;
    }

    /// <summary>Export persisted playlists (with tracks) as JSON for backup.</summary>
    public async Task<string> ExportAsync(IEnumerable<string>? playlistIds = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.Playlists.Include(p => p.Tracks).AsNoTracking().AsQueryable();
        if (playlistIds?.Any() == true)
            query = query.Where(p => playlistIds.Contains(p.Id));

        var playlists = await query.ToListAsync(ct);
        return JsonSerializer.Serialize(playlists, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>one playlist's track snapshot as utf-8 csv bytes, bom prepended so excel behaves.</summary>
    public async Task<byte[]> ExportCsvAsync(string playlistId, CancellationToken ct = default)
    {
        var entity = await _queries.GetForDisplayAsync(playlistId, ct)
            ?? throw new InvalidOperationException($"Playlist '{playlistId}' not found");
        return ExportCsvOf(entity);
    }

    /// <summary>Same rows from an already loaded playlist: callers that
    /// hold the entity (the bulk zip) do not re-read it per entry.</summary>
    public byte[] ExportCsvOf(PlaylistEntity entity)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Position,Title,Artist,Album,DurationMs,Status,BitrateKbps,SampleRateHz,MediaType,FileSizeBytes,Filename");
        foreach (var t in entity.Tracks)
        {
            sb.Append(t.Position + 1).Append(',')
                .Append(Csv.Escape(t.Title)).Append(',')
                .Append(Csv.Escape(t.Artist)).Append(',')
                .Append(Csv.Escape(t.Album)).Append(',')
                .Append(t.DurationMs).Append(',')
                .Append(Csv.Escape(t.DownloadStatus)).Append(',')
                .Append(t.Bitrate).Append(',')
                .Append(t.SampleRateHz).Append(',')
                .Append(Csv.Escape(t.MediaType)).Append(',')
                .Append(t.FileSizeBytes).Append(',')
                .AppendLine(Csv.Escape(t.CurrentPath is null ? string.Empty : Path.GetFileName(t.CurrentPath)));
        }

        // bom first: without it excel guesses a legacy codepage and mangles titles
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    /// <summary>
    /// Several playlists' CSVs in one archive, for the bulk export action.
    /// Browsers block a burst of automatic downloads, so one zip is the
    /// honest way to hand over more than one file at a time.
    /// </summary>
    public async Task<byte[]> ExportCsvZipAsync(
        IEnumerable<string> playlistIds, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var playlistId in playlistIds.Distinct(StringComparer.Ordinal))
            {
                var entity = await _queries.GetForDisplayAsync(playlistId, ct);
                if (entity is null) continue;

                var bytes = ExportCsvOf(entity);
                var baseName = SafeFileNames.Sanitize(entity.Name);
                if (string.IsNullOrWhiteSpace(baseName)) baseName = "playlist";

                var name = baseName;
                for (var suffix = 2; !usedNames.Add(name); suffix++)
                    name = $"{baseName} ({suffix})";

                var entry = archive.CreateEntry(name + ".csv", CompressionLevel.Optimal);
                await using var stream = entry.Open();
                await stream.WriteAsync(bytes, ct);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>Import playlists from a previous export; existing ones are refreshed instead.</summary>
    public async Task<int> ImportAsync(string json, CancellationToken ct = default)
    {
        var incoming = JsonSerializer.Deserialize<List<PlaylistEntity>>(json);
        if (incoming is null || incoming.Count == 0) return 0;

        var imported = 0;
        foreach (var playlist in incoming.Where(p => !string.IsNullOrWhiteSpace(p.SourceUrl)))
        {
            try
            {
                if (playlist.IsManualImport)
                {
                    // Manual imports have no live source: they come back as
                    // the exported snapshot, not as a Spotify fetch of a
                    // slug that only looks like a playlist id.
                    await _snapshots.RestoreManualSnapshotAsync(playlist, ct);
                    imported++;
                    continue;
                }
                await _snapshots.AddOrRefreshAsync(playlist.SourceUrl, playlist.TargetDirectory, ct);
                imported++;
            }
            catch (Exception ex)
            {
                // One row that cannot come back (a provider reject, a bad
                // source id) leaves the other rows untouched: the import
                // reports what it got instead of killing the restore.
                _logger.LogWarning(ex,
                    "Playlist '{Name}' ({Source}) did not import", playlist.Name, playlist.SourceUrl);
            }
        }
        return imported;
    }
}
