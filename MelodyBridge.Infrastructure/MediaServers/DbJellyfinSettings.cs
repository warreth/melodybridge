using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Infrastructure.MediaServers;

/// <summary>
/// Jellyfin connection values from the settings table, exactly what the
/// Settings page writes (jellyfin_url, jellyfin_key, jellyfin_user).
/// Falls back to IConfiguration when no row exists, so env-based setups
/// keep working.
/// </summary>
public class DbJellyfinSettings : IJellyfinSettings
{
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly ConfigJellyfinSettings _fallback;

    public DbJellyfinSettings(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        ConfigJellyfinSettings fallback)
    {
        _dbFactory = dbFactory;
        _fallback = fallback;
    }

    public async Task<string> GetBaseUrlAsync(CancellationToken ct = default)
        => await ReadAsync("jellyfin_url", ct)
           ?? await _fallback.GetBaseUrlAsync(ct);

    public async Task<string> GetApiKeyAsync(CancellationToken ct = default)
        => await ReadAsync("jellyfin_key", ct)
           ?? await _fallback.GetApiKeyAsync(ct);

    public async Task<string?> GetUserIdAsync(CancellationToken ct = default)
        => await ReadAsync("jellyfin_user", ct)
           ?? await _fallback.GetUserIdAsync(ct);

    private async Task<string?> ReadAsync(string key, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.DownloaderSettings.AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
    }
}
