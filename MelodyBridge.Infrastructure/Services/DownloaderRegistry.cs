using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Registry for downloader plugins. Enable/disable state and priority order
/// live in the ProviderStates table, so plugin configuration survives restarts.
/// </summary>
public class DownloaderRegistry : IDownloaderRegistry
{
    private readonly IEnumerable<IDownloader> _downloaders;
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly IPluginConfig _config;
    private readonly ILogger<DownloaderRegistry> _logger;

    // plugin id -> enabled (cache, lazily loaded from DB)
    private Dictionary<string, bool> _enabled = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, int> _priority = new(StringComparer.OrdinalIgnoreCase);
    private bool _cacheLoaded;

    public DownloaderRegistry(
        IEnumerable<IDownloader> downloaders,
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        IPluginConfig config,
        ILogger<DownloaderRegistry> logger)
    {
        _downloaders = downloaders;
        _dbFactory = dbFactory;
        _config = config;
        _logger = logger;
    }

    public IReadOnlyList<IDownloader> GetAll() => _downloaders.ToList();

    public IDownloader? Get(string id)
        => _downloaders.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<IDownloader> GetEnabled()
    {
        EnsureCacheLoaded().GetAwaiter().GetResult();
        return _downloaders
            .Where(d => _enabled.TryGetValue(d.Id, out var on) && on)
            .OrderBy(d => _priority.GetValueOrDefault(d.Id, int.MaxValue))
            .ThenBy(d => d.Name)
            .ToList();
    }

    public bool IsEnabled(string id)
    {
        EnsureCacheLoaded().GetAwaiter().GetResult();
        return _enabled.TryGetValue(id, out var on) && on;
    }

    public async Task SetEnabledAsync(string id, bool enabled)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var row = await db.ProviderStates.FindAsync(id);
        if (row is null)
        {
            row = new ProviderStateRow { ProviderId = id, IsEnabled = enabled };
            db.ProviderStates.Add(row);
        }
        else row.IsEnabled = enabled;

        await db.SaveChangesAsync();
        _enabled[id] = enabled;
        _logger.LogInformation("Downloader {Id} {State}", id, enabled ? "enabled" : "disabled");
    }

    public async Task<int> GetPriorityAsync(string id, CancellationToken ct = default)
    {
        await EnsureCacheLoaded(ct);
        return _priority.GetValueOrDefault(id, int.MaxValue);
    }

    public async Task SetPriorityAsync(string id, int priority, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.ProviderStates.FindAsync(new object[] { id }, ct);
        if (row is null)
        {
            row = new ProviderStateRow { ProviderId = id, IsEnabled = true, Priority = priority };
            db.ProviderStates.Add(row);
        }
        else row.Priority = priority;

        await db.SaveChangesAsync(ct);
        _priority[id] = priority;
        _logger.LogInformation("Downloader {Id} priority set to {Priority}", id, priority);
    }

    public async Task SetOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default)
    {
        if (orderedIds.Count == 0) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ProviderStates.ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.ProviderId, StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < orderedIds.Count; i++)
        {
            var id = orderedIds[i];
            if (!byId.TryGetValue(id, out var row))
            {
                row = new ProviderStateRow { ProviderId = id, IsEnabled = true };
                db.ProviderStates.Add(row);
                byId[id] = row;
            }
            row.Priority = i;
            _priority[id] = i;
        }

        // Plugins missing from the list keep their relative order, appended after.
        var maxSeen = orderedIds.Count - 1;
        foreach (var row in byId.Values.Where(r => !orderedIds.Contains(r.ProviderId, StringComparer.OrdinalIgnoreCase)))
            row.Priority = ++maxSeen;

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Downloader waterfall order set: {Order}", string.Join(" → ", orderedIds));
    }

    // Plugin config lives in the same DownloaderSettings table as app
    // settings, namespaced per plugin: "plugin:{id}:{key}". The store is
    // shared with the plugins themselves, which read their own knobs.
    public Task<string> GetConfigAsync(string id, string key, CancellationToken ct = default)
        => _config.GetAsync(id, key, ct);

    public Task SetConfigAsync(string id, string key, string? value, CancellationToken ct = default)
        => _config.SetAsync(id, key, value, ct);

    /// <summary>
    /// The shipped waterfall order before the Soulseek cleanup. An install
    /// whose stored order is exactly this was never reordered by hand, so
    /// it is moved to the new default; a custom order is left alone.
    /// </summary>
    private static readonly string[] PreviousDefaultOrder =
    [
        "soundcloud", "archiveorg", "ytdlp", "lucida", "monochrome", "doubledouble", "dabmusic",
    ];

    /// <summary>The order a fresh install gets, and the target of the migration.</summary>
    private static readonly string[] CurrentDefaultOrder =
    [
        "soundcloud", "archiveorg", "ytdlp", "monochrome", "slskd", "doubledouble", "lucida",
    ];

    /// <summary>
    /// Moves an untouched old default order onto the new default. Only an
    /// exact match of the previous shipped sequence qualifies (with the
    /// removed DAB Music plugin filtered out), so a user's own order is
    /// never rewritten.
    /// </summary>
    private async Task MigrateUntouchedDefaultOrderAsync(
        MelodyBridgeDbContext db, CancellationToken ct)
    {
        var known = _downloaders.Select(d => d.Id).ToList();
        var ordered = _downloaders
            .OrderBy(d => _priority.GetValueOrDefault(d.Id, int.MaxValue))
            .ThenBy(d => d.Name)
            .Select(d => d.Id)
            .ToList();

        // Compare only the ids the old default knew: a plugin added after
        // the change (Soulseek) rides at the end and must not disqualify
        // the untouched-old-default detection. No overlap at all (a test
        // set, an install of unknown plugins) is never a match.
        var previous = PreviousDefaultOrder
            .Where(id => known.Contains(id, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (previous.Count == 0) return;
        var orderedOld = ordered
            .Where(id => previous.Contains(id, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (!orderedOld.SequenceEqual(previous, StringComparer.OrdinalIgnoreCase))
            return;

        var rows = await db.ProviderStates.ToListAsync(ct);
        var byId = rows.ToDictionary(r => r.ProviderId, StringComparer.OrdinalIgnoreCase);

        var index = 0;
        foreach (var id in CurrentDefaultOrder)
        {
            if (!byId.TryGetValue(id, out var row)) continue;
            row.Priority = index;
            _priority[id] = index;
            index++;
        }
        // Anything the app does not know (an old plugin row) keeps a slot
        // after the known plugins instead of colliding with them.
        foreach (var row in rows.Where(r => !CurrentDefaultOrder.Contains(r.ProviderId, StringComparer.OrdinalIgnoreCase)))
        {
            row.Priority = index;
            _priority[row.ProviderId] = index;
            index++;
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation(
            "Moved the untouched default plugin order to the new default: {Order}",
            string.Join(" → ", CurrentDefaultOrder));
    }

    private async Task EnsureCacheLoaded(CancellationToken ct = default)
    {
        if (_cacheLoaded) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var rows = await db.ProviderStates.AsNoTracking().ToListAsync(ct);

            var nextPriority = rows.Count;
            foreach (var downloader in _downloaders)
            {
                var row = rows.FirstOrDefault(r => r.ProviderId.Equals(downloader.Id, StringComparison.OrdinalIgnoreCase));
                // New plugin: register enabled with the next free priority.
                if (row is null)
                {
                    db.ProviderStates.Add(new ProviderStateRow
                    {
                        ProviderId = downloader.Id,
                        IsEnabled = true,
                        Priority = nextPriority,
                    });
                    _enabled[downloader.Id] = true;
                    _priority[downloader.Id] = nextPriority;
                    nextPriority++;
                }
                else
                {
                    _enabled[downloader.Id] = row.IsEnabled;
                    _priority[downloader.Id] = row.Priority;
                }
            }

            await db.SaveChangesAsync(ct);

            // An install that never touched the old default order gets the
            // new one; custom orders stay as they are.
            await MigrateUntouchedDefaultOrderAsync(db, ct);
        }
        catch (Exception ex)
        {
            // DB unavailable (e.g. tests without schema): enable everything.
            _logger.LogWarning(ex, "Could not load downloader states; all plugins enabled");
            foreach (var d in _downloaders)
            {
                _enabled[d.Id] = true;
                _priority[d.Id] = 0;
            }
        }
        finally
        {
            _cacheLoaded = true;
        }
    }
}
