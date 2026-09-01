using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests;

public static class TestServices
{
    /// <summary>
    /// Registers every service the playlist/plugin pages inject:
    /// download manager, playlist store, coordinator, settings store.
    /// Call once per bUnit TestContext, before rendering.
    /// </summary>
    public static void AddDownloadPages(this IServiceCollection services,
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        params IDownloader[] downloaders)
    {
        IDownloaderRegistry registry = downloaders.Length > 0
            ? new ListRegistry(downloaders)
            : new EmptyRegistry();
        var manager = new DownloadManager(registry,
            NullLogger<DownloadManager>.Instance);
        var store = new PlaylistStore(dbFactory,
            Array.Empty<ISourceProvider>(), manager,
            NullLogger<PlaylistStore>.Instance);
        services.AddSingleton<IDownloadManager>(manager);
        services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(dbFactory);
        services.AddSingleton(store);
        // The coordinator resolves the scoped store per run from the
        // provider the TestContext builds, like the real app does.
        services.AddSingleton<DownloadCoordinator>();
        services.AddSingleton(new SettingsStore(dbFactory));
    }

    private sealed class ListRegistry(IDownloader[] downloaders) : IDownloaderRegistry
    {
        public IReadOnlyList<IDownloader> GetAll() => downloaders;
        public IDownloader? Get(string id) => downloaders.FirstOrDefault(d => d.Id == id);
        public IReadOnlyList<IDownloader> GetEnabled() => downloaders;
        public Task SetEnabledAsync(string id, bool enabled) => Task.CompletedTask;
        public bool IsEnabled(string id) => true;
        public Task<int> GetPriorityAsync(string id, CancellationToken ct = default) => Task.FromResult(0);
        public Task SetPriorityAsync(string id, int priority, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
    }
}
