using System.IO.Compression;
using System.Text;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

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

    public PlaylistTransfer(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        PlaylistQueries queries,
        PlaylistSnapshots snapshots)
    {
        _dbFactory = dbFactory;
        _queries = queries;
        _snapshots = snapshots;
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
        var entity = await _queries.GetByIdAsync(playlistId, ct)
            ?? throw new InvalidOperationException($"Playlist '{playlistId}' not found");

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
                var entity = await _queries.GetByIdAsync(playlistId, ct);
                if (entity is null) continue;

                var bytes = await ExportCsvAsync(playlistId, ct);
                var baseName = string.Concat(entity.Name.Select(c =>
                    Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
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
            await _snapshots.AddOrRefreshAsync(playlist.SourceUrl, playlist.TargetDirectory, ct);
            imported++;
        }
        return imported;
    }
}
