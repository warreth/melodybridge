using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using MelodyBridge.Core;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Application.Services;

public class DownloadManager : IDownloadManager
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
        return await DownloadWithQualityFallbackAsync(sourceUrl, outputDirectory, melodyId,
            new TrackQuality(320, MediaType.MP3), null, ct);
    }

    /// <summary>
    /// Quality waterfall download.
    /// Tries legacy downloaders first, then iterates through enabled providers
    /// attempting the highest quality first and falling back to lower qualities.
    /// </summary>
    public async Task<string?> DownloadWithQualityFallbackAsync(
        string sourceUrl, string outputDirectory, string melodyId,
        TrackQuality maxQuality, TrackQuality? minQuality = null, CancellationToken ct = default)
    {
        // Build quality ladder: from maxQuality down to minQuality
        var qualityLadder = BuildQualityLadder(maxQuality, minQuality ?? maxQuality);
        if (qualityLadder.Count == 0)
            qualityLadder.Add(maxQuality);

        // Phase 1: Try legacy downloaders (they handle their own quality)
        var legacy = _legacyDownloaders.FirstOrDefault(d => d.CanHandle(sourceUrl));
        if (legacy != null)
        {
            try
            {
                _logger.LogInformation("Trying legacy downloader {Name} for {url}", legacy.Name, sourceUrl);
                return await legacy.DownloadAsync(sourceUrl, outputDirectory, melodyId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Legacy downloader {Name} failed for {url}", legacy.Name, sourceUrl);
            }
        }

        // Phase 2: Try enabled music providers with quality waterfall
        var providers = _providerRegistry.GetEnabledProviders();
        if (providers.Count == 0)
        {
            _logger.LogWarning("No enabled providers available for {url}", sourceUrl);
            return null;
        }

        foreach (var quality in qualityLadder)
        {
            foreach (var provider in providers)
            {
                // Check if provider supports this quality
                if (!provider.SupportedQualities.Any(q =>
                        q.Bitrate == quality.Bitrate && q.Format == quality.Format))
                {
                    continue;
                }

                try
                {
                    _logger.LogInformation("Trying provider {Name} with quality {bitrate}{format} for {url}",
                        provider.Name, quality.Bitrate, quality.Format, sourceUrl);

                    var result = await provider.DownloadAsync(sourceUrl, quality, outputDirectory, ct);

                    if (result.Success && result.FilePath != null)
                    {
                        _logger.LogInformation("Download succeeded via {Name} at {bitrate}{format}",
                            provider.Name, quality.Bitrate, quality.Format);
                        return result.FilePath;
                    }

                    _logger.LogWarning("Provider {Name} returned failure for {url}: {Error}",
                        provider.Name, sourceUrl, result.ErrorMessage);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Provider {Name} failed for {url} at {bitrate}{format}",
                        provider.Name, sourceUrl, quality.Bitrate, quality.Format);
                }
            }
        }

        _logger.LogWarning("All providers exhausted for {url}", sourceUrl);
        return null;
    }

    private List<TrackQuality> BuildQualityLadder(TrackQuality max, TrackQuality min)
    {
        // Ordered list of all known quality tiers (highest to lowest)
        var allQualities = new List<TrackQuality>
        {
            new(24, MediaType.FLAC),
            new(192, MediaType.FLAC),
            new(16, MediaType.FLAC),
            new(320, MediaType.AAC),
            new(320, MediaType.OPUS),
            new(320, MediaType.MP3),
            new(256, MediaType.AAC),
            new(192, MediaType.MP3),
            new(128, MediaType.MP3),
        };

        // Find range between max and min
        var maxIdx = allQualities.FindIndex(q => q.Bitrate == max.Bitrate && q.Format == max.Format);
        var minIdx = allQualities.FindIndex(q => q.Bitrate == min.Bitrate && q.Format == min.Format);

        if (maxIdx < 0) maxIdx = 0;
        if (minIdx < 0) minIdx = allQualities.Count - 1;

        // Ensure max is higher priority than min
        if (maxIdx > minIdx)
            (maxIdx, minIdx) = (minIdx, maxIdx);

        return allQualities.Skip(maxIdx).Take(minIdx - maxIdx + 1).ToList();
    }
}
