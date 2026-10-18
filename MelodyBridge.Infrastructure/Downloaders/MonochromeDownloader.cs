using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// Monochrome downloader plugin: the official Monochrome lossless API
/// (tracks.monochrome.st). No account needed; the service sources the
/// audio itself and streams FLAC back over a plain GET. The host is
/// configurable (community mirror or a local proxy) through the plugin's
/// base_url setting.
///
/// API shape (their "tracks" provider, live-probed):
///   /search/tracks?q={query}&limit={n} → {"tracks":[track objects]}
///   /track/{id}                        → audio bytes (FLAC)
///   /health                            → 200 when the API is up
///
/// Track objects carry: id/trackId (snowflake), title, artistNames[],
/// duration (milliseconds), isrc and playable.
/// </summary>
public class MonochromeDownloader : IDownloader
{
    public const string DefaultBaseUrl = "https://tracks.monochrome.st";
    private const string BaseUrlKey = "base_url";

    // The official API serves one lossless tier: asking for FLAC gets a
    // real FLAC (usually ~900-1100 kbps). There is no lossy tier and no
    // quality parameter, so no AAC/MP3 deliverables are declared.
    public static readonly PluginCapabilities Caps = new(
    [
        new QualityDeliverable(AudioFormat.Flac, Lossless: true, 900, 10000),
    ]);
    public PluginCapabilities Capabilities => Caps;

    public string Id => "monochrome";
    public string Name => "Monochrome (lossless)";
    public string Description => "Official Monochrome lossless API: FLAC streams, no account needed";

    public IReadOnlyList<PluginConfigField> ConfigFields =>
    [
        new(BaseUrlKey, "API base URL", DefaultBaseUrl,
            "Override the Monochrome API host, for example a community mirror. Empty uses the official host.",
            Advanced: true),
    ];

    private readonly HttpClient _http;
    private readonly IPluginConfig _config;
    private readonly ILogger<MonochromeDownloader> _logger;

    // Availability probe cache: the waterfall asks per track and the
    // Plugins page re-checks on a timer; the TTL keeps /health from being
    // hammered.
    private readonly AvailabilityCache _availability = new();

    /// <summary>Per-request deadlines so one hanging API call cannot stall the waterfall.</summary>
    private static readonly TimeSpan SearchRequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StreamRequestTimeout = TimeSpan.FromMinutes(8);
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(5);

    public MonochromeDownloader(HttpClient http, IPluginConfig config, ILogger<MonochromeDownloader> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    /// <summary>The configured API host, falling back to the official one.</summary>
    private async Task<string> BaseUrlAsync(CancellationToken ct)
    {
        var configured = await _config.GetAsync(Id, BaseUrlKey, ct);
        if (string.IsNullOrWhiteSpace(configured)) return DefaultBaseUrl;
        return configured.Trim().TrimEnd('/');
    }

    // ── Availability ─────────────────────────────────────────────────────

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_availability.TryGet(out var cached))
            return cached;

        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(HealthTimeout);
            var url = await BaseUrlAsync(ct) + "/health";
            using var resp = await _http.GetAsync(url, probe.Token);
            var available = resp.IsSuccessStatusCode;
            if (!available)
                _logger.LogDebug("Monochrome health probe returned {Status}", (int)resp.StatusCode);
            _availability.Store(available);
            return available;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Monochrome health probe failed");
            _availability.Store(false);
            return false;
        }
    }

    // ── Search ───────────────────────────────────────────────────────────

    public async Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
    {
        // Cleaned terms: the API's index is literal, so "(Original Mix)"
        // suffixes and featuring tags reduce matches before the request is
        // even sent.
        var searchText = query.SearchText;
        if (searchText.Length == 0) return null;

        var baseUrl = await BaseUrlAsync(ct);
        var url = baseUrl + "/search/tracks?q=" + Uri.EscapeDataString(searchText) + "&limit=10";
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
            request.CancelAfter(SearchRequestTimeout);

            using var resp = await _http.GetAsync(url, request.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Monochrome search returned {Status}", (int)resp.StatusCode);
                return null;
            }

            var body = await resp.Content.ReadAsStringAsync(request.Token);
            return ParseSearchTracks(body, baseUrl, query.Artist, query.Title,
                query.ExpectedDuration, query.Isrc);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("Monochrome search timed out");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Monochrome search '{Query}' failed", searchText);
            return null;
        }
    }

    /// <summary>
    /// Parses a /search/tracks response ({"tracks":[…]}) into the best hit:
    /// every item is ranked by title/artist confidence plus duration
    /// proximity. An exact ISRC match (when the query carries one) narrows
    /// the pool to the recording itself and is accepted on that code even
    /// when the catalogue spells the title differently, so a noisy title
    /// cannot pick a different master. Null when there are no usable items.
    /// </summary>
    internal static DownloaderSearchHit? ParseSearchTracks(
        string json, string baseUrl, string artist, string fallbackTitle,
        TimeSpan? expectedDuration = null, string? expectedIsrc = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tracks", out var tracks) ||
                tracks.ValueKind != JsonValueKind.Array ||
                tracks.GetArrayLength() == 0)
                return null;

            // Rank every item instead of trusting the API's first row:
            // the list routinely buries the real match under remixes.
            var candidates = new List<(DownloaderSearchHit Hit, string? Isrc)>();
            foreach (var item in tracks.EnumerateArray())
            {
                // One malformed entry must not poison the response: skip
                // the item, keep the loop.
                if (item.ValueKind != JsonValueKind.Object) continue;

                // Unplayable entries (takedowns, region blocks) would only
                // fail later, after a wasted download attempt.
                if (item.TryGetProperty("playable", out var playable) &&
                    playable.ValueKind == JsonValueKind.False)
                    continue;

                var id = GetString(item, "id") ?? GetString(item, "trackId");
                if (string.IsNullOrEmpty(id)) continue;

                var hitTitle = GetString(item, "title") ?? fallbackTitle;
                var hitArtist = GetPrimaryArtist(item);

                TimeSpan? duration = item.TryGetProperty("duration", out var d) &&
                                     d.ValueKind == JsonValueKind.Number
                    ? TimeSpan.FromMilliseconds(d.GetDouble())
                    : null;

                var hit = new DownloaderSearchHit(
                    Title: hitTitle,
                    Artist: hitArtist,
                    SourceUrl: $"{baseUrl}/track/{id}",
                    Duration: duration,
                    MatchConfidence: FuzzyMatcher.Confidence(
                        artist, fallbackTitle, hitArtist: hitArtist, hitTitle: hitTitle));
                candidates.Add((hit, GetString(item, "isrc")));
            }

            if (candidates.Count == 0) return null;

            // An exact ISRC is the recording itself, not a guess: when the
            // query carries one and any candidate matches, the others are
            // a different master even when their titles look closer.
            var isrcPool = candidates.Where(c => IsrcEquals(expectedIsrc, c.Isrc)).ToList();
            if (isrcPool.Count > 0)
                return SearchRanker.RankExactRecording(artist, fallbackTitle,
                    expectedDuration, isrcPool.Select(c => c.Hit));

            return SearchRanker.Rank(artist, fallbackTitle, expectedDuration,
                candidates.Select(c => c.Hit));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>True when both ISRCs are present and equal, ignoring case and padding.</summary>
    internal static bool IsrcEquals(string? expected, string? actual)
        => !string.IsNullOrWhiteSpace(expected) && !string.IsNullOrWhiteSpace(actual)
           && string.Equals(expected.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);

    // ── Download ─────────────────────────────────────────────────────────

    public async Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl,
        string outputDirectory,
        string? melodyId,
        DownloadQuality? quality = null,
        CancellationToken ct = default)
    {
        if (!TryExtractTrackId(sourceUrl, out var trackId))
            return new DownloaderDownloadResult(false, null, "not a Monochrome track URL or id: " + sourceUrl);

        Directory.CreateDirectory(outputDirectory);

        // MelodyId in the name (when present) keeps retries and concurrent
        // downloads of the same track distinct files instead of a shared
        // path two writers clobber.
        var filePath = Path.Combine(outputDirectory,
            $"monochrome_{trackId}_{(melodyId is { Length: > 0 } ? melodyId : Guid.NewGuid().ToString("N")[..8])}.flac");

        try
        {
            var baseUrl = await BaseUrlAsync(ct);
            using var request = CancellationTokenSource.CreateLinkedTokenSource(ct);
            request.CancelAfter(StreamRequestTimeout);

            using var resp = await _http.GetAsync($"{baseUrl}/track/{trackId}",
                HttpCompletionOption.ResponseHeadersRead, request.Token);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogDebug("Monochrome track {TrackId} returned {Status}", trackId, (int)resp.StatusCode);
                return new DownloaderDownloadResult(false, null,
                    $"Monochrome track {trackId} returned HTTP {(int)resp.StatusCode}");
            }

            await using (var src = await resp.Content.ReadAsStreamAsync(request.Token))
            await using (var dst = File.Create(filePath))
            {
                await src.CopyToAsync(dst, 81920, request.Token);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            FileCleanup.DeleteQuietly(filePath);
            throw; // caller cancellation: the outer handler cleans up
        }
        catch (Exception ex)
        {
            FileCleanup.DeleteQuietly(filePath);
            _logger.LogDebug(ex, "Monochrome download for track {TrackId} failed", trackId);
            return new DownloaderDownloadResult(false, null, ex.Message);
        }

        if (new FileInfo(filePath).Length == 0)
        {
            // A zero-byte rip is a failed rip: never leave it behind.
            FileCleanup.DeleteQuietly(filePath);
            return new DownloaderDownloadResult(false, null, "Monochrome returned an empty stream");
        }

        // An error page saved under an audio name must never pass: sniff
        // the real bytes before accepting, and fix the extension to match.
        var container = AudioSignature.EnsureExtension(filePath);
        if (container is null)
        {
            _logger.LogDebug("Monochrome stream for track {TrackId} is not audio", trackId);
            FileCleanup.DeleteQuietly(filePath);
            return new DownloaderDownloadResult(false, null, "Monochrome stream is not audio");
        }

        YtDlpDownloader.TagDownloadedFile(container, expectedTitle: null);
        return new DownloaderDownloadResult(true, container, null);
    }

    /// <summary>
    /// Extracts the track id from a Monochrome stream URL
    /// ({base}/track/{id}) or a bare snowflake id. Ids stay strings:
    /// 19-digit snowflakes can overflow a long.
    /// </summary>
    internal static bool TryExtractTrackId(string sourceUrl, out string trackId)
    {
        trackId = "";
        if (string.IsNullOrWhiteSpace(sourceUrl)) return false;

        var candidate = sourceUrl.Trim().TrimEnd('/');

        // Bare id
        if (IsSnowflake(candidate))
        {
            trackId = candidate;
            return true;
        }

        // Last path segment of .../track/{id}; a URL must actually be a
        // track path, so a random numeric URL tail is not mistaken for one.
        var lastSegment = candidate.Split('/')[^1];
        if (!IsSnowflake(lastSegment)) return false;
        if (candidate.Contains("://", StringComparison.Ordinal) &&
            !candidate.Contains("/track/", StringComparison.OrdinalIgnoreCase))
            return false;
        trackId = lastSegment;
        return true;
    }

    /// <summary>A numeric id of at most 20 digits (the API's snowflake shape).</summary>
    private static bool IsSnowflake(string value)
        => value.Length is > 0 and <= 20 && value.All(char.IsAsciiDigit);

    // ── Parsing helpers ──────────────────────────────────────────────────

    /// <summary>Primary artist: artistNames[0], artists[0].name or artist.name.</summary>
    private static string? GetPrimaryArtist(JsonElement item)
    {
        if (item.TryGetProperty("artistNames", out var names) &&
            names.ValueKind == JsonValueKind.Array)
        {
            foreach (var name in names.EnumerateArray())
                if (name.ValueKind == JsonValueKind.String)
                    return name.GetString();
        }

        if (item.TryGetProperty("artists", out var artists) &&
            artists.ValueKind == JsonValueKind.Array && artists.GetArrayLength() > 0)
            return GetString(artists[0], "name");

        if (item.TryGetProperty("artist", out var artist))
        {
            if (artist.ValueKind == JsonValueKind.String)
                return artist.GetString();
            var name = GetString(artist, "name");
            if (!string.IsNullOrEmpty(name)) return name;
        }

        return null;
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
