using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.FlacMusic;

/// <summary>
/// The flac.music.hi.cn state the search provider and the downloader share:
/// the client, the base URL and platform setting, and the short negative
/// availability window. A search that fails on every platform updates the
/// same reason the plugin card shows.
/// </summary>
public sealed class FlacMusicSession
{
    public const string DefaultBaseUrl = FlacMusicClient.DefaultBaseUrl;
    public const string BaseUrlKey = "base_url";
    public const string PlatformKey = "platform";
    public const string PlatformBoth = "both";

    private readonly FlacMusicClient _client;
    private readonly IPluginConfig _config;
    private readonly ILogger<FlacMusicSession> _logger;
    private readonly AvailabilityCache _availability = new();

    public FlacMusicSession(
        FlacMusicClient client, IPluginConfig config, ILogger<FlacMusicSession> logger)
    {
        _client = client;
        _config = config;
        _logger = logger;
    }

    public FlacMusicClient Client => _client;

    public string? LastUnavailableReason { get; private set; }

    public void SetUnavailable(string? reason) => LastUnavailableReason = reason;

    public async Task<string> BaseUrlAsync(CancellationToken ct)
    {
        var value = await _config.GetAsync("flacmusic", BaseUrlKey, ct);
        var baseUrl = string.IsNullOrWhiteSpace(value) ? DefaultBaseUrl : value.Trim();
        _client.BaseUrl = baseUrl;
        return baseUrl;
    }

    public async Task<IReadOnlyList<string>> PlatformsAsync(CancellationToken ct)
    {
        var value = await _config.GetAsync("flacmusic", PlatformKey, ct);
        return string.IsNullOrWhiteSpace(value)
               || value.Equals(PlatformBoth, StringComparison.OrdinalIgnoreCase)
            ? new[] { "kuwo", "wyy" }
            : new[] { value.Trim().ToLowerInvariant() };
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_availability.TryGet(out var cached)) return cached;

        try
        {
            await BaseUrlAsync(ct);
            var (ready, reason) = await _client.CheckHealthAsync(ct);
            SetUnavailable(ready ? null : reason);
            _availability.Store(ready);
            return ready;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetUnavailable("The FLAC Music site could not be reached: " + ex.Message);
            _logger.LogDebug(ex, "FLAC Music health probe failed");
            _availability.Store(false);
            return false;
        }
    }

    public void RefreshAvailability() => _availability.Invalidate();
}
