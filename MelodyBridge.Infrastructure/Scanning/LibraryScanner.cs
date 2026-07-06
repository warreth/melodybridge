using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using TagLib;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Infrastructure.Scanning;

public class LibraryScanner : ILibraryScanner
{
    private readonly MelodyBridgeDbContext _db;
    private readonly ILogger<LibraryScanner> _logger;

    public LibraryScanner(MelodyBridgeDbContext db, ILogger<LibraryScanner> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task ScanAsync(IEnumerable<ScanLocation> paths, CancellationToken cancellationToken = default)
    {
        var extensions = new[] { ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".wav", ".webm" };

        foreach (var loc in paths)
        {
            if (string.IsNullOrWhiteSpace(loc.Path)) continue;
            if (!Directory.Exists(loc.Path))
            {
                _logger.LogWarning("Scan path missing: {path}", loc.Path);
                continue;
            }

            var files = Directory.EnumerateFiles(loc.Path, "*.*", SearchOption.AllDirectories)
                .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase));

            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var id = MelodyBridge.Infrastructure.Tagging.TaglibHelper.ReadMelodyId(filePath);
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        // Try reading custom TXXX or other tags if present (not implemented yet)
                        continue;
                    }

                    var existing = await _db.Tracks.FirstOrDefaultAsync(t => t.MelodyId == id, cancellationToken);
                    if (existing == null)
                    {
                        var tf = TagLib.File.Create(filePath);
                        var te = new TrackEntity
                        {
                            MelodyId = id,
                            Title = tf.Tag.Title,
                            Artist = tf.Tag.FirstPerformer,
                            MediaType = Path.GetExtension(filePath),
                            CurrentPath = filePath
                        };
                        _db.Tracks.Add(te);
                    }
                    else
                    {
                        existing.CurrentPath = filePath;
                        _db.Tracks.Update(existing);
                    }

                    await _db.SaveChangesAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to inspect file {file}", filePath);
                }
            }
        }
    }

    private string? ExtractMelodyId(string comment)
    {
        if (string.IsNullOrEmpty(comment)) return null;
        var marker = "MELODY_ID=";
        var idx = comment.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var after = comment.Substring(idx + marker.Length).Trim();
        var parts = after.Split(new[] { ' ', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : after;
    }
}
