using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Jumo;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.SearchProviders;

/// <summary>
/// The Jumo service state shared by the downloader and the search provider:
/// the client, the region cache and the short negative availability window.
/// A refused or throttled search still marks the plugin unavailable for the
/// same short TTL the probe uses, so the waterfall skips it instead of
/// retrying every track against a service that is refusing requests.
/// </summary>
public sealed class JumoSession
{
    public const string PluginId = "jumo";
    public const string RegionKey = "region";

    private static readonly TimeSpan RegionsTtl = TimeSpan.FromMinutes(10);

    private readonly JumoClient _client;
    private readonly IPluginConfig _config;
    private readonly ILogger<JumoSession> _logger;
    private readonly AvailabilityCache _availability = new();

    private readonly SemaphoreSlim _regionGate = new(1, 1);
    private DateTimeOffset _regionsFetchedAt = DateTimeOffset.MinValue;
    private string? _defaultRegion;
    private List<string> _availableRegions = [];

    public JumoSession(JumoClient client, IPluginConfig config, ILogger<JumoSession> logger)
    {
        _client = client;
        _config = config;
        _logger = logger;
    }

    public JumoClient Client => _client;

    public string? LastUnavailableReason { get; private set; }

    public void SetUnavailable(string? reason) => LastUnavailableReason = reason;

    /// <summary>The availability verdict, cached for the short TTL.</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_availability.TryGet(out var cached)) return cached;

        try
        {
            var regions = await RegionsAsync(ct);
            var available = regions is not null;
            SetUnavailable(available
                ? null
                : "The service's region endpoint did not answer");
            _availability.Store(available);
            return available;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetUnavailable("The service could not be reached: " + ex.Message);
            _logger.LogDebug(ex, "Jumo availability probe failed");
            _availability.Store(false);
            return false;
        }
    }

    /// <summary>A refused or throttled search makes the plugin unavailable for the short TTL.</summary>
    public void ReportSearchFailure(int status)
    {
        SetUnavailable(status is 403
            ? "The service refused the search (HTTP 403); it guards its API against non-browser clients"
            : $"The service is throttling requests (HTTP {status})");
        _availability.Store(false);
    }

    /// <summary>
    /// The storefront to use: the configured code when the service still
    /// offers it, otherwise the service's current default.
    /// </summary>
    public async Task<string> EffectiveRegionAsync(CancellationToken ct)
    {
        var configured = (await _config.GetAsync(PluginId, RegionKey, ct)).Trim().ToUpperInvariant();
        var regions = await RegionsAsync(ct);
        if (configured.Length == 2)
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

            using var doc = System.Text.Json.JsonDocument.Parse(response.Body);
            if (!doc.RootElement.TryGetProperty("regions", out var regions) ||
                regions.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;

            var available = new List<string>();
            foreach (var region in regions.EnumerateArray())
            {
                if (region.TryGetProperty("available", out var flag) &&
                    flag.ValueKind == System.Text.Json.JsonValueKind.False)
                    continue;
                var code = JumoJson.GetId(region, "code");
                if (!string.IsNullOrEmpty(code)) available.Add(code);
            }

            var fallback = JumoJson.GetId(doc.RootElement, "default");
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
}
