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
        services.AddSingleton(registry);
        services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(dbFactory);
        services.AddSingleton(store);
        // The coordinator resolves the scoped store per run from the
        // provider the TestContext builds, like the real app does.
        services.AddSingleton<DownloadCoordinator>();
        var settingsStore = new SettingsStore(dbFactory);
        services.AddSingleton(settingsStore);
        services.AddSingleton(new MediaServerProfileStore(settingsStore));
        services.AddSingleton(new DatabaseBackupService(dbFactory,
            NullLogger<DatabaseBackupService>.Instance));
        services.AddSingleton(new MelodyBridge.Server.Services.NotificationService());
        // Update check: a plain HttpClient; tests that need GitHub
        // behaviour stub the handler themselves.
        services.AddSingleton(new UpdateCheckService(new HttpClient()));
        services.AddHttpClient();
        // Media-server settings the Home page reads; empty = "not configured".
        services.AddSingleton<MelodyBridge.Infrastructure.MediaServers.IJellyfinSettings>(
            new FixedJellyfinSettings());
        services.AddSingleton<MelodyBridge.Infrastructure.MediaServers.IPlexSettings>(
            new FixedPlexSettings());
        services.AddSingleton<MelodyBridge.Infrastructure.MediaServers.INavidromeSettings>(
            new FixedNavidromeSettings());
    }

    /// <summary>Fixed connection values, exactly what the settings interfaces deliver.</summary>
    public sealed class FixedJellyfinSettings : MelodyBridge.Infrastructure.MediaServers.IJellyfinSettings
    {
        public string BaseUrl { get; init; } = "";
        public string ApiKey { get; init; } = "";
        public string? UserId { get; init; }

        public Task<string> GetBaseUrlAsync(CancellationToken ct = default) => Task.FromResult(BaseUrl);
        public Task<string> GetApiKeyAsync(CancellationToken ct = default) => Task.FromResult(ApiKey);
        public Task<string?> GetUserIdAsync(CancellationToken ct = default) => Task.FromResult(UserId);
    }

    public sealed class FixedPlexSettings : MelodyBridge.Infrastructure.MediaServers.IPlexSettings
    {
        public string BaseUrl { get; init; } = "";
        public string ApiKey { get; init; } = "";

        public Task<string> GetBaseUrlAsync(CancellationToken ct = default) => Task.FromResult(BaseUrl);
        public Task<string> GetApiKeyAsync(CancellationToken ct = default) => Task.FromResult(ApiKey);
    }

    public sealed class FixedNavidromeSettings : MelodyBridge.Infrastructure.MediaServers.INavidromeSettings
    {
        public string BaseUrl { get; init; } = "";
        public string Username { get; init; } = "";
        public string Password { get; init; } = "";

        public Task<string> GetBaseUrlAsync(CancellationToken ct = default) => Task.FromResult(BaseUrl);
        public Task<string> GetUsernameAsync(CancellationToken ct = default) => Task.FromResult(Username);
        public Task<string> GetPasswordAsync(CancellationToken ct = default) => Task.FromResult(Password);
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
    public Task<string> GetConfigAsync(string id, string key, CancellationToken ct = default) => Task.FromResult("");
    public Task SetConfigAsync(string id, string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }
}
