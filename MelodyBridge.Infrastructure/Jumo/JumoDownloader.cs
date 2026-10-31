using System.Globalization;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.SearchProviders;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    public const string RegionKey = JumoSession.RegionKey;
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

    public DownloaderSearchInfo SearchInfo =>
        DownloaderSearchInfo.ViaProvider(SearchData.Text | SearchData.Isrc,
            "Jumo catalogue search, ISRC first");

    public string IconName => "globe";
    public string? GuideUrl => "https://docs.melodybridge.app/jumo";
    public IReadOnlyList<string> Notes => new[] { "no account", "community front-end" };

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
    public string? LastUnavailableReason => _session.LastUnavailableReason;

    private readonly JumoSession _session;
    private readonly IPluginConfig _config;
    private readonly ILogger<JumoDownloader> _logger;

    /// <summary>How many segments are fetched at once; the CDN handles a handful fine.</summary>
    private const int SegmentConcurrency = 4;

    /// <summary>Retries per segment before the whole download fails.</summary>
    private const int SegmentAttempts = 3;

    public JumoDownloader(JumoSession session, IPluginConfig config, ILogger<JumoDownloader> logger)
    {
        _session = session;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Convenience constructor for tests and one-off hosts: creates a
    /// session from the same client and config. The app wires one shared
    /// session so the provider and the downloader see the same state.
    /// </summary>
    public JumoDownloader(JumoClient client, IPluginConfig config, ILogger<JumoDownloader> logger)
        : this(new JumoSession(client, config, NullLogger<JumoSession>.Instance), config, logger)
    {
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => _session.IsAvailableAsync(ct);

    // ── Download ─────────────────────────────────────────────────────────

    public async Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl, string outputDirectory, string? melodyId,
        DownloadQuality? quality = null, CancellationToken ct = default)
    {
        if (!JumoSourceId.TryParse(sourceUrl, out var albumId, out var trackId))
            return new DownloaderDownloadResult(false, null, "not a Jumo source identifier: " + sourceUrl);

        var format = await FormatForQualityAsync(quality, ct);
        string? filePath = null;
        try
        {
            Directory.CreateDirectory(outputDirectory);
            var region = await _session.EffectiveRegionAsync(ct);

            var fetchUrl = await TrackFetchUrlAsync(albumId, trackId, format, region, ct);
            if (fetchUrl is null)
                return new DownloaderDownloadResult(false, null,
                    $"Jumo has no format {format} streams for track {trackId} in region {region}");

            var descriptor = await _session.Client.ApiGetAsync(fetchUrl, ct);
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
            var effectiveFormat = JumoJson.GetId(qb, "effective_format") ?? JumoJson.GetId(qb, "format_id") ?? "";
            var contentKey = JumoJson.GetString(qb, "content_key") ?? "";
            var urlTemplate = JumoJson.GetString(qb, "url_template") ?? "";
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
        return _session.Client.DownloadBytesAsync(url, ct);
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

            var response = await _session.Client.ApiGetAsync(url, ct);
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
                    if (JumoJson.GetId(item, "id") != trackId) continue;
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

    // ── Parsing helpers ──────────────────────────────────────────────────

    /// <summary>Keeps file names safe when an id carries unusual characters.</summary>
    private static string Sanitize(string value)
        => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
