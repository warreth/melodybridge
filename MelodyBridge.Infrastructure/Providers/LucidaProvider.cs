using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Helpers;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Providers;

/// <summary>
/// Music provider that searches and downloads via lucida.to.
/// Uses HTTP scraping for search and Playwright/browser automation for downloads.
/// Supports: Tidal, Qobuz, Deezer, SoundCloud, Amazon Music, Yandex Music, Spotify.
/// </summary>
public partial class LucidaProvider : IMusicProvider
{
    private readonly ILogger<LucidaProvider> _logger;
    private readonly HttpClient _httpClient;

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

    public LucidaProvider(ILogger<LucidaProvider> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
            BaseAddress = new Uri(BaseUrl),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
    }

    // ── Search ──────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, Platform? platform = null, CancellationToken ct = default)
    {
        try
        {
            var service = MapPlatformToService(platform ?? Platform.Tidal);
            var country = service switch
            {
                "qobuz" => "GB",
                "deezer" => "FR",
                _ => "US",
            };

            var searchUrl = $"/search?service={service}&country={country}&query={HttpUtility.UrlEncode(query)}";
            var response = await _httpClient.GetAsync(searchUrl, ct);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(ct);

            return ParseSearchResults(html, platform ?? Platform.Tidal);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Lucida search failed for {Query}", query);
            return Array.Empty<SearchResult>();
        }
    }

    // ── Get Track Info ──────────────────────────────────────────────────
    public async Task<TrackInfo?> GetTrackInfoAsync(string url, CancellationToken ct = default)
    {
        try
        {
            // Submit URL to lucida.to to get track info
            var response = await _httpClient.GetAsync($"/?url={HttpUtility.UrlEncode(url)}", ct);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(ct);

            // Try to extract from embedded SvelteKit data
            var jsonResults = ExtractTracksFromJson(html);
            if (jsonResults.Count > 0)
            {
                var first = jsonResults[0];
                var platform = DetectPlatform(url);
                return new TrackInfo(
                    first.Title, first.Artist, first.Album, null,
                    first.Url, platform, SupportedQualities
                );
            }

            // Fallback: return basic info from URL
            var fallbackPlatform = DetectPlatform(url);
            return new TrackInfo("Unknown Track", "", null, null, url, fallbackPlatform, SupportedQualities);
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

            // Lucida.to triggers downloads via browser automation (Playwright).
            // For .NET, we attempt to find the direct download URL from the page
            // or fall back to the PythonRunner for Playwright-based downloads.

            // First, try to get the page and look for a direct download link
            var response = await _httpClient.GetAsync($"/?url={HttpUtility.UrlEncode(trackUrl)}", ct);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(ct);

            // Look for direct download URL in the page
            var directUrl = ExtractDirectDownloadUrl(html, trackUrl);
            if (!string.IsNullOrEmpty(directUrl))
            {
                var ext = Path.GetExtension(new Uri(directUrl).AbsolutePath)?.TrimStart('.') ?? "bin";
                if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = "bin";
                var fileName = $"lucida_{Guid.NewGuid():N}.{ext}";
                var filePath = Path.Combine(outputDirectory, fileName);

                await TrackFileHelper.DownloadFileAsync(directUrl, filePath);

                if (File.Exists(filePath))
                    return new DownloadResult(true, filePath, null, quality);
            }

            // If no direct URL found, provide guidance
            _logger.LogInformation(
                "Lucida.to requires browser automation for downloads. " +
                "Use the Python fallback: python lucida_client.py download {Url}", trackUrl);

            return new DownloadResult(false, null,
                "Lucida.to downloads require browser automation. " +
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
    private static string MapPlatformToService(Platform platform) => platform switch
    {
        Platform.Tidal => "tidal",
        Platform.Qobuz => "qobuz",
        Platform.Deezer => "deezer",
        Platform.Soundcloud => "soundcloud",
        Platform.AmazonMusic => "amazon",
        Platform.Spotify => "spotify",
        _ => "tidal",
    };

    private static Platform DetectPlatform(string url)
    {
        if (url.Contains("tidal.com", StringComparison.OrdinalIgnoreCase)) return Platform.Tidal;
        if (url.Contains("qobuz.com", StringComparison.OrdinalIgnoreCase)) return Platform.Qobuz;
        if (url.Contains("deezer.com", StringComparison.OrdinalIgnoreCase)) return Platform.Deezer;
        if (url.Contains("soundcloud.com", StringComparison.OrdinalIgnoreCase)) return Platform.Soundcloud;
        if (url.Contains("amazon", StringComparison.OrdinalIgnoreCase)) return Platform.AmazonMusic;
        if (url.Contains("spotify.com", StringComparison.OrdinalIgnoreCase)) return Platform.Spotify;
        return Platform.Unknown;
    }

    private List<SearchResult> ParseSearchResults(string html, Platform platform)
    {
        var results = new List<SearchResult>();

        // Try embedded JSON first
        var jsonResults = ExtractTracksFromJson(html);
        if (jsonResults.Count > 0)
            return jsonResults;

        // Fallback: simple regex-based extraction of track links
        var trackMatches = TrackLinkRegex().Matches(html);
        foreach (Match match in trackMatches)
        {
            if (match.Groups["url"].Success && match.Groups["title"].Success)
            {
                results.Add(new SearchResult(
                    match.Groups["title"].Value,
                    match.Groups["artist"].Success ? match.Groups["artist"].Value : "",
                    match.Groups["album"].Success ? match.Groups["album"].Value : null,
                    BaseUrl + match.Groups["url"].Value,
                    platform,
                    SupportedQualities
                ));
            }
        }

        return results;
    }

    private List<SearchResult> ExtractTracksFromJson(string html)
    {
        var results = new List<SearchResult>();

        // Look for SvelteKit embedded data: const data = [...]
        var dataMatch = JsonDataRegex().Match(html);
        if (!dataMatch.Success) return results;

        try
        {
            var jsonStr = dataMatch.Groups[1].Value;
            using var doc = JsonDocument.Parse(jsonStr);

            // Navigate: data[1].data.results.results.tracks
            if (doc.RootElement.GetArrayLength() < 2) return results;
            var dataNode = doc.RootElement[1];
            if (!dataNode.TryGetProperty("data", out var dataProp)) return results;
            if (!dataProp.TryGetProperty("results", out var outerResults)) return results;
            if (!outerResults.TryGetProperty("results", out var innerResults)) return results;
            if (!innerResults.TryGetProperty("tracks", out var tracks)) return results;

            foreach (var track in tracks.EnumerateArray())
            {
                var title = track.GetProperty("title").GetString() ?? "Unknown";
                var artists = string.Join(", ",
                    (track.TryGetProperty("artists", out var artistsArr)
                        ? JsonSerializer.Deserialize<JsonElement[]>(artistsArr.GetRawText())
                            ?.Select(a => a.TryGetProperty("name", out var n) ? n.GetString() : "")
                            .Where(n => n != null)
                        : Array.Empty<string>()) ?? Array.Empty<string>());
                var album = track.TryGetProperty("album", out var albumProp)
                    ? albumProp.GetProperty("title").GetString()
                    : null;
                var url = track.TryGetProperty("url", out var urlProp)
                    ? urlProp.GetString()
                    : "";

                if (!string.IsNullOrEmpty(url))
                {
                    var fullUrl = url.StartsWith("http") ? url : BaseUrl + url;
                    results.Add(new SearchResult(
                        title, artists, album, fullUrl, DetectPlatform(url),
                        SupportedQualities
                    ));
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse Lucida JSON data");
        }

        return results;
    }

    private static string? ExtractDirectDownloadUrl(string html, string originalUrl)
    {
        // Look for download links/buttons in the page
        var match = DownloadUrlRegex().Match(html);
        if (match.Success)
        {
            var url = match.Groups[1].Value;
            if (url.StartsWith("//")) url = "https:" + url;
            else if (url.StartsWith("/")) url = BaseUrl + url;
            return url;
        }

        return null;
    }

    [GeneratedRegex(@"<a[^>]*href=""(?<url>/track/[^""]+)""[^>]*>.*?<h1[^>]*>(?<title>[^<]+)</h1>.*?(?:<h2[^>]*>(?<artist>[^<]*)</h2>)?.*?(?:<h3[^>]*>(?<album>[^<]*)</h3>)?", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex TrackLinkRegex();

    [GeneratedRegex(@"const data = (\[.*?\]);", RegexOptions.Singleline)]
    private static partial Regex JsonDataRegex();

    [GeneratedRegex(@"href=""(https?://[^""]*\.(flac|mp3|m4a|aac|opus)[^""]*)""", RegexOptions.IgnoreCase)]
    private static partial Regex DownloadUrlRegex();
}
