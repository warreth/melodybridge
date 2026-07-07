using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Helpers;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Providers;

/// <summary>
/// Music provider that uses community-hosted Monochrome Hi-Fi API instances
/// (the same backend that powers monochrome.tf and related projects).
/// No TIDAL credentials needed — the API instances handle TIDAL authentication
/// internally. Falls back between multiple instances for reliability.
///
/// API route patterns (from monochrome's HiFiClient.query()):
///   /search/?q=&lt;query&gt;       → search tracks/albums/artists
///   /info/?id=&lt;id&gt;           → track metadata
///   /trackManifests/?id=&amp;quality= → signed manifest URL for download
///
/// Supports: TIDAL Hi-Res FLAC (up to 24-bit/192kHz), AAC (320kbps).
/// </summary>
public partial class MonochromeProvider : IMusicProvider
{
    private readonly ILogger<MonochromeProvider> _logger;
    private readonly HttpClient _httpClient;

    public string Id => "monochrome";
    public string Name => "Monochrome (TIDAL)";
    public string Description => "TIDAL Hi-Res FLAC via community Monochrome Hi-Fi API instances. No TIDAL account needed — instances handle auth internally.";
    public string Icon => "🎵";

    public IReadOnlyList<Platform> SupportedPlatforms { get; } = new[]
    {
        Platform.Tidal,
    };

    public IReadOnlyList<TrackQuality> SupportedQualities { get; } = ProviderQualities.Monochrome;

    // Community Hi-Fi API instances (same backends used by monochrome.tf)
    // The provider tries each in order until one responds.
    private static readonly string[] ApiInstances =
    [
        "https://monochrome-api.samidy.com",
        "https://api.monochrome.tf",
        "https://hifi.geeked.wtf",
        "https://wolf.qqdl.site",
        "https://maus.qqdl.site",
        "https://vogel.qqdl.site",
        "https://katze.qqdl.site",
        "https://hund.qqdl.site",
        "https://tidal.kinoplus.online",
    ];

    public MonochromeProvider(ILogger<MonochromeProvider> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MelodyBridge/1.0");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    // ── Search ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, Platform? platform = null, CancellationToken ct = default)
    {
        var endpoint = $"/search/?q={HttpUtility.UrlEncode(query)}&limit=10";

        foreach (var instance in ApiInstances)
        {
            try
            {
                var url = instance.TrimEnd('/') + endpoint;
                using var response = await _httpClient.GetAsync(url, ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Monochrome instance {Instance} returned {Status} for search", instance, response.StatusCode);
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                return ParseSearchResponse(body);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Monochrome instance {Instance} failed for search", instance);
            }
        }

        _logger.LogError("All Monochrome API instances failed for search query: {Query}", query);
        return Array.Empty<SearchResult>();
    }

    // ── Get Track Info ──────────────────────────────────────────────────

    public async Task<TrackInfo?> GetTrackInfoAsync(string url, CancellationToken ct = default)
    {
        if (!ProviderHelpers.TryExtractTidalTrackId(url, out var trackId))
        {
            _logger.LogWarning("Could not extract TIDAL track ID from {Url}", url);
            return null;
        }

        var endpoint = $"/info/?id={trackId}";

        foreach (var instance in ApiInstances)
        {
            try
            {
                var apiUrl = instance.TrimEnd('/') + endpoint;
                using var response = await _httpClient.GetAsync(apiUrl, ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Monochrome instance {Instance} returned {Status} for track info", instance, response.StatusCode);
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                var info = ParseTrackInfoResponse(body, url, trackId);
                if (info != null) return info;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Monochrome instance {Instance} failed for track info", instance);
            }
        }

        _logger.LogError("All Monochrome API instances failed for track {TrackId}", trackId);
        return null;
    }

    // ── Download ────────────────────────────────────────────────────────

    public async Task<DownloadResult> DownloadAsync(string trackUrl, TrackQuality quality, string outputDirectory, CancellationToken ct = default)
    {
        if (!ProviderHelpers.TryExtractTidalTrackId(trackUrl, out var trackId))
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

        // Try the trackManifests endpoint first, fall back to track endpoint
        var endpoints = new[]
        {
            $"/trackManifests/?id={trackId}&quality={qualityParam}",
            $"/track/?id={trackId}&quality={qualityParam}",
        };

        foreach (var instance in ApiInstances)
        {
            foreach (var endpoint in endpoints)
            {
                try
                {
                    var url = instance.TrimEnd('/') + endpoint;
                    using var response = await _httpClient.GetAsync(url, ct);

                    if (!response.IsSuccessStatusCode) continue;

                    var body = await response.Content.ReadAsStringAsync(ct);
                    var manifestUrl = ExtractManifestUrl(body);
                    if (string.IsNullOrEmpty(manifestUrl)) continue;

                    var ext = quality.Format switch
                    {
                        MediaType.FLAC => "flac",
                        MediaType.AAC => "m4a",
                        MediaType.MP3 => "mp3",
                        _ => "bin"
                    };

                    var fileName = $"tidal_{trackId}_{qualityParam}.{ext}";
                    var filePath = Path.Combine(outputDirectory, fileName);

                    await TrackFileHelper.DownloadFileAsync(manifestUrl, filePath);

                    if (File.Exists(filePath))
                        return new DownloadResult(true, filePath, null, quality);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Monochrome instance {Instance} endpoint {Endpoint} failed", instance, endpoint);
                }
            }
        }

        return new DownloadResult(false, null, "All Monochrome API instances failed for download — check network connectivity or authentication session", null);
    }

    // ── Response Parsing ────────────────────────────────────────────────

    private IReadOnlyList<SearchResult> ParseSearchResponse(string json)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // The Hi-Fi API search response has: data.tracks.items[]
            if (!root.TryGetProperty("data", out var data))
                return results;

            if (!data.TryGetProperty("tracks", out var tracks) ||
                !tracks.TryGetProperty("items", out var items))
                return results;

            foreach (var item in items.EnumerateArray())
            {
                var title = item.GetProperty("title").GetString() ?? "Unknown";
                var artist = ExtractArtistName(item);
                var album = item.TryGetProperty("album", out var alb)
                    ? alb.GetProperty("title").GetString() ?? null
                    : null;
                var id = item.GetProperty("id").GetInt64();
                var trackUrl = $"https://tidal.com/browse/track/{id}";

                results.Add(new SearchResult(
                    title, artist, album, trackUrl, Platform.Tidal,
                    new[] { new TrackQuality(320, MediaType.AAC), new TrackQuality(24, MediaType.FLAC) }
                ));
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse search response JSON");
        }

        return results;
    }

    private TrackInfo? ParseTrackInfoResponse(string json, string originalUrl, long trackId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Hi-Fi API /info/ response: { version, data: { ...track fields... } }
            JsonElement trackData;
            if (root.TryGetProperty("data", out var data))
            {
                trackData = data;
            }
            else
            {
                trackData = root;
            }

            var title = trackData.GetProperty("title").GetString() ?? "Unknown";
            var artist = ExtractArtistName(trackData);
            var album = trackData.TryGetProperty("album", out var alb)
                ? alb.GetProperty("title").GetString() ?? null
                : null;
            var coverUrl = ExtractCoverUrl(trackData);

            return new TrackInfo(title, artist, album, coverUrl, originalUrl, Platform.Tidal, SupportedQualities);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse track info response for track {TrackId}", trackId);
            return null;
        }
    }

    /// <summary>
    /// Extracts the manifest/signed URL from trackManifests or track endpoint responses.
    /// </summary>
    private static string? ExtractManifestUrl(string json)
    {
        using var doc = JsonDocument.Parse(json);

        // TrackManifests response: data.data.attributes.uri
        if (doc.RootElement.TryGetProperty("data", out var outerData))
        {
            if (outerData.TryGetProperty("data", out var innerData) &&
                innerData.TryGetProperty("attributes", out var attrs) &&
                attrs.TryGetProperty("uri", out var uri))
            {
                return uri.GetString();
            }

            // Track response: data.data.manifest or data.data.url
            if (outerData.TryGetProperty("data", out var playbackData))
            {
                if (playbackData.TryGetProperty("url", out var url))
                    return url.GetString();
                if (playbackData.TryGetProperty("manifest", out var manifest))
                    return manifest.GetString();
                if (playbackData.TryGetProperty("urls", out var urls) && urls.GetArrayLength() > 0)
                    return urls[0].GetString();
            }
        }

        // Flat responses: try direct fields
        if (doc.RootElement.TryGetProperty("url", out var directUrl))
            return directUrl.GetString();
        if (doc.RootElement.TryGetProperty("manifest", out var directManifest))
            return directManifest.GetString();
        if (doc.RootElement.TryGetProperty("uri", out var directUri))
            return directUri.GetString();

        return null;
    }

    private static string ExtractArtistName(JsonElement item)
    {
        // TIDAL track objects have artist { name } or artists[].name
        if (item.TryGetProperty("artist", out var artist))
        {
            if (artist.ValueKind == JsonValueKind.String)
                return artist.GetString() ?? "";
            if (artist.TryGetProperty("name", out var name))
                return name.GetString() ?? "";
        }

        if (item.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0)
        {
            var first = artists[0];
            if (first.TryGetProperty("name", out var firstName))
                return firstName.GetString() ?? "";
        }

        return "";
    }

    private static string? ExtractCoverUrl(JsonElement item)
    {
        if (item.TryGetProperty("album", out var album) &&
            album.TryGetProperty("cover", out var cover))
        {
            var coverVal = cover.GetString();
            if (!string.IsNullOrEmpty(coverVal))
            {
                if (coverVal.StartsWith("http")) return coverVal;
                return $"https://resources.tidal.com/images/{coverVal.Replace("--", "/")}/1280x1280.jpg";
            }
        }
        return null;
    }

    // ── Helpers ─────────────────────────────────────────────────────────
    // Delegates to ProviderHelpers (kept for reflection-based tests)
    private static bool TryExtractTidalTrackId(string url, out long id) => ProviderHelpers.TryExtractTidalTrackId(url, out id);
}
