using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using MelodyBridge.Core;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Application.Services;

public class DownloadManager
{
    private readonly IEnumerable<IAsyncDownloader> _legacyDownloaders;
    private readonly IMusicProviderRegistry _providerRegistry;
    private readonly ILogger<DownloadManager> _logger;

    public DownloadManager(
        IEnumerable<IAsyncDownloader> legacyDownloaders,
        IMusicProviderRegistry providerRegistry,
        ILogger<DownloadManager> logger)
    {
        _legacyDownloaders = legacyDownloaders;
        _providerRegistry = providerRegistry;
        _logger = logger;
    }

    public async Task<string?> DownloadAsync(string sourceUrl, string outputDirectory, string melodyId, CancellationToken ct = default)
    {
        // Try legacy downloaders first (e.g. yt-dlp)
        var legacy = _legacyDownloaders.FirstOrDefault(d => d.CanHandle(sourceUrl));
        if (legacy != null)
        {
            try
            {
                return await legacy.DownloadAsync(sourceUrl, outputDirectory, melodyId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Legacy downloader failed for {url}", sourceUrl);
                return null;
            }
        }

        // Try enabled music providers
        foreach (var provider in _providerRegistry.GetEnabledProviders())
        {
            try
            {
                var result = await provider.DownloadAsync(sourceUrl,
                    new TrackQuality(320, MediaType.MP3), // default quality
                    outputDirectory, ct);

                if (result.Success && result.FilePath != null)
                    return result.FilePath;

                _logger.LogWarning("Provider {Name} could not download {url}: {Error}",
                    provider.Name, sourceUrl, result.ErrorMessage);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Provider {Name} failed for {url}", provider.Name, sourceUrl);
            }
        }

        _logger.LogWarning("No downloader available for {url}", sourceUrl);
        return null;
    }
}
