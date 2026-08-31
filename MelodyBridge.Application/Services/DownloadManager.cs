using System.Collections.Concurrent;
using MelodyBridge.Core;
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
                var result = await downloader.DownloadAsync(sourceUrl, outputDirectory, melodyId, ct);
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
        int minimumKbps = 128, CancellationToken ct = default)
    {
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

                var hit = await downloader.SearchAsync(artist, title, minimumKbps, ct);
                if (hit is null || hit.SourceUrl is null)
                {
                    _logger.LogDebug("{Name}: no search hit for '{Artist} - {Title}'", downloader.Name, artist, title);
                    continue;
                }

                // Quality floor: reject reported bitrates below what the caller wants.
                if (hit.BitrateKbps is > 0 && hit.BitrateKbps < minimumKbps)
                {
                    _logger.LogInformation(
                        "{Name} hit for '{Title}' is {Hit} kbps, below the {Min} kbps floor; skipping",
                        downloader.Name, title, hit.BitrateKbps, minimumKbps);
                    continue;
                }

                _progress[melodyId] = new DownloadProgress(melodyId, title, "downloading", downloader.Name, null);
                var result = await downloader.DownloadAsync(hit.SourceUrl, outputDirectory, melodyId, ct);
                if (result.Success && result.FilePath is not null)
                {
                    _progress[melodyId] = new DownloadProgress(melodyId, title, "done", downloader.Name, result.FilePath);
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
