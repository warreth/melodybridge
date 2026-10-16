using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// DB-backed plugin settings: one row per value under
/// "plugin:{id}:{key}" in the DownloaderSettings table, so plugin
/// configuration survives restarts like every other setting.
/// </summary>
public class PluginConfigStore : IPluginConfig
{
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;

    public PluginConfigStore(IDbContextFactory<MelodyBridgeDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    internal static string ConfigKey(string id, string key) => $"plugin:{id}:{key}";

    public async Task<string> GetAsync(string pluginId, string key, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var value = await db.DownloaderSettings.AsNoTracking()
            .Where(s => s.Key == ConfigKey(pluginId, key))
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return value ?? "";
    }

    public async Task SetAsync(string pluginId, string key, string? value, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var storageKey = ConfigKey(pluginId, key);
        var row = await db.DownloaderSettings.FirstOrDefaultAsync(s => s.Key == storageKey, ct);
        if (row is null)
            db.DownloaderSettings.Add(new DownloaderSettingEntity { Key = storageKey, Value = value ?? "" });
        else
            row.Value = value ?? "";
        await db.SaveChangesAsync(ct);
    }
}
