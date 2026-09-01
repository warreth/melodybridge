using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Accounts;

using AccountTokens = MelodyBridge.Core.AccountTokens;

/// <summary>
/// Stores account OAuth tokens (and simple account settings) in the
/// existing settings table under "account:{provider}" keys. Tokens are the
/// only secrets: the access token, refresh token and expiry live as one
/// JSON blob per provider.
/// </summary>
public class AccountTokenStore
{
    private readonly IDbContextFactory<MelodyBridge.Infrastructure.Data.MelodyBridgeDbContext> _dbFactory;
    private readonly ILogger<AccountTokenStore> _logger;

    public AccountTokenStore(
        IDbContextFactory<MelodyBridge.Infrastructure.Data.MelodyBridgeDbContext> dbFactory,
        ILogger<AccountTokenStore> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    private static string TokenKey(string provider) => $"account:{provider.ToLowerInvariant()}:tokens";

    public async Task<AccountTokens?> GetTokensAsync(string provider, CancellationToken ct = default)
    {
        var value = await GetValueAsync(TokenKey(provider), ct);
        if (value is null) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<AccountTokens>(value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Corrupt tokens for {Provider}: {Message}", provider, ex.Message);
            return null;
        }
    }

    public async Task SaveTokensAsync(string provider, AccountTokens tokens, CancellationToken ct = default)
    {
        await SetValueAsync(TokenKey(provider),
            System.Text.Json.JsonSerializer.Serialize(tokens), ct);
    }

    /// <summary>Account settings (client id, redirect url, ...), not secrets.</summary>
    public Task<string?> GetSettingAsync(string provider, string key, CancellationToken ct = default)
        => GetValueAsync($"account:{provider.ToLowerInvariant()}:{key}", ct);

    public Task SaveSettingAsync(string provider, string key, string value, CancellationToken ct = default)
        => SetValueAsync($"account:{provider.ToLowerInvariant()}:{key}", value, ct);

    /// <summary>Removes everything stored for one provider (logout).</summary>
    public async Task ClearAsync(string provider, CancellationToken ct = default)
    {
        var prefix = $"account:{provider.ToLowerInvariant()}:";
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = db.DownloaderSettings.Where(s => s.Key.StartsWith(prefix)).ToList();
        if (rows.Count > 0)
        {
            db.DownloaderSettings.RemoveRange(rows);
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task<string?> GetValueAsync(string key, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.DownloaderSettings.AsNoTracking()
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
    }

    private async Task SetValueAsync(string key, string value, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.DownloaderSettings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            db.DownloaderSettings.Add(new MelodyBridge.Infrastructure.Data.DownloaderSettingEntity
            {
                Key = key, Value = value, ProviderId = "account",
            });
        }
        else
        {
            row.Value = value;
        }
        await db.SaveChangesAsync(ct);
    }
}
