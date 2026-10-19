using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Tagging;
using TagLib;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Infrastructure.Scanning;

public class LibraryScanner : ILibraryScanner
{
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
        var extensions = new[] { ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".wav", ".webm" };

        var locations = 0;
        var tagged = 0;
        var untagged = 0;
        var missing = new List<string>();
        // Never adopt an archive copy as a primary file, even when the
        // user points a scan location at the archive tree.
        var archiveRoots = await ArchiveRootsAsync(ct);

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

            foreach (var filePath in files)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var id = TaglibHelper.ReadMelodyId(filePath);
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        untagged++;
                        continue;
                    }
                    tagged++;

                    var existing = await _db.Tracks.FirstOrDefaultAsync(t => t.MelodyId == id, ct);
                    if (existing == null)
                    {
                        try
                        {
                            var tf = TagLib.File.Create(filePath);
                            existing = new TrackEntity
                            {
                                MelodyId = id,
                                Title = tf.Tag.Title,
                                Artist = tf.Tag.FirstPerformer,
                                Album = tf.Tag.Album,
                                MediaType = Path.GetExtension(filePath).TrimStart('.'),
                                CurrentPath = filePath,
                                LastSeenAt = DateTime.UtcNow,
                            };

                            // Extract bitrate if available
                            if (tf.Properties?.AudioBitrate > 0)
                                existing.Bitrate = tf.Properties.AudioBitrate;

                            Services.AudioProbe.Fill(existing, filePath);
                            _db.Tracks.Add(existing);
                        }
                        catch
                        {
                            // Add a minimal record even if tagging fails
                            existing = new TrackEntity
                            {
                                MelodyId = id,
                                CurrentPath = filePath,
                                MediaType = Path.GetExtension(filePath).TrimStart('.'),
                                LastSeenAt = DateTime.UtcNow,
                            };
                            Services.AudioProbe.Fill(existing, filePath);
                            _db.Tracks.Add(existing);
                        }
                    }
                    else
                    {
                        if (!PlaylistStore.PathsEqual(existing.CurrentPath, filePath))
                        {
                            // The row now points at different bytes: the
                            // old review and any settled spectrum verdict
                            // described the previous file.
                            existing.ReviewKind = null;
                            existing.ReviewText = null;
                            existing.SpectrumAccepted = false;
                        }
                        existing.CurrentPath = filePath;
                        existing.LastSeenAt = DateTime.UtcNow;
                        _db.Tracks.Update(existing);
                    }

                    await _db.SaveChangesAsync(ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to inspect file {file}", filePath);
                }
            }
        }

        // A scan must link the files it found to their playlist rows
        // right away: without this, a track that exists on disk stays
        // "pending" until the next restart.
        if (_reconciler is not null)
        {
            try
            {
                await _reconciler.ReconcileAllAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Reconcile after scan failed");
            }
        }

        return new ScanReport(locations, tagged, untagged, missing.ToArray());
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
