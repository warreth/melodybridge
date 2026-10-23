using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.Extensions.Logging;
using TagLib;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Infrastructure.Scanning;

public class LibraryScanner : ILibraryScanner
{
    /// <summary>
    /// One scan at a time, process-wide. Three drivers can start a scan on
    /// their own (the watcher, the schedule, the Library page), and two
    /// overlapping scans of the same location each created the row for a
    /// new file: both saw "missing" before either insert landed. Scans
    /// serialize now, so the second one waits and then sees the first
    /// one's rows.
    /// </summary>
    private static readonly SemaphoreSlim ScanGate = new(1, 1);
    private readonly HardLinkService _hardLinks = new();

    private readonly MelodyBridgeDbContext _db;
    private readonly ILogger<LibraryScanner> _logger;
    private readonly LibraryReconciler? _reconciler;

    public LibraryScanner(MelodyBridgeDbContext db, ILogger<LibraryScanner> logger,
        LibraryReconciler? reconciler = null)
    {
        _db = db;
        _logger = logger;
        _reconciler = reconciler;
    }

    public async Task<ScanReport> ScanAsync(IEnumerable<ScanLocation> paths, CancellationToken ct = default)
    {
        var (touchedRows, report) = await RunGatedScanAsync(paths, ct);

        // A scan must link the files it found to their playlist rows right
        // away; the reconcile runs after the scan's write commit, on its
        // own connection: the rows it needs are only visible once the
        // transaction landed.
        await ReconcileIfNeededAsync(touchedRows, ct);

        return report;
    }

    private async Task<(int TouchedRows, ScanReport Report)>
        RunGatedScanAsync(IEnumerable<ScanLocation> paths, CancellationToken ct)
    {
        await ScanGate.WaitAsync(ct);
        try
        {
            // SQLite walks its scan in one transaction (a failed file
            // cannot leave half a chunk behind); the InMemory provider the
            // shape alone tests run on has no transactions to begin.
            if (!_db.Database.IsRelational())
            {
                var plain = await ScanGatedAsync(paths, ct);
                return (plain.TouchedRows, new ScanReport(
                    plain.Locations, plain.Tagged, plain.Untagged, plain.Missing));
            }

            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            try
            {
                var result = await ScanGatedAsync(paths, ct);
                await tx.CommitAsync(ct);
                return (result.TouchedRows, new ScanReport(
                    result.Locations, result.Tagged, result.Untagged, result.Missing));
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
        finally
        {
            ScanGate.Release();
        }
    }

    private async Task<ScanResult> ScanGatedAsync(
        IEnumerable<ScanLocation> paths, CancellationToken ct)
    {
        const int ScanChunkSize = 200;
        var extensions = new[] { ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".wav", ".webm" };

        var locations = 0;
        var tagged = 0;
        var untagged = 0;
        var missing = new List<string>();
        // Never adopt an archive copy as a primary file, even when the
        // user points a scan location at the archive tree.
        var archiveRoots = await ArchiveRootsAsync(ct);
        var counters = new ScanRowChanges();

        foreach (var loc in paths)
        {
            if (string.IsNullOrWhiteSpace(loc.Path)) continue;
            locations++;
            if (!Directory.Exists(loc.Path))
            {
                _logger.LogWarning("Scan path missing: {path}", loc.Path);
                missing.Add(loc.Path);
                continue;
            }

            var files = Directory.EnumerateFiles(loc.Path, "*.*", SearchOption.AllDirectories)
                .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Where(f => !PlaylistRules.PathIsUnder(f, archiveRoots));

            // Chunked walking: every file is read once (the MELODY_ID tag
            // read is the walk's cost), and the database work happens in
            // batches. A row per file, one query per chunk, one write
            // transaction per chunk, and one failed file can no longer
            // trap every file after it in a poisoned context.
            var chunk = new List<(string Path, string MelodyId)>();
            foreach (var filePath in files)
            {
                ct.ThrowIfCancellationRequested();

                string id;
                try
                {
                    id = TaglibHelper.ReadMelodyId(filePath);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to inspect file {file}", filePath);
                    continue;
                }
                if (string.IsNullOrWhiteSpace(id))
                {
                    untagged++;
                    continue;
                }
                tagged++;
                chunk.Add((filePath, id));
                if (chunk.Count >= ScanChunkSize)
                {
                    await SaveChunkAsync(chunk, counters, ct);
                    chunk.Clear();
                }
            }
            if (chunk.Count > 0)
                await SaveChunkAsync(chunk, counters, ct);
        }

        return new ScanResult(locations, tagged, untagged, missing.ToArray(),
            counters.AddedRows, counters.MovedRows, counters.TouchedRows);
    }

    /// <summary>What one scan pass walked: the file counts the report
    /// carries and the row changes the reconcile decision needs.</summary>
    private sealed record ScanResult(
        int Locations, int Tagged, int Untagged, string[] Missing,
        int AddedRows, int MovedRows, int TouchedRows);

    /// <summary>What the scan's chunk actually changed on the rows.</summary>
    private sealed class ScanRowChanges
    {
        public int AddedRows;
        public int MovedRows;
        public int TouchedRows;
    }

    private async Task SaveChunkAsync(
        List<(string Path, string MelodyId)> chunk, ScanRowChanges counters,
        CancellationToken ct)
    {
        // One read for the chunk instead of one query per file.
        var ids = chunk.Select(f => f.MelodyId).Distinct().ToList();
        var existing = (await _db.Tracks
                .Where(t => ids.Contains(t.MelodyId))
                .ToListAsync(ct))
            .GroupBy(t => t.MelodyId!)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var (filePath, id) in chunk)
        {
            if (!existing.ContainsKey(id))
            {
                // A fresh file: the row comes from its tags. Best effort: a
                // file whose tags cannot be read joins the library on the
                // next scan instead of blocking this one.
                TryStageNewRow(filePath, id, counters, existing);
                continue;
            }
            await StageOneFileAsync(filePath, id, counters, existing, ct);
        }

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // One bad row must not cost the rest of the chunk: the whole
            // batch is rolled back, and each file saves on its own so a
            // bad row costs one file while the others keep their rows.
            // Detaching first is the point: an entity that stays Added in
            // the change tracker fails every later save of this context,
            // which is how one bad file could silence a whole library.
            _logger.LogWarning(ex, "Scan batch of {Count} files could not be saved; saving one file at a time", chunk.Count);
            _db.ChangeTracker.Clear();
            await SaveChunkPerFileAsync(chunk, counters, ct);
        }
    }

    /// <summary>
    /// Stages one fresh file (the tag ingest). A file whose tags cannot be
    /// read joins the library on the next scan instead of blocking here.
    /// </summary>
    private void TryStageNewRow(
        string filePath, string id, ScanRowChanges counters,
        Dictionary<string, TrackEntity> existing)
    {
        try
        {
            var row = NewRowFromTags(filePath, id);
            _db.Tracks.Add(row);
            existing[id] = row;
            counters.AddedRows++;
            counters.TouchedRows++;
        }
        catch
        {
            // The tag ingest is best effort: the file joins the library on
            // the next scan instead of blocking this one.
        }
    }

    /// <summary>
    /// Saves each staged file under its own save, after a chunk failed:
    /// one bad row costs one file instead of the whole batch.
    /// </summary>
    private async Task SaveChunkPerFileAsync(
        List<(string Path, string MelodyId)> chunk, ScanRowChanges counters, CancellationToken ct)
    {
        var ids = chunk.Select(f => f.MelodyId).Distinct().ToList();
        var existing = (await _db.Tracks
                .Where(t => ids.Contains(t.MelodyId))
                .ToListAsync(ct))
            .GroupBy(t => t.MelodyId!)
            .ToDictionary(g => g.Key, g => g.First());

        foreach (var (filePath, id) in chunk)
        {
            try
            {
                var changed = await StageOneFileAsync(filePath, id, counters, existing, ct);
                if (changed) await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to save the scan row for {file}; the file joins on the next scan", filePath);
                // A row that stays in the change tracker poisons the
                // context: detach whatever is pending after a failed save.
                _db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>One file's scan decision and tracking. True when it staged a
    /// change worth saving.</summary>
    private async Task<bool> StageOneFileAsync(
        string filePath, string id, ScanRowChanges counters,
        Dictionary<string, TrackEntity> existing, CancellationToken ct)
    {
        if (!existing.TryGetValue(id, out var row))
        {
            row = NewRowFromTags(filePath, id);
            _db.Tracks.Add(row);
            existing[id] = row;
            counters.AddedRows++;
            counters.TouchedRows++;
            return true;
        }

        if (PlaylistStore.PathsEqual(row.CurrentPath, filePath)) return false;

        var remembered = row.CurrentPath;
        var sameBytes = remembered is not null
            && System.IO.File.Exists(remembered)
            && _hardLinks.SameFile(remembered, filePath);
        if (sameBytes)
        {
            // The row already points at bytes it owns: the visit stamp is
            // all that changes.
            row.LastSeenAt = DateTime.UtcNow;
            counters.TouchedRows++;
            return true;
        }
        if (remembered is null || !System.IO.File.Exists(remembered))
        {
            // The remembered path is gone: this is the file the row moved
            // to (or a fresh copy the user made), so the verdicts that
            // described the old bytes go too.
            row.ReviewKind = null;
            row.ReviewText = null;
            row.SpectrumAccepted = false;
            row.CurrentPath = filePath;
            row.LastSeenAt = DateTime.UtcNow;
            counters.MovedRows++;
            counters.TouchedRows++;
            return true;
        }

        // The row's own file is still there. The scan does not drift the
        // row to whichever copy enumerated last; a hardlink twin in
        // another folder stays the row's own.
        return false;
    }

    private static TrackEntity NewRowFromTags(string filePath, string id)
    {
        try
        {
            var tf = TagLib.File.Create(filePath);
            var row = new TrackEntity
            {
                MelodyId = id,
                Title = tf.Tag.Title,
                Artist = tf.Tag.FirstPerformer,
                Album = tf.Tag.Album,
                MediaType = Path.GetExtension(filePath).TrimStart('.'),
                CurrentPath = filePath,
                LastSeenAt = DateTime.UtcNow,
            };
            if (tf.Properties?.AudioBitrate > 0)
                row.Bitrate = tf.Properties.AudioBitrate;
            Services.AudioProbe.Fill(row, filePath);
            return row;
        }
        catch
        {
            // A minimal record even if tagging fails.
            var row = new TrackEntity
            {
                MelodyId = id,
                CurrentPath = filePath,
                MediaType = Path.GetExtension(filePath).TrimStart('.'),
                LastSeenAt = DateTime.UtcNow,
            };
            Services.AudioProbe.Fill(row, filePath);
            return row;
        }
    }

    /// <summary>
    /// A scan must link the files it found to their playlist rows right
    /// away: without this, a track that exists on disk stays "pending"
    /// until the next restart. The whole-library reconcile is expensive
    /// and the watcher may trip it every few seconds, so a reconcile that
    /// just finished (and found the library in the same state) is not
    /// paid for again when this scan added or moved nothing.
    /// </summary>
    private static DateTime? LastReconcileCompleted;
    private static readonly TimeSpan ReconcileTtl = TimeSpan.FromSeconds(30);

    internal static void MarkReconcileCompleted() => LastReconcileCompleted = DateTime.UtcNow;
    internal static TimeSpan? SinceLastReconcile() => LastReconcileCompleted is null
        ? null : DateTime.UtcNow - LastReconcileCompleted;

    internal static void ResetReconcileClock() => LastReconcileCompleted = null;

    private async Task ReconcileIfNeededAsync(int touchedRows, CancellationToken ct)
    {
        if (_reconciler is null) return;
        var since = SinceLastReconcile();
        if (touchedRows == 0 && since is { } age && age < ReconcileTtl)
        {
            _logger.LogDebug(
                "Skipping the post-scan reconcile: the library state is under the {Seconds}s old one",
                ReconcileTtl.TotalSeconds);
            return;
        }
        try
        {
            await _reconciler.ReconcileAllAsync(ct);
            MarkReconcileCompleted();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reconcile after scan failed");
            MarkReconcileCompleted();
        }
    }

    /// <summary>Archive roots of the library: playlist archive folders plus
    /// the app-wide archive path.</summary>
    private async Task<List<string>> ArchiveRootsAsync(CancellationToken ct)
    {
        var roots = await _db.Playlists.AsNoTracking()
            .Where(p => p.ArchiveDirectory != null && p.ArchiveDirectory != "")
            .Select(p => p.ArchiveDirectory!)
            .ToListAsync(ct);
        var global = await _db.DownloaderSettings.AsNoTracking()
            .Where(s => s.Key == "archive_path")
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(global)) roots.Add(global);
        return roots;
    }
}
