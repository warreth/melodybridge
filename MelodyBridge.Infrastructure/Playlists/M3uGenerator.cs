using MelodyBridge.Core;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Playlists;

/// <summary>
/// Writes standard M3U playlists with #EXTINF metadata lines
/// (duration, artist - title) so media players show proper labels.
/// </summary>
public class M3uGenerator
{
    private readonly ILogger<M3uGenerator> _logger;

    public M3uGenerator(ILogger<M3uGenerator> logger)
    {
        _logger = logger;
    }

    public Task<string> GenerateM3uAsync(
        MelodyBridge.Core.Playlist playlist,
        IEnumerable<ScanLocation> searchLocations,
        PlaylistOutputOptions options,
        CancellationToken ct = default)
    {
        if (playlist?.Tracks == null)
            throw new ArgumentException("Playlist has no tracks");
        if (string.IsNullOrWhiteSpace(options.OutputPath))
            throw new ArgumentException("OutputPath required");

        var lines = new List<string> { "#EXTM3U" };
        var skipped = 0;

        foreach (var track in playlist.Tracks)
        {
            ct.ThrowIfCancellationRequested();

            var path = track.CurrentTrackLocation?.Path;
            if (string.IsNullOrEmpty(path))
            {
                skipped++;
                continue;
            }

            // Path remap: media servers often see files under a different mount.
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

            if (options.UseRelativePaths)
            {
                try
                {
                    path = Path.GetRelativePath(
                        Path.GetDirectoryName(options.OutputPath) ?? ".", path);
                }
                catch { /* keep absolute */ }
            }

            // #EXTINF:<seconds>,<artist> - <title>
            var seconds = (int)Math.Round(track.Duration?.TotalSeconds ?? -1);
            var label = string.IsNullOrEmpty(track.Artist)
                ? (track.Title ?? "Unknown")
                : $"{track.Artist} - {track.Title}";
            lines.Add($"#EXTINF:{seconds},{label}");
            lines.Add(path);
        }

        if (skipped > 0)
            _logger.LogWarning("M3U {Output}: skipped {Skipped} track(s) without a file",
                options.OutputPath, skipped);

        Directory.CreateDirectory(Path.GetDirectoryName(options.OutputPath) ?? ".");
        File.WriteAllLines(options.OutputPath, lines);

        if (skipped > 0 || lines.Count <= 1)
            _logger.LogInformation("M3U {Output}: {Tracks} track(s), {Skipped} skipped",
                options.OutputPath, lines.Count / 2, skipped);

        return Task.FromResult(options.OutputPath);
    }
}
