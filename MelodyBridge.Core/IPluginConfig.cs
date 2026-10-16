namespace MelodyBridge.Core;

/// <summary>
/// Per-plugin settings, stored under "plugin:{id}:{key}" rows in the
/// DownloaderSettings table. Plugins read their own knobs through this
/// (the base URL of a mirror, an API key); the Plugins page writes the
/// same values through the registry.
/// </summary>
public interface IPluginConfig
{
    /// <summary>The stored value, or "" when the key was never set.</summary>
    Task<string> GetAsync(string pluginId, string key, CancellationToken ct = default);

    /// <summary>Stores a value; null or empty clears it.</summary>
    Task SetAsync(string pluginId, string key, string? value, CancellationToken ct = default);
}
