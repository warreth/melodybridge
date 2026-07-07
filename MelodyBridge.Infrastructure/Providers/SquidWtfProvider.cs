using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Apis;
using MelodyBridge.Infrastructure.Resolvers;
using MelodyBridge.Infrastructure.Helpers;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Providers;

/// <summary>
/// Music provider that downloads from Qobuz, Tidal, Amazon Music and SoundCloud
/// via the squid.wtf network of subdomains.
/// </summary>
public class SquidWtfProvider : IMusicProvider
{
    private readonly ILogger<SquidWtfProvider> _logger;
    private readonly HttpClient _httpClient;

    public string Id => "squidwtf";
    public string Name => "Squid.wtf";
    public string Description => "Downloads from Qobuz, Tidal, Amazon Music & SoundCloud via squid.wtf subdomains. Hi-Res FLAC up to 24-bit/192kHz.";
    public string Icon => "🐙";

    public IReadOnlyList<Platform> SupportedPlatforms { get; } = new[]
    {
        Platform.Qobuz,
        Platform.AmazonMusic,
        Platform.Soundcloud,
    };

    public IReadOnlyList<TrackQuality> SupportedQualities { get; } = ProviderQualities.SquidWtf;

    // Known working Qobuz app ID for public API access
    private const string QobuzAppId = "798273057";

    public SquidWtfProvider(ILogger<SquidWtfProvider> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    // ── Search ──────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, Platform? platform = null, CancellationToken ct = default)
    {
        try
        {
            // Squid.wtf doesn't have its own search API — we search Qobuz's public API.
            // (Tidal search is handled by the Monochrome provider.)
            var targetPlatform = platform ?? Platform.Qobuz;
            if (targetPlatform != Platform.Qobuz)
            {
                _logger.LogWarning("SquidWtf search only supports Qobuz; got {Platform}", targetPlatform);
                return Array.Empty<SearchResult>();
            }

            // Qobuz catalog search — requires X-App-Id as header
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://www.qobuz.com/api.json/0.2/catalog/search?query={HttpUtility.UrlEncode(query)}&limit=10");
            request.Headers.Add("X-App-Id", QobuzAppId);

            using var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var results = new List<SearchResult>();

            var tracks = doc.RootElement.GetProperty("tracks").GetProperty("items");
            foreach (var item in tracks.EnumerateArray())
            {
                var title = item.GetProperty("title").GetString() ?? "Unknown";
                var artists = item.TryGetProperty("performer", out var perf)
                    ? perf.GetProperty("name").GetString() ?? ""
                    : "";
                var album = item.TryGetProperty("album", out var alb)
                    ? alb.GetProperty("title").GetString() ?? ""
                    : null;
                var id = item.GetProperty("id").GetInt64();
                var url = $"https://www.qobuz.com/track/{id}";

                results.Add(new SearchResult(
                    title, artists, album, url, Platform.Qobuz,
                    new[] { new TrackQuality(320, MediaType.MP3), new TrackQuality(24, MediaType.FLAC) }
                ));
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SquidWtf search failed for {Query}", query);
            return Array.Empty<SearchResult>();
        }
    }

    // ── Get Track Info ──────────────────────────────────────────────────
    public async Task<TrackInfo?> GetTrackInfoAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var platform = DetectPlatform(url);
            if (platform == Platform.Unknown)
            {
                _logger.LogWarning("Unknown platform URL: {Url}", url);
                return null;
            }

            // For Qobuz tracks: extract track ID and fetch metadata
            if (platform == Platform.Qobuz && TryExtractQobuzTrackId(url, out var qobuzId))
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    $"https://www.qobuz.com/api.json/0.2/track/get?track_id={qobuzId}");
                request.Headers.Add("X-App-Id", QobuzAppId);

                using var response = await _httpClient.SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                var title = root.GetProperty("title").GetString() ?? "Unknown";
                var artist = root.TryGetProperty("performer", out var perf)
                    ? perf.GetProperty("name").GetString() ?? ""
                    : "";
                var album = root.TryGetProperty("album", out var alb)
                    ? alb.GetProperty("title").GetString() ?? null
                    : null;
                var coverUrl = root.TryGetProperty("album", out var alb2) && alb2.TryGetProperty("image", out var img)
                    ? img.GetProperty("large").GetString() ?? null
                    : null;

                return new TrackInfo(title, artist, album, coverUrl, url, Platform.Qobuz,
                    new[] { new TrackQuality(320, MediaType.MP3), new TrackQuality(24, MediaType.FLAC) });
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get track info for {Url}", url);
            return null;
        }
    }

    // ── Download ────────────────────────────────────────────────────────
    public async Task<DownloadResult> DownloadAsync(string trackUrl, TrackQuality quality, string outputDirectory, CancellationToken ct = default)
    {
        try
        {
            var platform = DetectPlatform(trackUrl);
            Directory.CreateDirectory(outputDirectory);

            switch (platform)
            {
                case Platform.Qobuz:
                    return await DownloadQobuzAsync(trackUrl, quality, outputDirectory, ct);
                default:
                    return new DownloadResult(false, null, $"Platform {platform} not yet implemented via SquidWtf", null);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SquidWtf download failed for {Url}", trackUrl);
            return new DownloadResult(false, null, ex.Message, null);
        }
    }

    // ── Internal: Qobuz ─────────────────────────────────────────────────
    private async Task<DownloadResult> DownloadQobuzAsync(string trackUrl, TrackQuality quality, string outputDir, CancellationToken ct)
    {
        // Resolve Qobuz track ID from ISRC or URL
        long? qobuzId;
        if (TryExtractQobuzTrackId(trackUrl, out var extractedId))
        {
            qobuzId = extractedId;
        }
        else
        {
            // Fall back to ISRC resolver if URL contains an ISRC-like pattern
            var resolver = new QobuzIdResolver();
            // Try to get ISRC from the URL or use the last segment
            var isrc = Path.GetFileNameWithoutExtension(trackUrl);
            qobuzId = await resolver.GetQobuzTrackIdByIsrcAsync(isrc);
        }

        if (qobuzId == null)
            return new DownloadResult(false, null, "Could not resolve Qobuz track ID", null);

        // Map TrackQuality to squid.wtf quality code
        var qualityCode = MapQualityToCode(quality);

        // Get download URL from squid.wtf API
        var downloadUrl = QobuzSquidWtfApi.GetDownloadUrl(qobuzId.Value, qualityCode);

        // Extension from quality
        var ext = quality.Format switch
        {
            MediaType.FLAC => "flac",
            MediaType.MP3 => "mp3",
            MediaType.AAC => "m4a",
            MediaType.OPUS => "opus",
            _ => "bin"
        };

        var fileName = $"{qobuzId}_{qualityCode}.{ext}";
        var filePath = Path.Combine(outputDir, fileName);

        // Download via TrackFileHelper
        await TrackFileHelper.DownloadFileAsync(downloadUrl, filePath);

        if (!File.Exists(filePath))
            return new DownloadResult(false, null, "Download completed but file not found", null);

        return new DownloadResult(true, filePath, null, quality);
    }

    // ── Helpers ─────────────────────────────────────────────────────────
    private static Platform DetectPlatform(string url)
    {
        if (url.Contains("qobuz.com", StringComparison.OrdinalIgnoreCase)) return Platform.Qobuz;
        if (url.Contains("amazon", StringComparison.OrdinalIgnoreCase)) return Platform.AmazonMusic;
        if (url.Contains("soundcloud", StringComparison.OrdinalIgnoreCase)) return Platform.Soundcloud;
        return Platform.Unknown;
    }

    private static bool TryExtractQobuzTrackId(string url, out long id)
    {
        id = 0;
        // Pattern: https://www.qobuz.com/track/123456 or ?track_id=123456
        var match = System.Text.RegularExpressions.Regex.Match(url, @"(?:track/|track_id=)(\d+)");
        if (match.Success && long.TryParse(match.Groups[1].Value, out var parsed))
        {
            id = parsed;
            return true;
        }
        return false;
    }

    private static string MapQualityToCode(TrackQuality quality)
    {
        return quality switch
        {
            { Bitrate: 24, Format: MediaType.FLAC } => "27",
            { Bitrate: 320, Format: MediaType.MP3 } => "6",
            { Bitrate: 320, Format: MediaType.AAC } => "4",
            { Bitrate: 320, Format: MediaType.OPUS } => "5",
            { Bitrate: 16, Format: MediaType.FLAC } => "6",
            _ => "6"
        };
    }
}
