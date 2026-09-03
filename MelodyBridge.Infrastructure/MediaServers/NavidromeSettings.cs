using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Infrastructure.MediaServers;

/// <summary>
/// Navidrome connection values, resolved fresh for every sync call so the
/// Settings page (database) is the single source of truth. Navidrome has
/// no API key: the password is the credential (it travels only as a
/// salted md5 token, never in the clear).
/// </summary>
public interface INavidromeSettings
{
    /// <summary>Base URL, e.g. http://host.docker.internal:4533.</summary>
    Task<string> GetBaseUrlAsync(CancellationToken ct = default);

    /// <summary>Subsonic username.</summary>
    Task<string> GetUsernameAsync(CancellationToken ct = default);

    /// <summary>Subsonic password (salted-token hashed per call).</summary>
    Task<string> GetPasswordAsync(CancellationToken ct = default);
}

/// <summary>Navidrome values from the settings table, config fallback.</summary>
public class DbNavidromeSettings : INavidromeSettings
{
    private readonly IDbContextFactory<MelodyBridge.Infrastructure.Data.MelodyBridgeDbContext> _dbFactory;
    private readonly ConfigNavidromeSettings _fallback;

    public DbNavidromeSettings(
        IDbContextFactory<MelodyBridge.Infrastructure.Data.MelodyBridgeDbContext> dbFactory,
        ConfigNavidromeSettings fallback)
    {
        _dbFactory = dbFactory;
        _fallback = fallback;
    }

    public async Task<string> GetBaseUrlAsync(CancellationToken ct = default)
        => await ReadAsync("navidrome_url", ct) ?? await _fallback.GetBaseUrlAsync(ct);

    public async Task<string> GetUsernameAsync(CancellationToken ct = default)
        => await ReadAsync("navidrome_user", ct) ?? await _fallback.GetUsernameAsync(ct);

    public async Task<string> GetPasswordAsync(CancellationToken ct = default)
        => await ReadAsync("navidrome_password", ct) ?? await _fallback.GetPasswordAsync(ct);

    private async Task<string?> ReadAsync(string key, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.DownloaderSettings.AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
    }
}

/// <summary>Values from IConfiguration keys (Navidrome:BaseUrl, Navidrome:Username, Navidrome:Password).</summary>
public class ConfigNavidromeSettings : INavidromeSettings
{
    private readonly Microsoft.Extensions.Configuration.IConfiguration _config;
    public ConfigNavidromeSettings(Microsoft.Extensions.Configuration.IConfiguration config)
        => _config = config;

    public Task<string> GetBaseUrlAsync(CancellationToken ct = default)
        => Task.FromResult(_config["Navidrome:BaseUrl"] ?? "http://localhost:4533");

    public Task<string> GetUsernameAsync(CancellationToken ct = default)
        => Task.FromResult(_config["Navidrome:Username"] ?? string.Empty);

    public Task<string> GetPasswordAsync(CancellationToken ct = default)
        => Task.FromResult(_config["Navidrome:Password"] ?? string.Empty);
}
