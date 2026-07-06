using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Helpers;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Providers;

/// <summary>
/// Music provider that uses the TIDAL API (the same API that powers monochrome.tf).
/// Monochrome is an open-source TIDAL web UI. This provider directly accesses
/// the TIDAL streaming API for search, metadata, and download URLs.
///
/// Supports: TIDAL Hi-Res FLAC (up to 24-bit/192kHz), AAC (320kbps).
/// </summary>
public partial class MonochromeProvider : IMusicProvider
{
    private readonly ILogger<MonochromeProvider> _logger;
    private readonly HttpClient _httpClient;

    public string Id => "monochrome";
    public string Name => "Monochrome (TIDAL)";
    public string Description => "TIDAL Hi-Res FLAC streaming via the same API used by monochrome.tf. Requires a TIDAL account credential.";
    public string Icon => "🎵";

    public IReadOnlyList<Platform> SupportedPlatforms { get; } = new[]
    {
        Platform.Tidal,
    };

    public IReadOnlyList<TrackQuality> SupportedQualities { get; } = ProviderQualities.Monochrome;

    // TIDAL API constants (same as used by Monochrome)
    private const string TidalApiBase = "https://api.tidal.com/v1";
    private const string TidalToken = "txNoH4kkV41MfH25"; // Monochrome's token (publicly known)

    public MonochromeProvider(ILogger<MonochromeProvider> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _httpClient.DefaultRequestHeaders.Add("X-Tidal-Token", TidalToken);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Monochrome/1.0");
    }

    /// <summary>
    /// TIDAL session ID — required for downloads.
    /// Can be obtained by logging into TIDAL (see GetTrackInfoAsync flow).
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// TIDAL country code for content access (default: US).
    /// </summary>
    public string CountryCode { get; set; } = "US";

    // ── Search ──────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, Platform? platform = null, CancellationToken ct = default)
    {
        try
        {
            var url = $"{TidalApiBase}/search/top-hits?query={HttpUtility.UrlEncode(query)}&limit=10&countryCode={CountryCode}";
            var response = await _httpClient.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var results = new List<SearchResult>();

            if (doc.RootElement.TryGetProperty("tracks", out var tracks) &&
                tracks.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    var title = item.GetProperty("title").GetString() ?? "Unknown";
                    var artist = item.TryGetProperty("artist", out var art)
                        ? art.GetProperty("name").GetString() ?? ""
                        : "";
                    var album = item.TryGetProperty("album", out var alb)
                        ? alb.GetProperty("title").GetString() ?? ""
                        : null;
                    var id = item.GetProperty("id").GetInt64();
                    var trackUrl = $"https://tidal.com/browse/track/{id}";

                    results.Add(new SearchResult(
                        title, artist, album, trackUrl, Platform.Tidal,
                        new[] { new TrackQuality(320, MediaType.AAC), new TrackQuality(24, MediaType.FLAC) }
                    ));
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monochrome/TIDAL search failed for {Query}", query);
            return Array.Empty<SearchResult>();
        }
    }

    // ── Get Track Info ──────────────────────────────────────────────────
    public async Task<TrackInfo?> GetTrackInfoAsync(string url, CancellationToken ct = default)
    {
        try
        {
            if (!TryExtractTidalTrackId(url, out var trackId))
            {
                _logger.LogWarning("Could not extract TIDAL track ID from {Url}", url);
                return null;
            }

            var apiUrl = $"{TidalApiBase}/tracks/{trackId}?countryCode={CountryCode}";
            var response = await _httpClient.GetAsync(apiUrl, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            var title = root.GetProperty("title").GetString() ?? "Unknown";
            var artist = root.TryGetProperty("artist", out var art)
                ? art.GetProperty("name").GetString() ?? ""
                : "";
            var album = root.TryGetProperty("album", out var alb)
                ? alb.GetProperty("title").GetString() ?? ""
                : null;
            var coverUrl = root.TryGetProperty("album", out var alb2) &&
                           alb2.TryGetProperty("cover", out var cover)
                ? cover.GetString()
                : null;

            if (coverUrl != null && !coverUrl.StartsWith("http"))
                coverUrl = $"https://resources.tidal.com/images/{coverUrl.Replace("--", "/")}.jpg";

            var qualities = new List<TrackQuality>
            {
                new(320, MediaType.AAC),
                new(24, MediaType.FLAC),
            };

            return new TrackInfo(title, artist, album, coverUrl, url, Platform.Tidal, qualities);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monochrome/TIDAL getTrackInfo failed for {Url}", url);
            return null;
        }
    }

    // ── Download ────────────────────────────────────────────────────────
    public async Task<DownloadResult> DownloadAsync(string trackUrl, TrackQuality quality, string outputDirectory, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(SessionId))
            {
                // TIDAL downloads require authentication.
                // The session ID can be obtained by logging in via the TIDAL API.
                // For now, guide the user to provide credentials.
                _logger.LogInformation(
                    "TIDAL download requires authentication. " +
                    "Set the SessionId property by logging into TIDAL first.");
                return new DownloadResult(false, null,
                    "TIDAL download requires authentication. Log into TIDAL via Accounts page first.", null);
            }

            if (!TryExtractTidalTrackId(trackUrl, out var trackId))
                return new DownloadResult(false, null, "Could not extract TIDAL track ID", null);

            Directory.CreateDirectory(outputDirectory);

            // Map quality to TIDAL's audio quality parameter
            var qualityParam = quality switch
            {
                { Bitrate: 24, Format: MediaType.FLAC } => "HI_RES",
                { Bitrate: 320, Format: MediaType.AAC } => "HIGH",
                { Bitrate: 16, Format: MediaType.FLAC } => "LOSSLESS",
                _ => "HIGH",
            };

            // TIDAL streaming endpoint for getting the manifest URL
            var streamUrl = $"{TidalApiBase}/tracks/{trackId}/playurl" +
                            $"?countryCode={CountryCode}&audioquality={qualityParam}" +
                            $"&playbackmode=STREAM&assetpresentation=FULL";

            using var request = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            request.Headers.Add("X-Tidal-SessionId", SessionId);
            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);

            // The response contains a "manifest" or "urls" array
            string? directUrl = null;
            if (doc.RootElement.TryGetProperty("urls", out var urls) && urls.GetArrayLength() > 0)
            {
                directUrl = urls[0].GetString();
            }
            else if (doc.RootElement.TryGetProperty("manifest", out var manifest))
            {
                directUrl = manifest.GetString();
            }
            else if (doc.RootElement.TryGetProperty("url", out var urlProp))
            {
                directUrl = urlProp.GetString();
            }

            if (string.IsNullOrEmpty(directUrl))
                return new DownloadResult(false, null, "No stream URL in TIDAL response", null);

            var ext = quality.Format switch
            {
                MediaType.FLAC => "flac",
                MediaType.AAC => "m4a",
                MediaType.MP3 => "mp3",
                _ => "bin"
            };

            var fileName = $"tidal_{trackId}_{qualityParam}.{ext}";
            var filePath = Path.Combine(outputDirectory, fileName);

            await TrackFileHelper.DownloadFileAsync(directUrl, filePath);

            return File.Exists(filePath)
                ? new DownloadResult(true, filePath, null, quality)
                : new DownloadResult(false, null, "Download completed but file not found", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Monochrome/TIDAL download failed for {Url}", trackUrl);
            return new DownloadResult(false, null, ex.Message, null);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────
    private static bool TryExtractTidalTrackId(string url, out long id)
    {
        id = 0;
        var match = Regex.Match(url, @"track[/=](\d+)");
        if (match.Success && long.TryParse(match.Groups[1].Value, out var parsed))
        {
            id = parsed;
            return true;
        }
        return false;
    }
}
