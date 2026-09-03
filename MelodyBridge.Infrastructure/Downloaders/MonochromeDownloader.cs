using System.Text.Json;
using MelodyBridge.Core;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// Monochrome downloader plugin: community-hosted "Hi-Fi API" instances that
/// proxy TIDAL (monochrome.tf ecosystem). No TIDAL account needed: the
/// instances handle auth internally. Serves FLAC (LOSSLESS / HI_RES_LOSSLESS)
/// and AAC. Instances are tried in order until one answers, and a failing
/// one is skipped until the next call re-runs the fallback from the top.
///
/// API shape (v2.x, FastAPI-style):
///   /search/?s={query}              → {"data":{"items":[track objects]}}
///   /track/?id={id}&quality={Q}     → JSON with a manifest/stream URL
/// </summary>
public class MonochromeDownloader : IDownloader
{
    private readonly ILogger<MonochromeDownloader> _logger;
    private readonly HttpClient _http;

    // Monochrome serves TIDAL FLAC (lossless) and AAC (lossy); the API does not promise bitrates.
    public static readonly PluginCapabilities Caps =
        new([AudioFormat.Flac, AudioFormat.Aac], null, null, true, true);
    public PluginCapabilities Capabilities => Caps;

    public string Id => "monochrome";
    public string Name => "Monochrome (TIDAL)";
    public string Description => "Community TIDAL rips via monochrome.tf Hi-Fi API instances: FLAC/AAC, mirror fallback";

    // Official instance list (https://monochrome.tf/instances.json), tried in order.
    private static readonly string[] Instances =
    [
        "https://eu-central.monochrome.tf",
        "https://us-west.monochrome.tf",
        "https://arran.monochrome.tf",
        "https://api.monochrome.tf",
        "https://monochrome-api.samidy.com",
        "https://triton.squid.wtf",
        "https://wolf.qqdl.site",
        "https://maus.qqdl.site",
        "https://vogel.qqdl.site",
        "https://hund.qqdl.site",
        "https://tidal.kinoplus.online",
    ];

    /// <summary>Instance index that last answered; -1 = unknown, re-run fallback.</summary>
    private int _workingInstance = -1;

    public MonochromeDownloader(HttpClient http, ILogger<MonochromeDownloader> logger)
    {
        _http = http;
        _logger = logger;
    }

    // ── Availability ─────────────────────────────────────────────────────

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // Cheap probe per instance (single-char search); whole sweep must
        // stay within ~10s so the waterfall is not stalled.
        using var sweep = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sweep.CancelAfter(TimeSpan.FromSeconds(10));

        for (var i = 0; i < Instances.Length; i++)
        {
            if (await ProbeInstanceAsync(i, sweep.Token))
            {
                _workingInstance = i;
                return true;
            }
        }
        _workingInstance = -1;
        return false;
    }

    private async Task<bool> ProbeInstanceAsync(int index, CancellationToken ct)
    {
        try
        {
            var url = Instances[index].TrimEnd('/') + "/search/?s=a";
            using var resp = await _http.GetAsync(url, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Monochrome instance {Instance} probe failed", Instances[index]);
            return false;
        }
    }

    // ── Search ───────────────────────────────────────────────────────────

    public async Task<DownloaderSearchHit?> SearchAsync(
        string artist, string title, DownloadQuality quality, CancellationToken ct = default)
    {
        var query = (artist + " " + title).Trim();
        if (query.Length == 0) return null;

        // Start at the cached instance (or the first one) and wrap around
        // through the whole list so one dead mirror never wedges the plugin.
        for (var attempt = 0; attempt < Instances.Length; attempt++)
        {
            var index = _workingInstance < 0
                ? attempt
                : (_workingInstance + attempt) % Instances.Length;
            try
            {
                var url = Instances[index].TrimEnd('/') + "/search/?s=" + Uri.EscapeDataString(query);
                using var resp = await _http.GetAsync(url, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    _logger.LogDebug("Monochrome instance {Instance} returned {Status} for search",
                        Instances[index], (int)resp.StatusCode);
                    continue;
                }

                var body = await resp.Content.ReadAsStringAsync(ct);
                var hit = ParseSearchItems(body, artist, title, quality);
                if (hit is not null)
                {
                    _workingInstance = index;
                    return hit;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Monochrome instance {Instance} failed for search '{Query}'",
                    Instances[index], query);
            }
        }

        return null;
    }

    // ── Download ─────────────────────────────────────────────────────────

    public async Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl,
        string outputDirectory,
        string? melodyId,
        DownloadQuality? quality = null,
        CancellationToken ct = default)
    {
        try
        {
            if (!TryExtractTrackId(sourceUrl, out var trackId))
                return new DownloaderDownloadResult(false, null, "not a TIDAL track URL: " + sourceUrl);

            quality ??= DownloadQuality.Any;
            var qualityParam = MapQuality(quality);
            Directory.CreateDirectory(outputDirectory);

            for (var attempt = 0; attempt < Instances.Length; attempt++)
            {
                var index = _workingInstance < 0
                    ? attempt
                    : (_workingInstance + attempt) % Instances.Length;
                try
                {
                    var url = Instances[index].TrimEnd('/') + $"/track/?id={trackId}&quality={qualityParam}";
                    using var resp = await _http.GetAsync(url, ct);
                    if (!resp.IsSuccessStatusCode)
                    {
                        // Rips that fail upstream answer 403/500 {"detail":"Upstream API error"}.
                        _logger.LogDebug("Monochrome instance {Instance} returned {Status} for track {TrackId}",
                            Instances[index], (int)resp.StatusCode, trackId);
                        continue;
                    }

                    var body = await resp.Content.ReadAsStringAsync(ct);
                    var manifestUrl = ExtractManifestUrl(body);
                    if (string.IsNullOrEmpty(manifestUrl))
                        continue; // unparseable shape: try next instance

                    var ext = ExtensionFromManifest(manifestUrl, quality.Format);
                    var filePath = Path.Combine(outputDirectory, $"tidal_{trackId}_{qualityParam}{ext}");

                    using var fileResp = await _http.GetAsync(manifestUrl, ct);
                    if (!fileResp.IsSuccessStatusCode)
                    {
                        _logger.LogDebug("Monochrome manifest download from {Manifest} returned {Status}",
                            manifestUrl, (int)fileResp.StatusCode);
                        continue;
                    }

                    await using (var src = await fileResp.Content.ReadAsStreamAsync(ct))
                    await using (var dst = File.Create(filePath))
                    {
                        await src.CopyToAsync(dst, 81920, ct);
                    }

                    if (new FileInfo(filePath).Length == 0)
                    {
                        try { File.Delete(filePath); } catch { /* best effort */ }
                        continue;
                    }

                    YtDlpDownloader.TagDownloadedFile(filePath, expectedTitle: null);
                    _workingInstance = index;
                    return new DownloaderDownloadResult(true, filePath, null);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Monochrome instance {Instance} failed for track {TrackId}",
                        Instances[index], trackId);
                }
            }

            return new DownloaderDownloadResult(false, null,
                $"all Monochrome instances failed for TIDAL track {trackId}");
        }
        catch (Exception ex)
        {
            return new DownloaderDownloadResult(false, null, ex.Message);
        }
    }

    // ── Mapping helpers ──────────────────────────────────────────────────

    /// <summary>Maps a download quality to the API's quality parameter.</summary>
    internal static string MapQuality(DownloadQuality quality) => quality.Format switch
    {
        AudioFormat.Flac => quality.MaxKbps is null ? "HI_RES_LOSSLESS" : "LOSSLESS",
        AudioFormat.Mp3 or AudioFormat.Aac => "HIGH",
        _ => "HI_RES_LOSSLESS",
    };

    /// <summary>File extension from the manifest URL path, defaulting by requested format.</summary>
    private static string ExtensionFromManifest(string manifestUrl, AudioFormat format)
    {
        try
        {
            var ext = Path.GetExtension(new Uri(manifestUrl).AbsolutePath);
            if (!string.IsNullOrEmpty(ext))
                return ext.ToLowerInvariant(); // .flac / .m4a / .mp3
        }
        catch { /* non-absolute manifest URLs default below */ }
        return format switch
        {
            AudioFormat.Flac => ".flac",
            AudioFormat.Mp3 => ".mp3",
            AudioFormat.Aac => ".m4a",
            _ => ".flac",
        };
    }

    // ── Parsers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Parses a /search/?s= response ({"data":{"items":[…]}}) into the best
    /// hit: every item is ranked by title/artist confidence, lossless
    /// availability breaks ties. Null when there are no usable items.
    /// </summary>
    internal static DownloaderSearchHit? ParseSearchItems(
        string json, string artist, string fallbackTitle, DownloadQuality quality)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array ||
                items.GetArrayLength() == 0)
                return null;

            // Rank every item instead of trusting the API's first result:
            // the list routinely buries the real match under remixes and
            // live versions. Fuzzy score decides (an exact title outranks
            // a remix of it); a lossless hit breaks ties.
            DownloaderSearchHit? best = null;
            var bestScore = -1.0;
            var bestLossless = false;
            foreach (var item in items.EnumerateArray())
            {
                var hitTitle = GetString(item, "title") ?? fallbackTitle;
                var hitArtist = GetArtistName(item);
                if (!item.TryGetProperty("id", out var idProp) ||
                    !idProp.TryGetInt64(out var trackId))
                    continue;

                TimeSpan? duration = item.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    ? TimeSpan.FromSeconds(d.GetInt32())
                    : null;

                var score = Services.FuzzyMatcher.Score(
                    artist, fallbackTitle, hitArtist: hitArtist, hitTitle: hitTitle);
                var lossless = IsLossless(item);

                var better = best is null
                    || score > bestScore
                    || (score == bestScore && lossless && !bestLossless);
                if (!better) continue;

                best = new DownloaderSearchHit(
                    Title: hitTitle,
                    Artist: hitArtist,
                    SourceUrl: $"https://tidal.com/browse/track/{trackId}",
                    Duration: duration,
                    MatchConfidence: Services.FuzzyMatcher.Confidence(
                        artist, fallbackTitle, hitArtist: hitArtist, hitTitle: hitTitle));
                bestScore = score;
                bestLossless = lossless;
            }
            return best;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the item exposes a lossless stream, matching the Hi-Res /
    /// Lossless flags the API reports (audioQuality or quality key).
    /// A lossless hit satisfies every lossy band; the download step still
    /// applies the requested cap.
    /// </summary>
    private static bool IsLossless(JsonElement item)
        => (GetString(item, "audioQuality") ?? GetString(item, "quality") ?? string.Empty)
            .Contains("LOSSLESS", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Extracts the manifest/stream URL from a /track/ response, covering
    /// the observed shapes: data.data.attributes.uri (the old chain),
    /// data.data.url / manifest / urls[0], data.manifest, data.url, and flat
    /// root url/manifest/uri.
    /// </summary>
    internal static string? ExtractManifestUrl(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("data", out var outer))
            {
                // Preferred chain: data.data.attributes.uri
                if (outer.TryGetProperty("data", out var inner) &&
                    inner.TryGetProperty("attributes", out var attrs))
                {
                    var uri = GetString(attrs, "uri") ?? GetString(attrs, "url");
                    if (!string.IsNullOrEmpty(uri)) return uri;
                }

                // data.data.{url,manifest,urls[0]}
                if (outer.TryGetProperty("data", out var playback))
                {
                    var url = GetString(playback, "url") ?? GetString(playback, "manifest");
                    if (!string.IsNullOrEmpty(url)) return url;
                    if (playback.TryGetProperty("urls", out var urls) &&
                        urls.ValueKind == JsonValueKind.Array && urls.GetArrayLength() > 0 &&
                        urls[0].ValueKind == JsonValueKind.String)
                        return urls[0].GetString();
                }

                // FastAPI v2.x flat shape: data.manifest / data.url
                var direct = GetString(outer, "manifest") ?? GetString(outer, "url");
                if (!string.IsNullOrEmpty(direct)) return direct;
            }

            // Flat root fallback
            return GetString(root, "url") ?? GetString(root, "manifest") ?? GetString(root, "uri");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Extracts the numeric TIDAL track id from a URL or a bare id string.</summary>
    internal static bool TryExtractTrackId(string sourceUrl, out long trackId)
    {
        trackId = 0;
        if (string.IsNullOrWhiteSpace(sourceUrl)) return false;

        // Bare numeric id
        if (long.TryParse(sourceUrl.Trim(), out trackId) && trackId > 0)
            return true;

        // Last path segment of .../track/{id}
        var lastSegment = sourceUrl.TrimEnd('/').Split('/')[^1];
        return long.TryParse(lastSegment, out trackId) && trackId > 0;
    }

    /// <summary>TIDAL objects carry artist {name} or artists[].name.</summary>
    private static string? GetArtistName(JsonElement item)
    {
        if (item.TryGetProperty("artist", out var artist))
        {
            if (artist.ValueKind == JsonValueKind.String)
                return artist.GetString();
            var name = GetString(artist, "name");
            if (!string.IsNullOrEmpty(name)) return name;
        }
        if (item.TryGetProperty("artists", out var artists) &&
            artists.ValueKind == JsonValueKind.Array && artists.GetArrayLength() > 0)
            return GetString(artists[0], "name");
        return null;
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
