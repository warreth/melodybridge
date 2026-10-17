using System.Globalization;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Jumo;

/// <summary>
/// Downloader plugin for Jumo-DL (jumo-dl.pages.dev), a community Qobuz
/// front-end that needs no account. Search and stream descriptors come
/// from its guarded API (see <see cref="JumoClient"/>); the audio itself
/// is fetched directly from the signed Qobuz CDN URLs the descriptor
/// carries and decrypted locally (see <see cref="JumoContainer"/>).
///
/// Quality maps to Qobuz formats: 27 (hi-res FLAC), 6 (16-bit CD FLAC)
/// and 5 (MP3 320). The requested quality decides the format; the
/// database's effective format can be lower when the release has no
/// higher tier, which still delivers the requested lossless container.
/// </summary>
public class JumoDownloader : IDownloader
{
    public const string RegionKey = "region";
    public const string FlacQualityKey = "flac_quality";
    public const string DefaultFlacQuality = "27";

    // Lossless streams arrive as 16/44.1 or hi-res FLAC; MP3 requests use
    // the 320 tier. The plugin controls the format per request, so both
    // deliverables are marked controlled.
    public static readonly PluginCapabilities Caps = new(
    [
        new QualityDeliverable(AudioFormat.Flac, Lossless: true, 900, 10000),
        new QualityDeliverable(AudioFormat.Mp3, Lossless: false, 320, 320),
    ]);
    public PluginCapabilities Capabilities => Caps;

    public string Id => "jumo";
    public string Name => "Jumo";
    public string Description =>
        "Qobuz rips via jumo-dl.pages.dev, no account needed (lossless, CD and MP3 320)";

    public IReadOnlyList<PluginConfigField> ConfigFields =>
    [
        new(RegionKey, "Region", null,
            "Two-letter storefront code (US, GB, FR, DE, CA, JP...). Empty uses the service's current default."),
        new(FlacQualityKey, "FLAC quality", null,
            "How high lossless goes; MP3 requests always use the 320 tier.",
            Options:
            [
                new("6", "16-bit / 44.1 kHz (CD quality)"),
                new("7", "24-bit up to 96 kHz"),
                new("27", "24-bit above 96 kHz"),
            ]),
    ];

    /// <inheritdoc />
    public string? LastUnavailableReason { get; private set; }

    private readonly JumoClient _client;
    private readonly IPluginConfig _config;
    private readonly ILogger<JumoDownloader> _logger;
    private readonly AvailabilityCache _availability = new();

    /// <summary>Region lookup cache: the backend's list changes slowly.</summary>
    private readonly SemaphoreSlim _regionGate = new(1, 1);
    private DateTimeOffset _regionsFetchedAt = DateTimeOffset.MinValue;
    private string? _defaultRegion;
    private List<string> _availableRegions = [];

    /// <summary>How many segments are fetched at once; the CDN handles a handful fine.</summary>
    private const int SegmentConcurrency = 4;

    /// <summary>Retries per segment before the whole download fails.</summary>
    private const int SegmentAttempts = 3;

    /// <summary>
    /// Search timeout: inclusive of the client's own throttle backoff,
    /// which can span half a minute during a limiter burst. Still well
    /// inside the plugin budget.
    /// </summary>
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RegionsTtl = TimeSpan.FromMinutes(10);

    public JumoDownloader(JumoClient client, IPluginConfig config, ILogger<JumoDownloader> logger)
    {
        _client = client;
        _config = config;
        _logger = logger;
    }

    // ── Availability ─────────────────────────────────────────────────────

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_availability.TryGet(out var cached)) return cached;

        try
        {
            var regions = await RegionsAsync(ct);
            var available = regions is not null;
            LastUnavailableReason = available
                ? null
                : "The service's region endpoint did not answer";
            _availability.Store(available);
            return available;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LastUnavailableReason = "The service could not be reached: " + ex.Message;
            _logger.LogDebug(ex, "Jumo availability probe failed");
            _availability.Store(false);
            return false;
        }
    }

    // ── Search ───────────────────────────────────────────────────────────

    public async Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
    {
        var searchText = query.SearchText;
        if (searchText.Length == 0) return null;

        try
        {
            var region = await EffectiveRegionAsync(ct);

            // A small page keeps the request light; the service throttles
            // heavy API use (live probing saw 503s for a limit of 30 while
            // smaller pages passed in the same window).
            var url = JumoClient.Origin + JumoClient.SearchPath
                + "?query=" + Uri.EscapeDataString(searchText)
                + "&offset=0&limit=10&region=" + Uri.EscapeDataString(region) + "&view=ui";

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SearchTimeout);
            var response = await _client.ApiGetAsync(url, timeout.Token);
            if (response.Status != 200)
            {
                _logger.LogDebug("Jumo search returned {Status}", response.Status);

                // A refused or throttled search means the service is not
                // usable right now. Report that as unavailable (the short
                // negative TTL makes the waterfall skip the plugin for a
                // while) instead of letting every track of a run fail as a
                // bare "no source" miss.
                if (response.Status is 403 or 429 or 503)
                {
                    LastUnavailableReason = response.Status is 403
                        ? "The service refused the search (HTTP 403); it guards its API against non-browser clients"
                        : $"The service is throttling requests (HTTP {response.Status})";
                    _availability.Store(false);
                }
                return null;
            }

            return ParseSearchTracks(response.Body, query.Artist, query.Title,
                query.ExpectedDuration, query.Isrc);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("Jumo search timed out");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Jumo search '{Query}' failed", searchText);
            return null;
        }
    }

    /// <summary>
    /// Ranks the search response's track items with the shared ranker and
    /// maps them onto source identifiers. The album id travels along so
    /// the download can refetch fresh descriptor URLs later.
    /// </summary>
    internal static DownloaderSearchHit? ParseSearchTracks(
        string json, string artist, string fallbackTitle,
        TimeSpan? expectedDuration = null, string? expectedIsrc = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("tracks", out var tracks) ||
                !tracks.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return null;

            var candidates = new List<(DownloaderSearchHit Hit, string? Isrc, bool Hires)>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var trackId = GetId(item, "id");
                var albumId = item.TryGetProperty("album", out var album) ? GetId(album, "id") : null;
                if (trackId is null || albumId is null) continue;

                var hitTitle = GetString(item, "title") ?? fallbackTitle;
                var hitArtist = GetPrimaryArtist(item);

                TimeSpan? duration = item.TryGetProperty("duration", out var d) &&
                                     d.ValueKind == JsonValueKind.Number
                    ? TimeSpan.FromSeconds(d.GetDouble())
                    : null;

                var hires = item.TryGetProperty("hires", out var h) && h.ValueKind == JsonValueKind.True;
                var hit = new DownloaderSearchHit(
                    Title: hitTitle,
                    Artist: hitArtist,
                    SourceUrl: SourceUrl(albumId, trackId),
                    Duration: duration,
                    MatchConfidence: FuzzyMatcher.Confidence(
                        artist, fallbackTitle, hitArtist: hitArtist, hitTitle: hitTitle));
                candidates.Add((hit, GetString(item, "isrc"), hires));
            }

            if (candidates.Count == 0) return null;

            // An exact ISRC is the recording itself, not a guess.
            var isrcPool = candidates
                .Where(c => IsrcEquals(expectedIsrc, c.Isrc))
                .ToList();
            var pool = isrcPool.Count > 0 ? isrcPool : candidates;

            return SearchRanker.Rank(artist, fallbackTitle, expectedDuration,
                pool.Select(c => c.Hit),
                tieBreaker: hit => pool.First(p => ReferenceEquals(p.Hit, hit)).Hires ? 1 : 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Source identifier: jumo:{albumId}:{trackId}.</summary>
    internal static string SourceUrl(string albumId, string trackId) => $"jumo:{albumId}:{trackId}";

    /// <summary>Splits a source identifier back into album and track id.</summary>
    internal static bool TryParseSourceUrl(string? sourceUrl, out string albumId, out string trackId)
    {
        albumId = "";
        trackId = "";
        if (string.IsNullOrWhiteSpace(sourceUrl)) return false;
        var parts = sourceUrl.Trim().Split(':');
        if (parts.Length != 3 || parts[0] != "jumo") return false;
        if (parts[1].Length == 0 || parts[2].Length == 0) return false;
        albumId = parts[1];
        trackId = parts[2];
        return true;
    }

    /// <summary>True when both ISRCs are present and equal, ignoring case and padding.</summary>
    internal static bool IsrcEquals(string? expected, string? actual)
        => !string.IsNullOrWhiteSpace(expected) && !string.IsNullOrWhiteSpace(actual)
           && string.Equals(expected.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase);

    // ── Download ─────────────────────────────────────────────────────────

    public async Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl, string outputDirectory, string? melodyId,
        DownloadQuality? quality = null, CancellationToken ct = default)
    {
        if (!TryParseSourceUrl(sourceUrl, out var albumId, out var trackId))
            return new DownloaderDownloadResult(false, null, "not a Jumo source identifier: " + sourceUrl);

        var format = await FormatForQualityAsync(quality, ct);
        string? filePath = null;
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var region = await EffectiveRegionAsync(ct);

            var fetchUrl = await TrackFetchUrlAsync(albumId, trackId, format, region, ct);
            if (fetchUrl is null)
                return new DownloaderDownloadResult(false, null,
                    $"Jumo has no format {format} streams for track {trackId} in region {region}");

            var descriptor = await _client.ApiGetAsync(fetchUrl, ct);
            if (descriptor.Status != 200)
                return new DownloaderDownloadResult(false, null,
                    $"Jumo descriptor for track {trackId} returned HTTP {descriptor.Status}");

            var manifest = ParseManifest(descriptor.Body)
                ?? throw new InvalidDataException("Jumo descriptor did not contain a stream manifest");
            if (manifest.Preview)
                return new DownloaderDownloadResult(false, null,
                    $"Jumo only offers a preview stream for track {trackId} in region {region}");

            var extension = manifest.EffectiveFormat == "5" ? "mp3" : "flac";
            var suffix = melodyId is { Length: > 0 } ? melodyId : Guid.NewGuid().ToString("N")[..8];
            filePath = Path.Combine(outputDirectory, $"jumo_{Sanitize(trackId)}_{Sanitize(suffix)}.{extension}");
            await FetchAndWriteAsync(manifest, filePath, ct);

            if (new FileInfo(filePath).Length == 0)
            {
                FileCleanup.DeleteQuietly(filePath);
                return new DownloaderDownloadResult(false, null, "Jumo produced an empty stream");
            }

            // The bytes, never the extension, decide what this file is.
            var container = AudioSignature.EnsureExtension(filePath);
            if (container is null)
            {
                FileCleanup.DeleteQuietly(filePath);
                return new DownloaderDownloadResult(false, null, "Jumo produced bytes that are not audio");
            }
            return new DownloaderDownloadResult(true, container, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            if (filePath is not null) FileCleanup.DeleteQuietly(filePath);
            throw;
        }
        catch (Exception ex)
        {
            if (filePath is not null) FileCleanup.DeleteQuietly(filePath);
            _logger.LogDebug(ex, "Jumo download for track {TrackId} failed", trackId);
            return new DownloaderDownloadResult(false, null, ex.Message);
        }
    }

    /// <summary>A parsed descriptor manifest (the qb object).</summary>
    internal sealed record StreamManifest(
        string EffectiveFormat, string ContentKey, string UrlTemplate,
        int SegmentCount, bool Preview);

    internal static StreamManifest? ParseManifest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("qb", out var qb) || qb.ValueKind != JsonValueKind.Object)
                return null;

            var preview = qb.TryGetProperty("preview", out var p) && p.ValueKind == JsonValueKind.True;
            var effectiveFormat = GetId(qb, "effective_format") ?? GetId(qb, "format_id") ?? "";
            var contentKey = GetString(qb, "content_key") ?? "";
            var urlTemplate = GetString(qb, "url_template") ?? "";
            var segments = qb.TryGetProperty("n_segments", out var n) && n.ValueKind == JsonValueKind.Number
                ? (int)n.GetDouble()
                : 0;
            if (contentKey.Length == 0 || urlTemplate.Length == 0 || segments <= 0)
                return null;
            return new StreamManifest(effectiveFormat, contentKey, urlTemplate, segments, preview);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Fetches every segment straight from the signed CDN template and
    /// assembles (decrypts) the stream. Each segment is validated against
    /// the init segment's table, so a truncated transfer is retried
    /// instead of producing a broken file.
    ///
    /// Segments are fetched in small batches and decrypted straight into
    /// the output file, so peak memory stays at one batch of segment bytes
    /// instead of the whole stream (a hi-res track is around 96 MB).
    /// </summary>
    private async Task FetchAndWriteAsync(StreamManifest manifest, string filePath, CancellationToken ct)
    {
        var init = await FetchSegmentAsync(manifest.UrlTemplate, 0, ct);
        var initInfo = JumoContainer.ParseInit(init);
        if (initInfo.Table.Count == 0)
            throw new InvalidDataException("Jumo init segment has an empty segment table");
        if (initInfo.Table.Count != manifest.SegmentCount)
            throw new InvalidDataException(
                $"Jumo segment table has {initInfo.Table.Count} rows, descriptor says {manifest.SegmentCount}");

        var contentKey = Convert.FromHexString(manifest.ContentKey);
        await using var output = File.Create(filePath);
        JumoContainer.WriteStreamHeader(initInfo, manifest.EffectiveFormat, output);

        var count = initInfo.Table.Count;
        for (var start = 0; start < count; start += SegmentConcurrency)
        {
            var batch = new byte[Math.Min(SegmentConcurrency, count - start)][];
            await Task.WhenAll(Enumerable.Range(0, batch.Length).Select(async offset =>
                batch[offset] = await FetchValidatedSegmentAsync(manifest, initInfo, start + offset, ct)));

            for (var i = 0; i < batch.Length; i++)
                JumoContainer.WriteSegment(initInfo, start + i, batch[i], contentKey, output);
        }
    }

    /// <summary>Fetches one media segment and checks it against the table row.</summary>
    private async Task<byte[]> FetchValidatedSegmentAsync(
        StreamManifest manifest, JumoContainer.InitInfo initInfo, int index, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var segment = await FetchSegmentAsync(manifest.UrlTemplate, index + 1, ct);
                if (initInfo.Table.Count > index)
                {
                    var layout = JumoContainer.ParseSegment(segment);
                    var mdatBytes = layout.MdatEnd - layout.DataStart;
                    if (mdatBytes != initInfo.Table[index].ByteLength)
                        throw new InvalidDataException(
                            $"truncated segment ({mdatBytes} vs {initInfo.Table[index].ByteLength} bytes)");
                }
                return segment;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < SegmentAttempts)
            {
                _logger.LogDebug(ex, "Jumo segment {Index} attempt {Attempt} failed; retrying", index + 1, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt), ct);
            }
        }
    }

    /// <summary>Downloads one segment from the CDN template.</summary>
    private Task<byte[]> FetchSegmentAsync(string urlTemplate, int index, CancellationToken ct)
    {
        var url = urlTemplate.Replace("$SEGMENT$", index.ToString(CultureInfo.InvariantCulture));
        return _client.DownloadBytesAsync(url, ct);
    }

    /// <summary>
    /// Looks up the track inside its album and returns the descriptor URL
    /// for the requested format. Descriptor URLs are signed and short
    /// lived, so they are fetched fresh for every download. Long albums
    /// (box sets, compilations) page through the track list until the
    /// track is found.
    /// </summary>
    private async Task<string?> TrackFetchUrlAsync(
        string albumId, string trackId, string format, string region, CancellationToken ct)
    {
        const int pageSize = 50;
        const int maxPages = 4;

        var offset = 0;
        for (var page = 0; page < maxPages; page++)
        {
            var url = JumoClient.Origin + JumoClient.AlbumPath
                + "?album_id=" + Uri.EscapeDataString(albumId)
                + "&region=" + Uri.EscapeDataString(region)
                + "&track_offset=" + offset + "&track_limit=" + pageSize
                + "&get_track_urls=true&format_id=" + Uri.EscapeDataString(format);

            var response = await _client.ApiGetAsync(url, ct);
            if (response.Status != 200)
            {
                _logger.LogDebug("Jumo album {AlbumId} returned {Status}", albumId, response.Status);
                return null;
            }

            try
            {
                using var doc = JsonDocument.Parse(response.Body);
                if (!doc.RootElement.TryGetProperty("tracks", out var tracks) ||
                    !tracks.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array)
                    return null;

                foreach (var item in items.EnumerateArray())
                {
                    if (GetId(item, "id") != trackId) continue;
                    if (!item.TryGetProperty("fetchUrls", out var fetchUrls) ||
                        !fetchUrls.TryGetProperty(format, out var formatUrl) ||
                        formatUrl.ValueKind != JsonValueKind.String)
                        return null;
                    return formatUrl.GetString();
                }

                var itemCount = items.GetArrayLength();
                if (itemCount == 0) return null;
                offset += itemCount;

                var total = tracks.TryGetProperty("total", out var totalValue) &&
                            totalValue.ValueKind == JsonValueKind.Number
                    ? (long)totalValue.GetDouble()
                    : offset;
                if (offset >= total) return null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>Requested quality to Qobuz format id.</summary>
    private async Task<string> FormatForQualityAsync(DownloadQuality? quality, CancellationToken ct)
    {
        if (quality?.Format == AudioFormat.Mp3) return "5";

        var configured = await _config.GetAsync(Id, FlacQualityKey, ct);
        return string.IsNullOrWhiteSpace(configured) ? DefaultFlacQuality : configured.Trim();
    }

    // ── Regions ──────────────────────────────────────────────────────────

    /// <summary>
    /// The region every request uses: the configured code when the service
    /// currently offers it, otherwise the service's own default.
    /// </summary>
    private async Task<string> EffectiveRegionAsync(CancellationToken ct)
    {
        var configured = (await _config.GetAsync(Id, RegionKey, ct))?.Trim().ToUpperInvariant();
        var regions = await RegionsAsync(ct);

        if (!string.IsNullOrEmpty(configured))
        {
            if (regions is null || regions.Value.Available.Contains(configured))
                return configured;
            _logger.LogInformation(
                "Jumo region {Configured} is not offered right now; using {Default}",
                configured, regions.Value.Default);
        }

        return regions?.Default ?? "CA";
    }

    /// <summary>Fetches the service's region list once per <see cref="RegionsTtl"/>.</summary>
    private async Task<(string Default, List<string> Available)?> RegionsAsync(CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _regionsFetchedAt < RegionsTtl && _defaultRegion is not null)
            return (_defaultRegion, _availableRegions);

        await _regionGate.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow - _regionsFetchedAt < RegionsTtl && _defaultRegion is not null)
                return (_defaultRegion, _availableRegions);

            var response = await _client.ApiGetAsync(JumoClient.Origin + JumoClient.RegionsPath, ct);
            if (response.Status != 200) return null;

            using var doc = JsonDocument.Parse(response.Body);
            if (!doc.RootElement.TryGetProperty("regions", out var regions) ||
                regions.ValueKind != JsonValueKind.Array)
                return null;

            var available = new List<string>();
            foreach (var region in regions.EnumerateArray())
            {
                if (region.TryGetProperty("available", out var flag) && flag.ValueKind == JsonValueKind.False)
                    continue;
                var code = GetId(region, "code");
                if (!string.IsNullOrEmpty(code)) available.Add(code);
            }

            var fallback = GetId(doc.RootElement, "default");
            _defaultRegion = !string.IsNullOrEmpty(fallback) && available.Contains(fallback)
                ? fallback
                : available.FirstOrDefault() ?? "CA";
            _availableRegions = available;
            _regionsFetchedAt = DateTimeOffset.UtcNow;
            return (_defaultRegion, _availableRegions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Jumo regions lookup failed");
            return null;
        }
        finally
        {
            _regionGate.Release();
        }
    }

    // ── Parsing helpers ──────────────────────────────────────────────────

    /// <summary>Primary artist: performer.name, artists[0].name or album.artist.name.</summary>
    private static string? GetPrimaryArtist(JsonElement item)
    {
        if (item.TryGetProperty("performer", out var performer))
        {
            var name = GetString(performer, "name");
            if (!string.IsNullOrEmpty(name)) return name;
        }

        if (item.TryGetProperty("artists", out var artists) &&
            artists.ValueKind == JsonValueKind.Array && artists.GetArrayLength() > 0)
        {
            var name = GetString(artists[0], "name");
            if (!string.IsNullOrEmpty(name)) return name;
        }

        if (item.TryGetProperty("artist", out var artist))
        {
            var name = GetString(artist, "name");
            if (!string.IsNullOrEmpty(name)) return name;
        }

        if (item.TryGetProperty("album", out var album) &&
            album.ValueKind == JsonValueKind.Object &&
            album.TryGetProperty("artist", out var albumArtist))
            return GetString(albumArtist, "name");

        return null;
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>An id that may arrive as a string or a number (track and album ids differ).</summary>
    private static string? GetId(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var v))
            return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetDouble().ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    /// <summary>Keeps file names safe when an id carries unusual characters.</summary>
    private static string Sanitize(string value)
        => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
