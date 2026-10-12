using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// DAB Music downloader plugin (dabmusic.xyz), the successor of the old
/// dab.yeet.su service: a community Qobuz ripper that needs no account
/// for search or stream. Serves FLAC (up to 24-bit/192 kHz) and MP3 320.
///
/// API shape (public OpenAPI spec, security: [] for both endpoints):
///   /api/search?q={query}&amp;type=track&amp;limit=5
///     → {"tracks":[{id,title,artist,duration,albumCover,audioQuality},…]}
///   /api/stream?trackId={id}&amp;quality={27|7|6|5}
///     → {"url":"https://…"}    (quality: 27=24/192 FLAC, 7=24/96,
///                               6=16/44.1, 5=MP3 320)
///
/// The service is community-run and regularly offline or moved between
/// domains; availability is checked cheaply and cached, so the waterfall
/// skips the plugin instead of stalling when it is down.
/// </summary>
public class DabMusicDownloader : IDownloader
{
    private const string BaseUrl = "https://dabmusic.xyz";

    // Quality ids the service understands, hi-res first.
    internal const string QualityHiRes = "27";  // 24-bit / 192 kHz FLAC
    internal const string QualityCd = "6";      // 16-bit / 44.1 kHz FLAC
    internal const string QualityMp3 = "5";     // MP3 320

    private readonly ILogger<DabMusicDownloader> _logger;
    private readonly HttpClient _http;
    private readonly AvailabilityCache _availability = new();

    // DAB serves Qobuz FLAC (lossless) and MP3 320; the API does not promise per-file bitrates.
    public static readonly PluginCapabilities Caps =
        new([AudioFormat.Flac, AudioFormat.Mp3], null, null, SupportsLossless: true, SupportsLossy: true);
    public PluginCapabilities Capabilities => Caps;

    public string Id => "dabmusic";
    public string Name => "DAB Music (Qobuz)";
    public string Description => "dabmusic.xyz community Qobuz rips: FLAC up to 24/192 and MP3 320, no account needed";

    private static readonly TimeSpan SearchRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StreamRequestTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FileRequestTimeout = TimeSpan.FromMinutes(5);

    public DabMusicDownloader(HttpClient http, ILogger<DabMusicDownloader> logger)
    {
        _http = http;
        _logger = logger;
    }

    // ── Availability ─────────────────────────────────────────────────────

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_availability.TryGet(out var cached))
            return cached;

        var available = false;
        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromSeconds(6));
            using var resp = await _http.GetAsync(
                $"{BaseUrl}/api/search?q=a&type=track&limit=1", probe.Token);
            available = resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DAB Music availability probe failed");
        }

        _availability.Store(available);
        return available;
    }

    // ── Search ───────────────────────────────────────────────────────────

    public async Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
    {
        // Qobuz indexes ISRCs, so a known code is the exact way in and
        // skips the noise of a text search; a search that does not return
        // a confident match falls through to the text query.
        if (query.HasIsrc)
        {
            var byIsrc = await SearchTextAsync(query, query.Isrc!, ct);
            if (byIsrc?.MatchConfidence == MatchConfidence.High) return byIsrc;
        }

        var searchText = query.SearchText;
        if (searchText.Length == 0) return null;
        return await SearchTextAsync(query, searchText, ct);
    }

    /// <summary>One search request with the given query text, ranked.</summary>
    private async Task<DownloaderSearchHit?> SearchTextAsync(TrackQuery query, string searchText, CancellationToken ct)
    {
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
            request.CancelAfter(SearchRequestTimeout);
            using var resp = await _http.GetAsync(
                $"{BaseUrl}/api/search?q={Uri.EscapeDataString(searchText)}&type=track&limit=5", request.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("DAB Music search returned {Status}", (int)resp.StatusCode);
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(request.Token);
            return ParseSearchTracks(body, query.Artist, query.Title, query.ExpectedDuration);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("DAB Music search timed out for '{Query}'", searchText);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DAB Music search failed for '{Query}'", searchText);
            return null;
        }
    }

    /// <summary>
    /// Parses the /api/search response into ranked candidates. Track ids
    /// arrive as string or number; the source URL carries the id so the
    /// download step can map straight back to /api/stream.
    /// </summary>
    internal static DownloaderSearchHit? ParseSearchTracks(
        string json, string artist, string fallbackTitle, TimeSpan? expectedDuration = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tracks", out var tracks) ||
                tracks.ValueKind != JsonValueKind.Array || tracks.GetArrayLength() == 0)
                return null;

            var candidates = new List<DownloaderSearchHit>();
            foreach (var track in tracks.EnumerateArray())
            {
                if (track.ValueKind != JsonValueKind.Object) continue;

                var id = GetId(track);
                if (string.IsNullOrEmpty(id)) continue;

                var hitTitle = GetString(track, "title") ?? fallbackTitle;
                var hitArtist = GetString(track, "artist") ?? artist;
                var duration = track.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number
                    ? TimeSpan.FromSeconds(d.GetInt32())
                    : (TimeSpan?)null;

                candidates.Add(new DownloaderSearchHit(
                    Title: hitTitle,
                    Artist: hitArtist,
                    SourceUrl: $"{BaseUrl}/api/stream?trackId={Uri.EscapeDataString(id!)}",
                    Duration: duration,
                    MatchConfidence: FuzzyMatcher.Confidence(
                        artist, fallbackTitle, hitArtist: hitArtist, hitTitle: hitTitle)));
            }

            return SearchRanker.Rank(artist, fallbackTitle, expectedDuration, candidates);
        }
        catch (JsonException)
        {
            return null;
        }
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
            var trackId = ExtractTrackId(sourceUrl);
            if (string.IsNullOrEmpty(trackId))
                return new DownloaderDownloadResult(false, null, "not a DAB Music stream URL: " + sourceUrl);

            quality ??= DownloadQuality.Any;
            var qualityParam = TryMapQuality(quality);
            if (qualityParam is null)
                return new DownloaderDownloadResult(false, null,
                    "no DAB Music tier fits this quality band; skipping instead of wasting a download");

            var streamUrl = await ResolveStreamUrlAsync(trackId!, qualityParam, ct);
            if (streamUrl is null)
                return new DownloaderDownloadResult(false, null,
                    "DAB Music did not return a stream URL for track " + trackId);

            Directory.CreateDirectory(outputDirectory);
            var ext = ExtensionFromUrl(streamUrl) ?? DefaultExtension(qualityParam);
            var filePath = Path.Combine(outputDirectory,
                $"dab_{trackId}_{qualityParam}_{(melodyId is { Length: > 0 } ? melodyId : Guid.NewGuid().ToString("N")[..8])}{ext}");

            if (!await DownloadFileAsync(streamUrl, filePath, ct))
                return new DownloaderDownloadResult(false, null, "DAB Music stream download failed");

            // An error page saved under an audio name must never pass:
            // sniff the real bytes before accepting.
            var container = AudioSignature.EnsureExtension(filePath);
            if (container is null)
            {
                FileCleanup.DeleteQuietly(filePath);
                return new DownloaderDownloadResult(false, null, "DAB Music returned a file that is not audio");
            }

            YtDlpDownloader.TagDownloadedFile(container, expectedTitle: null);
            return new DownloaderDownloadResult(true, container, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DAB Music download failed for {Url}", sourceUrl);
            return new DownloaderDownloadResult(false, null, ex.Message);
        }
    }

    /// <summary>Resolves the media URL through /api/stream; null on any failure.</summary>
    private async Task<string?> ResolveStreamUrlAsync(string trackId, string qualityParam, CancellationToken ct)
    {
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
            request.CancelAfter(StreamRequestTimeout);
            using var resp = await _http.GetAsync(
                $"{BaseUrl}/api/stream?trackId={Uri.EscapeDataString(trackId)}&quality={qualityParam}", request.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("DAB Music stream request returned {Status}", (int)resp.StatusCode);
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(request.Token);
            return ParseStreamUrl(body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("DAB Music stream request timed out for track {TrackId}", trackId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DAB Music stream request failed for track {TrackId}", trackId);
            return null;
        }
    }

    /// <summary>
    /// Pulls the media URL out of the stream response. The public spec
    /// calls the field streamUrl while the generated clients read url,
    /// so both (and a nested data.url) are accepted.
    /// </summary>
    internal static string? ParseStreamUrl(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var direct = GetString(root, "url") ?? GetString(root, "streamUrl") ?? GetString(root, "stream_url");
            if (!string.IsNullOrEmpty(direct)) return direct;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                return GetString(data, "url") ?? GetString(data, "streamUrl");
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Streams the media URL into filePath. A half-written file is
    /// deleted before the failure propagates.
    /// </summary>
    private async Task<bool> DownloadFileAsync(string streamUrl, string filePath, CancellationToken ct)
    {
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
        request.CancelAfter(FileRequestTimeout);
        try
        {
            using var resp = await _http.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, request.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("DAB Music file download returned {Status}", (int)resp.StatusCode);
                return false;
            }

            await using (var src = await resp.Content.ReadAsStreamAsync(request.Token))
            await using (var dst = File.Create(filePath))
            {
                await src.CopyToAsync(dst, 81920, request.Token);
            }

            if (new FileInfo(filePath).Length == 0)
            {
                FileCleanup.DeleteQuietly(filePath);
                return false;
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            FileCleanup.DeleteQuietly(filePath);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "DAB Music file download failed from {Url}", streamUrl);
            FileCleanup.DeleteQuietly(filePath);
            return false;
        }
    }

    // ── Mapping helpers ──────────────────────────────────────────────────

    /// <summary>
    /// The DAB tier that fits the requested band, or null when none does
    /// (a Space Saver cap, say: the smallest tier is MP3 320). Null lets
    /// the caller fail fast instead of downloading bytes the
    /// post-download gate would delete.
    /// </summary>
    internal static string? TryMapQuality(DownloadQuality quality)
    {
        if (quality.Format == AudioFormat.Flac)
            return QualityHiRes;
        if (quality.Format == AudioFormat.Mp3)
            return QualityMp3;
        if (quality.Format == AudioFormat.Aac)
            return null; // DAB serves no AAC tier; do not claim what it cannot produce

        var min = quality.MinKbps;
        var max = quality.MaxKbps;
        if (max is null)
            return QualityHiRes;
        if (min is > 320)
        {
            // A lossless floor: only the FLAC tiers can carry it.
            if (max >= 4000) return QualityHiRes;
            if (max >= 900) return QualityCd;
            return null;
        }
        return max >= 320 ? QualityMp3 : null;
    }

    /// <summary>Track id from a /api/stream URL (query parameter or last segment).</summary>
    internal static string? ExtractTrackId(string sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl)) return null;
        try
        {
            var uri = new Uri(sourceUrl, UriKind.Absolute);
            var query = uri.Query.TrimStart('?');
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length == 2 && parts[0].Equals("trackId", StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(parts[1]);
            }
            var last = uri.AbsolutePath.TrimEnd('/').Split('/')[^1];
            return string.IsNullOrEmpty(last) ? null : Uri.UnescapeDataString(last);
        }
        catch
        {
            // Bare id instead of a URL.
            return sourceUrl.Trim();
        }
    }

    private static string? ExtensionFromUrl(string url)
    {
        try
        {
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            if (!string.IsNullOrEmpty(ext))
                return ext.ToLowerInvariant();
        }
        catch { /* non-absolute URL: default below */ }
        return null;
    }

    private static string DefaultExtension(string qualityParam)
        => qualityParam == QualityMp3 ? ".mp3" : ".flac";

    private static string? GetId(JsonElement track)
    {
        if (!track.TryGetProperty("id", out var id)) return null;
        return id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null,
        };
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
