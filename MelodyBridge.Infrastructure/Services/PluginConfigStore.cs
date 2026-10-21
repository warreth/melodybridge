using System.Collections.Concurrent;
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
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _loaded;

    public PluginConfigStore(IDbContextFactory<MelodyBridgeDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    internal static string ConfigKey(string id, string key) => $"plugin:{id}:{key}";

    public async Task<string> GetAsync(string pluginId, string key, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _cache.GetValueOrDefault(ConfigKey(pluginId, key), "");
    }

    public async Task SetAsync(string pluginId, string key, string? value, CancellationToken ct = default)
    {
        // Load the snapshot first: a save must update the cache, not leave
        // the next read to re-query the row it just wrote.
        await EnsureLoadedAsync(ct);
        var storageKey = ConfigKey(pluginId, key);
        var stored = value ?? "";

        // Two circuits can save at once. Serialize the database write and
        // the cache update together so the snapshot ends on the same value
        // the last commit leaves in the database.
        await _writeGate.WaitAsync(ct);
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var row = await db.DownloaderSettings.FirstOrDefaultAsync(s => s.Key == storageKey, ct);
            if (row is null)
                db.DownloaderSettings.Add(new DownloaderSettingEntity { Key = storageKey, Value = stored });
            else
                row.Value = stored;
            await db.SaveChangesAsync(ct);

            if (string.IsNullOrWhiteSpace(stored))
                _cache.TryRemove(storageKey, out _);
            else
                _cache[storageKey] = stored;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// One query loads every plugin setting. The Plugins page reads each
    /// field for every card, and download plugins read their own knobs
    /// while probing; without this each read opened a context and a SQLite
    /// connection. Writes go through this singleton, so the snapshot stays
    /// current; a database restore needs the restart the app already asks
    /// for.
    /// </summary>
    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _loaded)) return;

        await _loadGate.WaitAsync(ct);
        try
        {
            if (Volatile.Read(ref _loaded)) return;

            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var rows = await db.DownloaderSettings.AsNoTracking()
                .Where(s => s.Key.StartsWith("plugin:"))
                .Select(s => new { s.Key, s.Value })
                .ToListAsync(ct);

            _cache.Clear();
            foreach (var row in rows)
                if (!string.IsNullOrWhiteSpace(row.Value))
                    _cache[row.Key] = row.Value;

            Volatile.Write(ref _loaded, true);
        }
        finally
        {
            _loadGate.Release();
        }
    }
}
