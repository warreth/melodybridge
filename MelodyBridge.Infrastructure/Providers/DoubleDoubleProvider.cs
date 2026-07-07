using System.Text.Json;
using System.Web;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Helpers;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Providers;

/// <summary>
/// Music provider for doubledouble.top.
/// Supports: Amazon Music, SoundCloud, Qobuz, Deezer, Tidal.
///
/// API endpoints (discovered from app.js):
///   GET /search?q=&lt;query&gt;&amp;service=&lt;service&gt; → search results JSON
///   GET /dl?url=&lt;url&gt;[&amp;external=&lt;service&gt;] → submit URL, returns { success, id }
///   GET /dl/&lt;id&gt; → poll for download status, returns { status, url } on completion
///
/// NOTE: DoubleDouble requires Cloudflare Turnstile CAPTCHA. Automated access
/// may be blocked. For Tidal and Qobuz, consider using the Monochrome or
/// SquidWtf providers instead.
/// </summary>
public class DoubleDoubleProvider : IMusicProvider
{
    private readonly ILogger<DoubleDoubleProvider> _logger;
    private readonly HttpClient _httpClient;

    public string Id => "doubledouble";
    public string Name => "DoubleDouble";
    public string Description => "Downloads from Amazon Music, SoundCloud, Qobuz, Deezer & Tidal via doubledouble.top. Real decrypted files. CAPTCHA may be required.";
    public string Icon => "🔁";

    public IReadOnlyList<Platform> SupportedPlatforms { get; } = new[]
    {
        Platform.AmazonMusic,
        Platform.Soundcloud,
        Platform.Qobuz,
        Platform.Deezer,
        Platform.Tidal,
    };

    public IReadOnlyList<TrackQuality> SupportedQualities { get; } = ProviderQualities.DoubleDouble;

    private const string UsBaseUrl = "https://us.doubledouble.top";
    private const string EuBaseUrl = "https://eu.doubledouble.top";

    public DoubleDoubleProvider(ILogger<DoubleDoubleProvider> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    /// <summary>Region to use: "us" or "eu". Can be changed at runtime.</summary>
    public string Region { get; set; } = "us";

    private string BaseUrl => Region == "eu" ? EuBaseUrl : UsBaseUrl;

    // ── Search ──────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, Platform? platform = null, CancellationToken ct = default)
    {
        try
        {
            var targetPlatform = platform ?? Platform.Qobuz;

            // DoubleDouble search API: GET /search?q=<query>&service=<service>
            // Returns JSON: { results: [ { name, artist, album, cover, link, type, links }, ... ] }
            var searchUrl = $"{BaseUrl}/search?q={HttpUtility.UrlEncode(query)}&service={MapPlatformToService(targetPlatform)}";
            var response = await _httpClient.GetAsync(searchUrl, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("DoubleDouble CAPTCHA block — cannot search");
                return Array.Empty<SearchResult>();
            }

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);

            return ParseSearchResults(body, targetPlatform);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DoubleDouble search failed for {Query}", query);
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

            // Submit URL via GET /dl?url= and parse the response JSON
            var dlUrl = $"{BaseUrl}/dl?url={HttpUtility.UrlEncode(url)}";
            var response = await _httpClient.GetAsync(dlUrl, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("DoubleDouble CAPTCHA block for {Url}", url);
                return null;
            }

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);

            // Response is JSON: { success, id } on initial submit
            // Or the initial response may include track info
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var title = root.TryGetProperty("title", out var titleProp)
                ? titleProp.GetString() ?? "Unknown Track"
                : "Unknown Track";
            var artist = root.TryGetProperty("artist", out var artistProp)
                ? artistProp.GetString() ?? ""
                : "";

            return new TrackInfo(title, artist, null, null, url, platform, SupportedQualities);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DoubleDouble getTrackInfo failed for {Url}", url);
            return null;
        }
    }

    // ── Download ────────────────────────────────────────────────────────
    public async Task<DownloadResult> DownloadAsync(string trackUrl, TrackQuality quality, string outputDirectory, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var platform = DetectPlatform(trackUrl);

            if (platform == Platform.Unknown)
                return new DownloadResult(false, null, "Unknown platform URL", null);

            // Step 1: Submit URL via GET /dl?url= to start processing
            var dlUrl = $"{BaseUrl}/dl?url={HttpUtility.UrlEncode(trackUrl)}";
            var response = await _httpClient.GetAsync(dlUrl, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return new DownloadResult(false, null,
                    "Blocked by CAPTCHA. Try accessing https://doubledouble.top in a browser first.", null);

            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsStringAsync(ct);

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Check if submission succeeded
            if (root.TryGetProperty("success", out var successProp) && successProp.GetBoolean())
            {
                var id = root.GetProperty("id").GetString();
                if (!string.IsNullOrEmpty(id))
                {
                    // Step 2: Poll /dl/<id> until complete
                    var result = await PollDownloadAsync(id, ct);
                    if (result != null)
                        return result;
                }
            }

            // If we got a direct download URL in the response instead
            if (root.TryGetProperty("url", out var urlProp))
            {
                var downloadUrl = urlProp.GetString();
                if (!string.IsNullOrEmpty(downloadUrl))
                {
                    var ext = Path.GetExtension(new Uri(downloadUrl).AbsolutePath)?.TrimStart('.') ?? "bin";
                    if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = "bin";
                    var fileName = $"dd_{Guid.NewGuid():N}.{ext}";
                    var filePath = Path.Combine(outputDirectory, fileName);
                    await TrackFileHelper.DownloadFileAsync(downloadUrl, filePath);
                    return File.Exists(filePath)
                        ? new DownloadResult(true, filePath, null, quality)
                        : new DownloadResult(false, null, "Download completed but file not found", null);
                }
            }

            return new DownloadResult(false, null,
                "DoubleDouble requires CAPTCHA verification. Open https://doubledouble.top in a browser, complete the CAPTCHA, then try again.",
                null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DoubleDouble download failed for {Url}", trackUrl);
            return new DownloadResult(false, null, ex.Message, null);
        }
    }

    private async Task<DownloadResult?> PollDownloadAsync(string id, CancellationToken ct)
    {
        var pollUrl = $"{BaseUrl}/dl/{id}";

        for (var i = 0; i < 30; i++) // Poll up to 30 times (~60s max)
        {
            try
            {
                var response = await _httpClient.GetAsync(pollUrl, ct);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(ct);

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                // Check status
                if (root.TryGetProperty("status", out var statusProp))
                {
                    var status = statusProp.GetString();

                    if (status == "complete" || status == "ready")
                    {
                        // Get download URL
                        if (root.TryGetProperty("url", out var urlProp))
                        {
                            var downloadUrl = urlProp.GetString();
                            if (!string.IsNullOrEmpty(downloadUrl))
                            {
                                var ext = Path.GetExtension(new Uri(downloadUrl).AbsolutePath)?.TrimStart('.') ?? "bin";
                                if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = "bin";
                                var outputDir = Path.GetDirectoryName(pollUrl) ?? ".";
                                var fileName = $"dd_{Guid.NewGuid():N}.{ext}";
                                var filePath = Path.Combine(outputDir, fileName);
                                await TrackFileHelper.DownloadFileAsync(downloadUrl, filePath);
                                return File.Exists(filePath)
                                    ? new DownloadResult(true, filePath, null, null)
                                    : null;
                            }
                        }
                        return null;
                    }

                    if (status == "error")
                    {
                        var error = root.TryGetProperty("error", out var errProp)
                            ? errProp.GetString() : "Unknown error";
                        _logger.LogWarning("DoubleDouble processing error: {Error}", error);
                        return null;
                    }
                }

                // Wait before polling again
                await Task.Delay(2000, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DoubleDouble poll attempt {Attempt} failed", i);
                await Task.Delay(2000, ct);
            }
        }

        _logger.LogWarning("DoubleDouble poll timed out for {Id}", id);
        return null;
    }

    // ── Internal ────────────────────────────────────────────────────────
    private static string MapPlatformToService(Platform platform) => platform switch
    {
        Platform.Qobuz => "qobuz",
        Platform.Tidal => "tidal",
        Platform.Deezer => "deezer",
        Platform.Soundcloud => "soundcloud",
        Platform.AmazonMusic => "amazon",
        _ => "qobuz",
    };

    private static Platform DetectPlatform(string url)
    {
        if (url.Contains("amazon", StringComparison.OrdinalIgnoreCase)) return Platform.AmazonMusic;
        if (url.Contains("soundcloud.com", StringComparison.OrdinalIgnoreCase)) return Platform.Soundcloud;
        if (url.Contains("qobuz.com", StringComparison.OrdinalIgnoreCase)) return Platform.Qobuz;
        if (url.Contains("deezer.com", StringComparison.OrdinalIgnoreCase)) return Platform.Deezer;
        if (url.Contains("tidal.com", StringComparison.OrdinalIgnoreCase)) return Platform.Tidal;
        return Platform.Unknown;
    }

    private static IReadOnlyList<SearchResult> ParseSearchResults(string json, Platform platform)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Response: { results: [ { name, artist, album, cover, link, type, links }, ... ] }
            if (!root.TryGetProperty("results", out var items))
                return results;

            foreach (var item in items.EnumerateArray())
            {
                var title = item.GetProperty("name").GetString() ?? "Unknown";
                var artist = item.TryGetProperty("artist", out var a)
                    ? a.GetString() ?? "" : "";
                var album = item.TryGetProperty("album", out var alb)
                    ? alb.GetString() ?? null : null;
                var link = item.TryGetProperty("link", out var l)
                    ? l.GetString() ?? "" : "";

                if (!string.IsNullOrEmpty(link))
                {
                    results.Add(new SearchResult(title, artist, album, link, platform,
                        new[] { new TrackQuality(320, MediaType.MP3), new TrackQuality(24, MediaType.FLAC) }));
                }
            }
        }
        catch (JsonException)
        {
            // Ignore malformed responses
        }

        return results;
    }
}
