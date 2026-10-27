using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Rhythmax;

/// <summary>
/// Tidal through the public Rhythmax hifi API (tidal.odskyler.com), with
/// no Tidal account of the user's own. The source itself never searches:
/// the Tidal ISRC provider resolves a track URL, and this plugin only turns
/// that URL into a file. That keeps the search logic in one place for both
/// Tidal downloaders.
///
/// Quality follows Tidal's own three useful tiers: LOW (96 kbps AAC), HIGH
/// (320 kbps AAC) and LOSSLESS (16-bit FLAC). Hi-res is deliberately not
/// declared: the API can serve it, but this plugin asks for and verifies the
/// CD tier, which is what the manifest promises here.
/// </summary>
public class RhythmaxDownloader : IDownloader, IPluginAvailabilityRefresh
{
    public const string DefaultBaseUrl = RhythmaxClient.DefaultBaseUrl;
    private const string BaseUrlKey = "base_url";
    private const string CountryKey = "country_code";

    // Tidal offers exactly two AAC tiers, 96 and 320 kbps. Declaring a
    // single 96-320 band would claim the router can serve 128-160 kbps,
    // which no Tidal tier fits; two point bands keep the manifest honest.
    public static readonly PluginCapabilities Caps = new(
    [
        new QualityDeliverable(AudioFormat.Flac, Lossless: true, 900, 10000),
        new QualityDeliverable(AudioFormat.Aac, Lossless: false, 320, 320),
        new QualityDeliverable(AudioFormat.Aac, Lossless: false, 96, 96),
    ]);

    public string Id => "rhythmax";
    public string Name => "Tidal (Rhythmax)";
    public string Description =>
        "Tidal lossless and AAC through the public Rhythmax API, no Tidal account needed";

    public PluginCapabilities Capabilities => Caps;

    /// <summary>The Tidal searcher supplies the URL; this source only downloads it.</summary>
    public DownloaderSearchInfo SearchInfo =>
        DownloaderSearchInfo.ViaProvider(SearchData.Isrc | SearchData.Text,
            "Uses the Tidal ISRC searcher; no source-side title search");

    public string? LastUnavailableReason { get; private set; }

    public IReadOnlyList<PluginConfigField> ConfigFields =>
    [
        new(CountryKey, "Tidal country", "US",
            "Two letters. The ISRC search and the catalogue follow this region.",
            Advanced: false),
        new(BaseUrlKey, "Rhythmax API base URL", DefaultBaseUrl,
            "Only for a mirror or a local hifi-api. Empty uses the public service.",
            Advanced: true),
    ];

    private readonly RhythmaxClient _client;
    private readonly IPluginConfig _config;
    private readonly ILogger<RhythmaxDownloader> _logger;
    private readonly AvailabilityCache _availability = new();

    public RhythmaxDownloader(
        RhythmaxClient client, IPluginConfig config, ILogger<RhythmaxDownloader> logger)
    {
        _client = client;
        _config = config;
        _logger = logger;
    }

    public void RefreshAvailability() => _availability.Invalidate();

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_availability.TryGet(out var cached)) return cached;

        _client.BaseUrl = await BaseUrlAsync(ct);
        var (ready, reason) = await _client.CheckHealthAsync(ct);
        LastUnavailableReason = ready ? null : reason;
        _availability.Store(ready);
        if (!ready) _logger.LogDebug("Rhythmax unavailable: {Reason}", reason);
        return ready;
    }

    public Task<DownloaderSearchHit?> SearchAsync(
        TrackQuery query, CancellationToken ct = default)
        // Provider-backed by contract. Returning null here is intentional:
        // a direct caller cannot silently fall back to a source-side search.
        => Task.FromResult<DownloaderSearchHit?>(null);

    public async Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl,
        string outputDirectory,
        string? melodyId,
        DownloadQuality? quality = null,
        CancellationToken ct = default)
    {
        if (!TryExtractTrackId(sourceUrl, out var trackId))
            return new(false, null, "not a Tidal track URL: " + sourceUrl);

        var requested = quality ?? DownloadQuality.Any;
        var tier = QualityFor(requested);
        if (tier is null)
            return new(false, null,
                $"Rhythmax cannot serve {Describe(requested)}; no lossy Tidal tier fits");

        try
        {
            _client.BaseUrl = await BaseUrlAsync(ct);
            var result = await _client.DownloadAsync(
                trackId, tier, outputDirectory, melodyId, ct);
            _availability.Store(true);
            LastUnavailableReason = null;
            return new(true, result.FilePath, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (RhythmaxException ex)
        {
            _logger.LogDebug(ex, "Rhythmax download for {TrackId} failed", trackId);
            return new(false, null, ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Rhythmax download for {TrackId} threw", trackId);
            return new(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Tidal tier for a request: FLAC means LOSSLESS, an AAC band picks HIGH
    /// when 320 fits and LOW when only 96 fits. Auto takes the best allowed
    /// level, so a "High Quality" cap does not accidentally ask for FLAC.
    /// Internal for tests.
    /// </summary>
    internal static string? QualityFor(DownloadQuality quality)
    {
        if (quality.Format == AudioFormat.Flac) return "LOSSLESS";
        if (quality.Format is AudioFormat.Mp3 or AudioFormat.Opus) return null;

        if (quality.Format == AudioFormat.Aac || quality.HasConstraints)
        {
            if (quality.MinKbps is >= 900) return "LOSSLESS";
            // Prefer the 320 tier whenever the band allows it; fall back
            // to 96 only when the cap or floor rules 320 out.
            if (quality.IsWithinBand(320) && quality.MaxKbps is not < 320)
                return "HIGH";
            if (quality.IsWithinBand(96)) return "LOW";
            return null;
        }

        return "LOSSLESS";
    }

    /// <summary>Track id from a Tidal URL or a bare id. Internal for tests.</summary>
    internal static bool TryExtractTrackId(string sourceUrl, out long trackId)
    {
        trackId = 0;
        if (string.IsNullOrWhiteSpace(sourceUrl)) return false;

        var value = sourceUrl.Trim();
        if (long.TryParse(value, out trackId) && trackId > 0) return true;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        // Exact host or a real subdomain, never "nottidal.com" or
        // "tidal.com.example.net".
        var host = uri.Host;
        if (!host.Equals("tidal.com", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".tidal.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var marker = uri.AbsolutePath.LastIndexOf("/track/", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return false;
        var tail = uri.AbsolutePath[(marker + "/track/".Length)..].Trim('/');
        return long.TryParse(tail, out trackId) && trackId > 0;
    }

    private async Task<string> BaseUrlAsync(CancellationToken ct)
    {
        var value = await _config.GetAsync(Id, BaseUrlKey, ct);
        return string.IsNullOrWhiteSpace(value) ? DefaultBaseUrl : value.Trim();
    }

    private static string Describe(DownloadQuality quality)
    {
        var format = quality.Format == AudioFormat.Auto ? "any format" : quality.Format.ToString();
        return (quality.MinKbps, quality.MaxKbps) switch
        {
            (null, null) => format,
            (null, { } max) => $"{format} up to {max} kbps",
            ({ } min, null) => $"{format} at least {min} kbps",
            ({ } min, { } max) => $"{format} {min}-{max} kbps",
        };
    }
}
