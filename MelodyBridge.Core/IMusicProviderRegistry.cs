namespace MelodyBridge.Core;

/// <summary>
/// Registry that holds all available music providers and tracks their enabled/disabled state.
/// </summary>
public interface IMusicProviderRegistry
{
    /// <summary>Get all registered providers.</summary>
    IReadOnlyList<IMusicProvider> GetAllProviders();

    /// <summary>Get a specific provider by ID.</summary>
    IMusicProvider? GetProvider(string id);

    /// <summary>Get only enabled providers.</summary>
    IReadOnlyList<IMusicProvider> GetEnabledProviders();

    /// <summary>Enable or disable a provider by ID.</summary>
    Task SetProviderEnabledAsync(string id, bool enabled);

    /// <summary>Check if a provider is currently enabled.</summary>
    bool IsProviderEnabled(string id);
}
