using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Infrastructure.MediaServers;

/// <summary>
/// Plex connection values, resolved fresh for every sync call so the
/// Settings page (database) is the single source of truth.
/// </summary>
public interface IPlexSettings
{
    /// <summary>Base URL, e.g. http://host.docker.internal:32400.</summary>
    Task<string> GetBaseUrlAsync(CancellationToken ct = default);

    /// <summary>X-Plex-Token; empty when not configured.</summary>
    Task<string> GetApiKeyAsync(CancellationToken ct = default);
}

/// <summary>Plex values from the settings table (plex_url, plex_key), config fallback.</summary>
public class DbPlexSettings : IPlexSettings
{
    private readonly IDbContextFactory<MelodyBridge.Infrastructure.Data.MelodyBridgeDbContext> _dbFactory;
    private readonly ConfigPlexSettings _fallback;

    public DbPlexSettings(
        IDbContextFactory<MelodyBridge.Infrastructure.Data.MelodyBridgeDbContext> dbFactory,
        ConfigPlexSettings fallback)
    {
        _dbFactory = dbFactory;
        _fallback = fallback;
    }

    public async Task<string> GetBaseUrlAsync(CancellationToken ct = default)
        => await ReadAsync("plex_url", ct) ?? await _fallback.GetBaseUrlAsync(ct);

    public async Task<string> GetApiKeyAsync(CancellationToken ct = default)
        => await ReadAsync("plex_key", ct) ?? await _fallback.GetApiKeyAsync(ct);

    private async Task<string?> ReadAsync(string key, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.DownloaderSettings.AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
    }
}

/// <summary>Values from IConfiguration keys (Plex:BaseUrl, Plex:ApiKey).</summary>
public class ConfigPlexSettings : IPlexSettings
{
    private readonly Microsoft.Extensions.Configuration.IConfiguration _config;
    public ConfigPlexSettings(Microsoft.Extensions.Configuration.IConfiguration config)
        => _config = config;

    public Task<string> GetBaseUrlAsync(CancellationToken ct = default)
        => Task.FromResult(_config["Plex:BaseUrl"] ?? "http://localhost:32400");

    public Task<string> GetApiKeyAsync(CancellationToken ct = default)
        => Task.FromResult(_config["Plex:ApiKey"] ?? string.Empty);
}
