using System.Text.Json;
using System.Web;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Helpers;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Providers;

/// <summary>
/// Shared client for querying community-hosted Monochrome Hi-Fi API instances
/// (the same backend that powers monochrome.tf). Used by MonochromeProvider
/// and LucidaProvider for TIDAL search and metadata lookup.
///
/// API route patterns (from monochrome's HiFiClient.query()):
///   /search/?q=&lt;query&gt;       → search tracks
///   /info/?id=&lt;id&gt;           → track metadata
///   /trackManifests/?id=&amp;quality= → signed manifest URL for download
/// </summary>
public class MonochromeApiClient
{
    // Community Hi-Fi API instances — tried in order until one responds
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

    private readonly HttpClient _httpClient;

    public MonochromeApiClient()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MelodyBridge/1.0");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var endpoint = $"/search/?q={HttpUtility.UrlEncode(query)}&limit=10";

        foreach (var instance in ApiInstances)
        {
            try
            {
                var url = instance.TrimEnd('/') + endpoint;
                using var response = await _httpClient.GetAsync(url, ct);

                if (!response.IsSuccessStatusCode)
                    continue;

                var body = await response.Content.ReadAsStringAsync(ct);
                return ParseSearchResponse(body);
            }
            catch
            {
                // Try next instance
            }
        }

        return Array.Empty<SearchResult>();
    }

    public async Task<TrackInfo?> GetTrackInfoAsync(string url, CancellationToken ct = default)
    {
        if (!TryExtractTidalTrackId(url, out var trackId))
            return null;

        var endpoint = $"/info/?id={trackId}";

        foreach (var instance in ApiInstances)
        {
            try
            {
                var instanceUrl = instance.TrimEnd('/') + endpoint;
                using var response = await _httpClient.GetAsync(instanceUrl, ct);

                if (!response.IsSuccessStatusCode)
                    continue;

                var body = await response.Content.ReadAsStringAsync(ct);
                return ParseTrackInfoResponse(body, url, trackId);
            }
            catch
            {
                // Try next instance
            }
        }

        return null;
    }

    public async Task<DownloadResult> DownloadAsync(string trackUrl, TrackQuality quality, string outputDirectory, CancellationToken ct = default)
    {
        if (!TryExtractTidalTrackId(trackUrl, out var trackId))
            return new DownloadResult(false, null, "Could not extract TIDAL track ID", null);

        Directory.CreateDirectory(outputDirectory);

        var qualityParam = quality switch
        {
            { Bitrate: 24, Format: MediaType.FLAC } => "HI_RES",
            { Bitrate: 320, Format: MediaType.AAC } => "HIGH",
            { Bitrate: 16, Format: MediaType.FLAC } => "LOSSLESS",
            _ => "HIGH",
        };

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
                catch
                {
                    // Try next instance/endpoint
                }
            }
        }

        return new DownloadResult(false, null, "All Monochrome API instances failed for download", null);
    }

    // ── Response Parsing ────────────────────────────────────────────────

    private static IReadOnlyList<SearchResult> ParseSearchResponse(string json)
    {
        var results = new List<SearchResult>();

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

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
        catch (JsonException)
        {
            // Ignore malformed responses
        }

        return results;
    }

    private static TrackInfo? ParseTrackInfoResponse(string json, string originalUrl, long trackId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            JsonElement trackData;
            if (root.TryGetProperty("data", out var data))
                trackData = data;
            else
                trackData = root;

            var title = trackData.GetProperty("title").GetString() ?? "Unknown";
            var artist = ExtractArtistName(trackData);
            var album = trackData.TryGetProperty("album", out var alb)
                ? alb.GetProperty("title").GetString() ?? null
                : null;

            // Cover URL: use TIDAL cover pattern
            string? coverUrl = null;
            if (trackData.TryGetProperty("album", out var albumData) &&
                albumData.TryGetProperty("cover", out var cover))
            {
                var coverStr = cover.GetString();
                if (!string.IsNullOrEmpty(coverStr))
                    coverUrl = coverStr.StartsWith("http") ? coverStr : $"https://tidal.com/images/{coverStr}";
            }

            return new TrackInfo(title, artist, album, coverUrl, originalUrl, Platform.Tidal,
                new[] { new TrackQuality(320, MediaType.AAC), new TrackQuality(24, MediaType.FLAC) });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractManifestUrl(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // /trackManifests/ response: { data: { manifestUrl: "..." } }
            if (root.TryGetProperty("data", out var data) &&
                data.TryGetProperty("manifestUrl", out var manifestUrlProp))
            {
                var manifestUrl = manifestUrlProp.GetString();
                if (!string.IsNullOrEmpty(manifestUrl)) return manifestUrl;
            }

            // /track/ response: { data: { url: "..." } }
            if (root.TryGetProperty("data", out var data2) &&
                data2.TryGetProperty("url", out var urlProp))
            {
                var urlStr = urlProp.GetString();
                if (!string.IsNullOrEmpty(urlStr)) return urlStr;
            }

            // Fallback: check root for url property
            if (root.TryGetProperty("url", out var directUrlProp))
            {
                var urlStr = directUrlProp.GetString();
                if (!string.IsNullOrEmpty(urlStr)) return urlStr;
            }
        }
        catch (JsonException)
        {
            // Ignore
        }

        return null;
    }

    private static string ExtractArtistName(JsonElement trackData)
    {
        if (trackData.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
        {
            var names = new List<string>();
            foreach (var a in artists.EnumerateArray())
            {
                if (a.TryGetProperty("name", out var name))
                    names.Add(name.GetString() ?? "");
            }
            if (names.Count > 0)
                return string.Join(", ", names);
        }

        if (trackData.TryGetProperty("artist", out var artist))
        {
            if (artist.ValueKind == JsonValueKind.Object &&
                artist.TryGetProperty("name", out var name))
                return name.GetString() ?? "";
            if (artist.ValueKind == JsonValueKind.String)
                return artist.GetString() ?? "";
        }

        return "";
    }

    private static bool TryExtractTidalTrackId(string url, out long id)
    {
        id = 0;
        var match = System.Text.RegularExpressions.Regex.Match(url, @"track[/=](\d+)");
        return match.Success && long.TryParse(match.Groups[1].Value, out id);
    }
}
