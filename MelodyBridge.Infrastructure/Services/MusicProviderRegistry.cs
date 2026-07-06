using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Registry that manages available music providers and persists their enabled/disabled state
/// in the MelodyBridge database.
/// </summary>
public class MusicProviderRegistry : IMusicProviderRegistry
{
    private readonly IEnumerable<IMusicProvider> _providers;
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly ILogger<MusicProviderRegistry> _logger;

    // In-memory cache of enabled state (synced lazily from DB)
    private HashSet<string> _enabledIds = new(StringComparer.OrdinalIgnoreCase);
    private bool _cacheLoaded;

    public MusicProviderRegistry(
        IEnumerable<IMusicProvider> providers,
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        ILogger<MusicProviderRegistry> logger)
    {
        _providers = providers;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public IReadOnlyList<IMusicProvider> GetAllProviders() => _providers.ToList();

    public IMusicProvider? GetProvider(string id)
        => _providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<IMusicProvider> GetEnabledProviders()
    {
        EnsureCacheLoaded().GetAwaiter().GetResult();
        return _providers.Where(p => _enabledIds.Contains(p.Id)).ToList();
    }

    public bool IsProviderEnabled(string id)
    {
        EnsureCacheLoaded().GetAwaiter().GetResult();
        return _enabledIds.Contains(id);
    }

    public async Task SetProviderEnabledAsync(string id, bool enabled)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var existing = await db.ProviderStates
            .FirstOrDefaultAsync(ps => ps.ProviderId == id);

        if (existing != null)
        {
            existing.IsEnabled = enabled;
        }
        else
        {
            db.ProviderStates.Add(new ProviderStateRow
            {
                ProviderId = id,
                IsEnabled = enabled,
            });
        }

        await db.SaveChangesAsync();

        // Update cache
        if (enabled)
            _enabledIds.Add(id);
        else
            _enabledIds.Remove(id);

        _logger.LogInformation("Provider {Id} is now {State}", id, enabled ? "enabled" : "disabled");
    }

    private async Task EnsureCacheLoaded()
    {
        if (_cacheLoaded) return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var states = await db.ProviderStates.ToListAsync();

            foreach (var provider in _providers)
            {
                var state = states.FirstOrDefault(s => s.ProviderId == provider.Id);
                if (state == null)
                {
                    // New provider — default to enabled
                    db.ProviderStates.Add(new ProviderStateRow
                    {
                        ProviderId = provider.Id,
                        IsEnabled = true,
                    });
                    _enabledIds.Add(provider.Id);
                }
                else if (state.IsEnabled)
                {
                    _enabledIds.Add(provider.Id);
                }
            }

            await db.SaveChangesAsync();
            _cacheLoaded = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load provider states from DB; enabling all providers");

            // Fallback: enable all
            foreach (var p in _providers)
                _enabledIds.Add(p.Id);

            _cacheLoaded = true;
        }
    }
}
