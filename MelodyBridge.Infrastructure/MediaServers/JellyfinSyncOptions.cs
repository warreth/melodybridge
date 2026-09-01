namespace MelodyBridge.Infrastructure.MediaServers;

/// <summary>
/// Jellyfin connection values, resolved fresh for every sync call so the
/// Settings page (database) is the single source of truth and saved
/// changes apply without a restart.
/// </summary>
public interface IJellyfinSettings
{
    /// <summary>Base URL, e.g. http://host.docker.internal:8096.</summary>
    Task<string> GetBaseUrlAsync(CancellationToken ct = default);

    /// <summary>API key; empty when not configured.</summary>
    Task<string> GetApiKeyAsync(CancellationToken ct = default);

    /// <summary>User whose favorites receive the liked songs.</summary>
    Task<string?> GetUserIdAsync(CancellationToken ct = default);
}

/// <summary>Values from IConfiguration keys (Jellyfin:BaseUrl, ...).</summary>
public class ConfigJellyfinSettings : IJellyfinSettings
{
    private readonly Microsoft.Extensions.Configuration.IConfiguration _config;
    public ConfigJellyfinSettings(Microsoft.Extensions.Configuration.IConfiguration config)
        => _config = config;

    public Task<string> GetBaseUrlAsync(CancellationToken ct = default)
        => Task.FromResult(_config["Jellyfin:BaseUrl"] ?? "http://localhost:8096");

    public Task<string> GetApiKeyAsync(CancellationToken ct = default)
        => Task.FromResult(_config["Jellyfin:ApiKey"] ?? string.Empty);

    public Task<string?> GetUserIdAsync(CancellationToken ct = default)
        => Task.FromResult(_config["Jellyfin:UserId"]);
}
