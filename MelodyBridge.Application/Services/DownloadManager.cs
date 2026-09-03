using System.Collections.Concurrent;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Application.Services;

/// <summary>
/// Coordinates downloader plugins (the waterfall): given track metadata,
/// search and download through enabled plugins in priority order.
/// </summary>
public class DownloadManager : IDownloadManager
{
    private readonly IDownloaderRegistry _registry;
    private readonly ILogger<DownloadManager> _logger;

    public DownloadManager(IDownloaderRegistry registry, ILogger<DownloadManager> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    /// <summary>Live progress reporting per melodyId, for the UI.</summary>
    private readonly ConcurrentDictionary<string, DownloadProgress> _progress = new();

    public IReadOnlyList<DownloadProgress> SnapshotProgress()
        => _progress.Values.ToList();

    /// <inheritdoc />
    public async Task<string?> DownloadAsync(string sourceUrl, string outputDirectory, string melodyId, CancellationToken ct = default)
    {
        // Direct URL case: let plugins download the URL as-is.
        foreach (var downloader in _registry.GetEnabled())
        {
            if (!await downloader.IsAvailableAsync(ct))
            {
                _logger.LogDebug("Skipping {Name}: unavailable", downloader.Name);
                continue;
            }

            try
            {
                var result = await downloader.DownloadAsync(sourceUrl, outputDirectory, melodyId, null, ct);
                if (result.Success && result.FilePath is not null)
                    return result.FilePath;
                _logger.LogWarning("{Name} failed for {Url}: {Error}", downloader.Name, sourceUrl, result.ErrorMessage);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "{Name} threw for {Url}", downloader.Name, sourceUrl);
            }
        }
        return null;
    }

    /// <summary>
    /// The main entry for playlist tracks: search by artist/title through the
    /// plugin waterfall and download the first hit.
    /// </summary>
    public async Task<string?> DownloadTrackAsync(
        string artist, string title, string outputDirectory, string melodyId,
        DownloadQuality? quality = null, CancellationToken ct = default)
    {
        quality ??= DownloadQuality.Any;
        _progress[melodyId] = new DownloadProgress(melodyId, title, "searching", null, null);
        try
        {
            foreach (var downloader in _registry.GetEnabled())
            {
                if (!await downloader.IsAvailableAsync(ct))
                {
                    _logger.LogDebug("Skipping {Name}: unavailable", downloader.Name);
                    continue;
                }

                var hit = await downloader.SearchAsync(artist, title, quality, ct);
                if (hit is null || hit.SourceUrl is null)
                {
                    _logger.LogDebug("{Name}: no search hit for '{Artist} - {Title}'", downloader.Name, artist, title);
                    continue;
                }

                // Quality gate: reject reported bitrates outside the requested band.
                if (!quality.IsWithinBand(hit.BitrateKbps))
                {
                    _logger.LogInformation(
                        "{Name} hit for '{Title}' is {Hit} kbps, outside the requested {Min}–{Max} kbps band; skipping",
                        downloader.Name, title, hit.BitrateKbps,
                        quality.MinKbps?.ToString() ?? "any", quality.MaxKbps?.ToString() ?? "any");
                    continue;
                }

                _progress[melodyId] = new DownloadProgress(
                    melodyId, title, "downloading", downloader.Name, null, hit.MatchConfidence);
                var result = await downloader.DownloadAsync(hit.SourceUrl, outputDirectory, melodyId, quality, ct);
                if (result.Success && result.FilePath is not null)
                {
                    // Enforce the band on the real file: plugins can over-promise,
                    // the measurement never lies.
                    var measured = BitrateProbe.MeasureKbps(result.FilePath);
                    if (!quality.IsWithinBand(measured))
                    {
                        _logger.LogInformation(
                            "{Name} produced {Measured} kbps for '{Title}', outside the requested {Min}–{Max} kbps band; rejecting",
                            downloader.Name, measured, title,
                            quality.MinKbps?.ToString() ?? "any", quality.MaxKbps?.ToString() ?? "any");
                        try { System.IO.File.Delete(result.FilePath); } catch { /* best effort */ }
                        continue;
                    }

                    _progress[melodyId] = new DownloadProgress(
                        melodyId, title, "done", downloader.Name, result.FilePath, hit.MatchConfidence,
                        Warning: measured is null ? "bitrate could not be verified" : null);
                    return result.FilePath;
                }

                _logger.LogWarning("{Name} download failed for '{Artist} - {Title}': {Error}",
                    downloader.Name, artist, title, result.ErrorMessage);
            }

            _progress[melodyId] = new DownloadProgress(melodyId, title, "failed", null, "No plugin could download this track");
            return null;
        }
        catch (Exception ex)
        {
            _progress[melodyId] = new DownloadProgress(melodyId, title, "failed", null, ex.Message);
            throw;
        }
    }
}
