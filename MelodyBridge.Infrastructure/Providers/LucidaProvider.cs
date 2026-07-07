using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Helpers;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Providers;

/// <summary>
/// Music provider that searches via community API instances and downloads via lucida.to.
/// Uses Monochrome Hi-Fi API instances for Tidal search and Qobuz public API for Qobuz search.
/// Download requires browser automation (Playwright via Python).
/// </summary>
public partial class LucidaProvider : IMusicProvider
{
    private readonly ILogger<LucidaProvider> _logger;
    private readonly HttpClient _httpClient;
    private readonly MonochromeApiClient _monochromeClient;

    public string Id => "lucida";
    public string Name => "Lucida.to";
    public string Description => "Downloads from Tidal, Qobuz, Deezer, SoundCloud, Amazon Music, Spotify and more via lucida.to. FLAC up to 24-bit.";
    public string Icon => "🔮";

    public IReadOnlyList<Platform> SupportedPlatforms { get; } = new[]
    {
        Platform.Tidal,
        Platform.Qobuz,
        Platform.Deezer,
        Platform.Soundcloud,
        Platform.AmazonMusic,
        Platform.Spotify,
    };

    public IReadOnlyList<TrackQuality> SupportedQualities { get; } = ProviderQualities.Lucida;

    private const string BaseUrl = "https://lucida.to";
    private const string QobuzAppId = "798273057";

    public LucidaProvider(ILogger<LucidaProvider> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _monochromeClient = new MonochromeApiClient();
    }

    // ── Search ──────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, Platform? platform = null, CancellationToken ct = default)
    {
        try
        {
            var targetPlatform = platform ?? Platform.Tidal;

            // Tidal → Monochrome Hi-Fi API instances
            if (targetPlatform == Platform.Tidal)
                return await _monochromeClient.SearchAsync(query, ct);

            // Qobuz → Qobuz public API (same approach as SquidWtfProvider)
            if (targetPlatform == Platform.Qobuz)
                return await SearchQobuzAsync(query, ct);

            _logger.LogWarning("Lucida search: {Platform} not supported via API; try using the Monochrome or SquidWtf provider instead", targetPlatform);
            return Array.Empty<SearchResult>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lucida search failed for {Query}", query);
            return Array.Empty<SearchResult>();
        }
    }

    private async Task<IReadOnlyList<SearchResult>> SearchQobuzAsync(string query, CancellationToken ct)
    {
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
                ? alb.GetProperty("title").GetString() ?? null
                : null;
            var id = item.GetProperty("id").GetInt64();

            results.Add(new SearchResult(
                title, artists, album, $"https://www.qobuz.com/track/{id}", Platform.Qobuz,
                new[] { new TrackQuality(320, MediaType.MP3), new TrackQuality(24, MediaType.FLAC) }
            ));
        }

        return results;
    }

    // ── Get Track Info ──────────────────────────────────────────────────
    public async Task<TrackInfo?> GetTrackInfoAsync(string url, CancellationToken ct = default)
    {
        try
        {
            var platform = ProviderHelpers.DetectPlatform(url);
            if (platform == Platform.Unknown)
            {
                _logger.LogWarning("Unknown platform URL: {Url}", url);
                return null;
            }

            // Tidal → Monochrome API instances
            if (platform == Platform.Tidal)
                return await _monochromeClient.GetTrackInfoAsync(url, ct);

            // Qobuz → Qobuz public API
            if (platform == Platform.Qobuz)
            {
                if (!ProviderHelpers.TryExtractQobuzTrackId(url, out var qobuzId))
                    return null;

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

            // Other platforms → return basic info from URL
            return new TrackInfo("Unknown Track", "", null, null, url, platform, SupportedQualities);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lucida getTrackInfo failed for {Url}", url);
            return null;
        }
    }

    // ── Download ────────────────────────────────────────────────────────
    public async Task<DownloadResult> DownloadAsync(string trackUrl, TrackQuality quality, string outputDirectory, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);

            var platform = ProviderHelpers.DetectPlatform(trackUrl);

            // Tidal → download via Monochrome API instances
            if (platform == Platform.Tidal)
                return await _monochromeClient.DownloadAsync(trackUrl, quality, outputDirectory, ct);

            // Qobuz → download via SquidWtf
            if (platform == Platform.Qobuz && ProviderHelpers.TryExtractQobuzTrackId(trackUrl, out var qobuzId))
            {
                var qualityCode = quality switch
                {
                    { Bitrate: 24, Format: MediaType.FLAC } => "27",
                    { Bitrate: 320, Format: MediaType.MP3 } => "6",
                    _ => "6"
                };
                var ext = quality.Format switch
                {
                    MediaType.FLAC => "flac",
                    MediaType.MP3 => "mp3",
                    MediaType.AAC => "m4a",
                    _ => "bin"
                };
                var downloadUrl = Apis.QobuzSquidWtfApi.GetDownloadUrl(qobuzId, qualityCode);
                var fileName = $"{qobuzId}_{qualityCode}.{ext}";
                var filePath = Path.Combine(outputDirectory, fileName);
                await TrackFileHelper.DownloadFileAsync(downloadUrl, filePath);
                return File.Exists(filePath)
                    ? new DownloadResult(true, filePath, null, quality)
                    : new DownloadResult(false, null, "Download completed but file not found", null);
            }

            // Other platforms → provide guidance
            _logger.LogInformation(
                "Lucida.to downloads require browser automation. " +
                "Use the Python fallback: python lucida_client.py download {Url}", trackUrl);

            return new DownloadResult(false, null,
                $"Lucida.to downloads for {platform} require browser automation. " +
                "Install Playwright and use the Python helper script, or try a different provider.",
                null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lucida download failed for {Url}", trackUrl);
            return new DownloadResult(false, null, ex.Message, null);
        }
    }

    // ── Internal ────────────────────────────────────────────────────────
    // Delegates to ProviderHelpers (kept for reflection-based tests)
    private static Platform DetectPlatform(string url) => ProviderHelpers.DetectPlatform(url);
    private static bool TryExtractQobuzTrackId(string url, out long id) => ProviderHelpers.TryExtractQobuzTrackId(url, out id);
    private static string MapPlatformToService(Platform platform) => ProviderHelpers.MapPlatformToService(platform);

}
