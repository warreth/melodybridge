using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.SearchProviders;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// Monochrome downloader plugin: the official Monochrome lossless API
/// (tracks.monochrome.st). No account needed; the service sources the
/// audio itself and streams FLAC back over a plain GET. The host is
/// configurable (community mirror or a local proxy) through the plugin's
/// base_url setting.
///
/// API shape (their "tracks" provider, live-probed):
///   /search/tracks?q={query}&limit={n} → {"tracks":[track objects]}
///   /track/{id}                        → audio bytes (FLAC)
///   /health                            → 200 when the API is up
///
/// Track objects carry: id/trackId (snowflake), title, artistNames[],
/// duration (milliseconds), isrc and playable.
/// </summary>
public class MonochromeDownloader : IDownloader, IPluginAvailabilityRefresh
{
    public const string DefaultBaseUrl = MonochromeApi.DefaultBaseUrl;

    // The official API serves one lossless tier: asking for FLAC gets a
    // real FLAC (usually ~900-1100 kbps). There is no lossy tier and no
    // quality parameter, so no AAC/MP3 deliverables are declared.
    public static readonly PluginCapabilities Caps = new(
    [
        new QualityDeliverable(AudioFormat.Flac, Lossless: true, 900, 10000),
    ]);
    public PluginCapabilities Capabilities => Caps;

    public string Id => "monochrome";
    public string Name => "Monochrome (lossless)";
    public string Description => "Official Monochrome lossless API: FLAC streams, no account needed";

    public DownloaderSearchInfo SearchInfo =>
        DownloaderSearchInfo.ViaProvider(SearchData.Text | SearchData.Isrc,
            "Monochrome catalogue search, ISRC first");

    public string IconName => "music";
    public string? GuideUrl => null;
    public IReadOnlyList<string> Notes => new[] { "lossless", "no account" };

    /// <inheritdoc />
    public string? LastUnavailableReason { get; private set; }

    public IReadOnlyList<PluginConfigField> ConfigFields =>
    [
        new(MonochromeApi.BaseUrlKey, "API base URL", DefaultBaseUrl,
            "Override the Monochrome API host, for example a community mirror. Empty uses the official host.",
            Advanced: true,
            DefaultValue: DefaultBaseUrl),
    ];

    private readonly HttpClient _http;
    private readonly IPluginConfig _config;
    private readonly ILogger<MonochromeDownloader> _logger;

    // Availability probe cache: the waterfall asks per track and the
    // Plugins page re-checks on a timer; the TTL keeps /health from being
    // hammered.
    private readonly AvailabilityCache _availability = new();

    /// <summary>Per-request deadlines so one hanging API call cannot stall the waterfall.</summary>
    private static readonly TimeSpan StreamRequestTimeout = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(15);

    public MonochromeDownloader(HttpClient http, IPluginConfig config, ILogger<MonochromeDownloader> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    // ── Availability ─────────────────────────────────────────────────────

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_availability.TryGet(out var cached))
            return cached;

        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(HealthTimeout);
            // The old /health endpoint can hang even when the catalogue
            // itself answers. Probe the endpoint downloads actually use.
            var url = await MonochromeApi.BaseUrlAsync(_config, Id, ct)
                + "/search/tracks?q=monochrome&limit=1";
            using var resp = await _http.GetAsync(url, probe.Token);
            var available = resp.IsSuccessStatusCode;
            LastUnavailableReason = available
                ? null
                : $"The Monochrome API answered HTTP {(int)resp.StatusCode}";
            if (!available)
                _logger.LogDebug("Monochrome search probe returned {Status}", (int)resp.StatusCode);
            _availability.Store(available);
            return available;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            LastUnavailableReason = "The Monochrome API did not answer within 15 seconds";
            _logger.LogDebug("Monochrome search probe timed out");
            _availability.Store(false);
            return false;
        }
        catch (Exception ex)
        {
            LastUnavailableReason = "The Monochrome API did not answer";
            _logger.LogDebug(ex, "Monochrome search probe failed");
            _availability.Store(false);
            return false;
        }
    }

    public void RefreshAvailability() => _availability.Invalidate();

    // ── Download ─────────────────────────────────────────────────────────

    public async Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl,
        string outputDirectory,
        string? melodyId,
        DownloadQuality? quality = null,
        CancellationToken ct = default)
    {
        if (!TryExtractTrackId(sourceUrl, out var trackId))
            return new DownloaderDownloadResult(false, null, "not a Monochrome track URL or id: " + sourceUrl);

        Directory.CreateDirectory(outputDirectory);

        // MelodyId in the name (when present) keeps retries and concurrent
        // downloads of the same track distinct files instead of a shared
        // path two writers clobber.
        var filePath = Path.Combine(outputDirectory,
            $"monochrome_{trackId}_{(melodyId is { Length: > 0 } ? melodyId : Guid.NewGuid().ToString("N")[..8])}.flac");

        // The official CDN occasionally cuts a long FLAC stream in half
        // (HTTP/2 INTERNAL_ERROR or a truncated body). One retry turns that
        // into a usable file instead of a failed track; a caller cancel and
        // a real 4xx still stop immediately.
        const int attempts = 2;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var baseUrl = await MonochromeApi.BaseUrlAsync(_config, Id, ct);
                using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
                request.CancelAfter(StreamRequestTimeout);

                using var resp = await _http.GetAsync($"{baseUrl}/track/{trackId}",
                    HttpCompletionOption.ResponseHeadersRead, request.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Monochrome track {TrackId} returned {Status}", trackId, (int)resp.StatusCode);
                    if (attempt < attempts && (int)resp.StatusCode >= 500)
                    {
                        await Task.Delay(500, ct);
                        continue;
                    }
                    return new DownloaderDownloadResult(false, null,
                        $"Monochrome track {trackId} returned HTTP {(int)resp.StatusCode}");
                }

                await using (var src = await resp.Content.ReadAsStreamAsync(request.Token))
                await using (var dst = File.Create(filePath))
                {
                    await src.CopyToAsync(dst, 81920, request.Token);
                }
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                FileCleanup.DeleteQuietly(filePath);
                throw; // caller cancellation: the outer handler cleans up
            }
            catch (Exception ex) when (attempt < attempts && IsTransientStreamFailure(ex))
            {
                FileCleanup.DeleteQuietly(filePath);
                _logger.LogDebug(ex,
                    "Monochrome stream for track {TrackId} was cut off; retrying", trackId);
                await Task.Delay(500, ct);
            }
            catch (Exception ex)
            {
                FileCleanup.DeleteQuietly(filePath);
                _logger.LogDebug(ex, "Monochrome download for track {TrackId} failed", trackId);
                return new DownloaderDownloadResult(false, null, ex.Message);
            }
        }

        if (new FileInfo(filePath).Length == 0)
        {
            // A zero-byte rip is a failed rip: never leave it behind.
            FileCleanup.DeleteQuietly(filePath);
            return new DownloaderDownloadResult(false, null, "Monochrome returned an empty stream");
        }

        // An error page saved under an audio name must never pass: sniff
        // the real bytes before accepting, and fix the extension to match.
        var container = AudioSignature.EnsureExtension(filePath);
        if (container is null)
        {
            _logger.LogDebug("Monochrome stream for track {TrackId} is not audio", trackId);
            FileCleanup.DeleteQuietly(filePath);
            return new DownloaderDownloadResult(false, null, "Monochrome stream is not audio");
        }

        YtDlpDownloader.TagDownloadedFile(container, expectedTitle: null);
        return new DownloaderDownloadResult(true, container, null);
    }

    /// <summary>
    /// Extracts the track id from a Monochrome stream URL
    /// ({base}/track/{id}) or a bare snowflake id. Ids stay strings:
    /// 19-digit snowflakes can overflow a long.
    /// </summary>
    internal static bool TryExtractTrackId(string sourceUrl, out string trackId)
    {
        trackId = "";
        if (string.IsNullOrWhiteSpace(sourceUrl)) return false;

        var candidate = sourceUrl.Trim().TrimEnd('/');

        // Bare id
        if (IsSnowflake(candidate))
        {
            trackId = candidate;
            return true;
        }

        // Last path segment of .../track/{id}; a URL must actually be a
        // track path, so a random numeric URL tail is not mistaken for one.
        var lastSegment = candidate.Split('/')[^1];
        if (!IsSnowflake(lastSegment)) return false;
        if (candidate.Contains("://", StringComparison.Ordinal) &&
            !candidate.Contains("/track/", StringComparison.OrdinalIgnoreCase))
            return false;
        trackId = lastSegment;
        return true;
    }

    /// <summary>A cut-off body or a timeout, not a caller cancel or a 4xx.</summary>
    private static bool IsTransientStreamFailure(Exception ex)
        => ex is IOException
            or HttpRequestException
            or TaskCanceledException;

    /// <summary>A numeric id of at most 20 digits (the API's snowflake shape).</summary>
    private static bool IsSnowflake(string value)
        => value.Length is > 0 and <= 20 && value.All(char.IsAsciiDigit);

}
