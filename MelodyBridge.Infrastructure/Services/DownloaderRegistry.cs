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
    private readonly ILogger<DownloaderRegistry> _logger;

    // plugin id -> enabled (cache, lazily loaded from DB)
    private Dictionary<string, bool> _enabled = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, int> _priority = new(StringComparer.OrdinalIgnoreCase);
    private bool _cacheLoaded;

    public DownloaderRegistry(
        IEnumerable<IDownloader> downloaders,
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        ILogger<DownloaderRegistry> logger)
    {
        _downloaders = downloaders;
        _dbFactory = dbFactory;
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

    private async Task EnsureCacheLoaded(CancellationToken ct = default)
    {
        if (_cacheLoaded) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var rows = await db.ProviderStates.AsNoTracking().ToListAsync(ct);

            foreach (var downloader in _downloaders)
            {
                var row = rows.FirstOrDefault(r => r.ProviderId.Equals(downloader.Id, StringComparison.OrdinalIgnoreCase));
                // New plugin: register enabled with default priority.
                if (row is null)
                {
                    db.ProviderStates.Add(new ProviderStateRow
                    {
                        ProviderId = downloader.Id,
                        IsEnabled = true,
                        Priority = rows.Count,
                    });
                    _enabled[downloader.Id] = true;
                    _priority[downloader.Id] = rows.Count;
                }
                else
                {
                    _enabled[downloader.Id] = row.IsEnabled;
                    _priority[downloader.Id] = row.Priority;
                }
            }

            await db.SaveChangesAsync(ct);
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
