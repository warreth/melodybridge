using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.SearchProviders;

/// <summary>
/// The Monochrome API host resolution shared by the provider and the
/// downloader. One setting, one default, one trim rule.
/// </summary>
public static class MonochromeApi
{
    public const string DefaultBaseUrl = "https://tracks.monochrome.st";
    public const string BaseUrlKey = "base_url";

    /// <summary>The configured API host, falling back to the official one.</summary>
    public static async Task<string> BaseUrlAsync(
        IPluginConfig config, string pluginId, CancellationToken ct)
    {
        var configured = await config.GetAsync(pluginId, BaseUrlKey, ct);
        if (string.IsNullOrWhiteSpace(configured)) return DefaultBaseUrl;
        return configured.Trim().TrimEnd('/');
    }
}
