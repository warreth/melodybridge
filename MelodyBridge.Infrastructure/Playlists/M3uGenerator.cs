using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Infrastructure.Playlists;

public class M3uGenerator
{
    private readonly MelodyBridgeDbContext _db;
    private readonly ILogger<M3uGenerator> _logger;

    public M3uGenerator(MelodyBridgeDbContext db, ILogger<M3uGenerator> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<string> GenerateM3uAsync(Playlist playlist, IEnumerable<ScanLocation> searchLocations, PlaylistOutputOptions options, CancellationToken ct = default)
    {
        // Resolve tracks by MelodyId — Playlist.Tracks should contain Track with SongID as identifier
        var lines = new List<string> { "#EXTM3U" };

        if (playlist.Tracks == null)
            throw new ArgumentException("Playlist has no tracks");

        foreach (var track in playlist.Tracks)
        {
            ct.ThrowIfCancellationRequested();
            var melodyId = track.SongID?.ID;
            if (string.IsNullOrEmpty(melodyId)) continue;
            var found = await _db.Tracks.FirstOrDefaultAsync(t => t.MelodyId == melodyId, ct);
            if (found == null)
            {
                _logger.LogWarning("Track {id} not found in DB", melodyId);
                continue;
            }

            var path = found.CurrentPath ?? string.Empty;
            if (options.PathRemap != null)
            {
                foreach (var kv in options.PathRemap)
                {
                    if (path.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        path = kv.Value + path.Substring(kv.Key.Length);
                        break;
                    }
                }
            }

            if (options.UseRelativePaths && !string.IsNullOrEmpty(options.OutputPath))
            {
                try
                {
                    var rel = Path.GetRelativePath(Path.GetDirectoryName(options.OutputPath) ?? string.Empty, path);
                    path = rel;
                }
                catch { }
            }

            lines.Add(path);
        }

        var outPath = options.OutputPath;
        if (string.IsNullOrWhiteSpace(outPath))
            throw new ArgumentException("OutputPath required");

        Directory.CreateDirectory(Path.GetDirectoryName(outPath) ?? ".");
        await File.WriteAllLinesAsync(outPath, lines, ct);
        return outPath;
    }
}
