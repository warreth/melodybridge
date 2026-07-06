using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Helpers;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Providers;

/// <summary>
/// Music provider for doubledouble.top.
/// Supports: Amazon Music, SoundCloud, Qobuz, Deezer, Tidal.
/// NOTE: DoubleDouble has NO public API and uses CAPTCHA protection.
/// This provider uses HTTP-based scraping which may be limited.
/// For full functionality, browser automation is required.
/// </summary>
public partial class DoubleDoubleProvider : IMusicProvider
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

    // Default region — the user picks US or EU on the landing page.
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
            // DoubleDouble doesn't have a dedicated search API.
            // It works by submitting URLs from streaming services.
            // For search, we can try submitting a search query directly.
            // The endpoint appears to accept a query parameter.
            var formData = new Dictionary<string, string>
            {
                ["query"] = query,
                ["service"] = MapPlatformToService(platform ?? Platform.Qobuz),
            };

            // Some DoubleDouble endpoints accept form-encoded POSTs
            using var content = new FormUrlEncodedContent(formData);
            var response = await _httpClient.PostAsync($"{BaseUrl}/", content, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("DoubleDouble CAPTCHA block — cannot search");
                return Array.Empty<SearchResult>();
            }

            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(ct);

            // Parse any track links from the response
            return ParseResults(html, platform ?? Platform.Qobuz);
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

            // Submit the URL to DoubleDouble to get info
            var formData = new Dictionary<string, string>
            {
                ["url"] = url,
            };

            using var content = new FormUrlEncodedContent(formData);
            var response = await _httpClient.PostAsync($"{BaseUrl}/", content, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogWarning("DoubleDouble CAPTCHA block");
                return null;
            }

            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(ct);

            // Try to extract title/artist from the response page
            var title = ExtractMetaValue(html, "title");
            var artist = ExtractMetaValue(html, "artist");

            return new TrackInfo(
                title ?? "Unknown Track",
                artist ?? "",
                null, null, url, platform, SupportedQualities);
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

            // Submit the track URL to DoubleDouble for processing
            var formData = new Dictionary<string, string>
            {
                ["url"] = trackUrl,
            };

            using var content = new FormUrlEncodedContent(formData);
            var response = await _httpClient.PostAsync($"{BaseUrl}/", content, ct);

            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return new DownloadResult(false, null, "Blocked by CAPTCHA. Try accessing https://doubledouble.top in a browser first.", null);

            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(ct);

            // Look for direct download link in the response
            var downloadUrl = ExtractDownloadUrl(html);
            if (string.IsNullOrEmpty(downloadUrl))
            {
                _logger.LogInformation("DoubleDouble requires interactive CAPTCHA. Direct download URL not found.");
                return new DownloadResult(false, null,
                    "DoubleDouble requires CAPTCHA verification. Open https://doubledouble.top in a browser, complete the CAPTCHA, then try again.",
                    null);
            }

            var ext = Path.GetExtension(new Uri(downloadUrl).AbsolutePath)?.TrimStart('.') ?? "bin";
            if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = "bin";

            var fileName = $"dd_{Guid.NewGuid():N}.{ext}";
            var filePath = Path.Combine(outputDirectory, fileName);

            await TrackFileHelper.DownloadFileAsync(downloadUrl, filePath);

            return File.Exists(filePath)
                ? new DownloadResult(true, filePath, null, quality)
                : new DownloadResult(false, null, "Download completed but file not found", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DoubleDouble download failed for {Url}", trackUrl);
            return new DownloadResult(false, null, ex.Message, null);
        }
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

    private static List<SearchResult> ParseResults(string html, Platform platform)
    {
        var results = new List<SearchResult>();
        // Look for track links / results in the page
        var matches = ResultLinkRegex().Matches(html);
        foreach (Match match in matches)
        {
            var title = match.Groups["title"].Success ? match.Groups["title"].Value.Trim() : "Unknown";
            var url = match.Groups["url"].Success ? match.Groups["url"].Value.Trim() : "";
            if (!string.IsNullOrEmpty(url))
            {
                results.Add(new SearchResult(title, "", null, url, platform,
                    new[] { new TrackQuality(320, MediaType.MP3), new TrackQuality(24, MediaType.FLAC) }));
            }
        }
        return results;
    }

    private static string? ExtractMetaValue(string html, string name)
    {
        var match = MetaRegex().Match(html);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? ExtractDownloadUrl(string html)
    {
        var match = DownloadLinkRegex().Match(html);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"<a[^>]*href=""(?<url>/download/[^""]+)""[^>]*>(?<title>[^<]+)</a>", RegexOptions.IgnoreCase)]
    private static partial Regex ResultLinkRegex();

    [GeneratedRegex(@"<meta[^>]*(?:property|name)=""(?:og:)?(title|artist)""[^>]*content=""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRegex();

    [GeneratedRegex(@"href=""(https?://[^""]*\.(flac|mp3|m4a|aac|opus)[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadLinkRegex();
}
