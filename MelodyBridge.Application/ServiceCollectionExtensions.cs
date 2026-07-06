using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.MediaServers;
using MelodyBridge.Infrastructure.Playlists;
using MelodyBridge.Infrastructure.Providers;
using MelodyBridge.Infrastructure.Scanning;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MelodyBridge.Application;

/// <summary>
/// Extension methods for registering MelodyBridge services in a DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all MelodyBridge infrastructure and application services.
    /// </summary>
    public static IServiceCollection AddMelodyBridge(this IServiceCollection services)
    {
        // Infrastructure services
        services.AddScoped<M3uGenerator>();
        services.AddScoped<LibraryScanner>();
        services.AddScoped<MusicProviderRegistry>();

        // Legacy downloaders
        services.AddSingleton<YouTubeDownloader>();
        services.AddSingleton<IAsyncDownloader>(sp =>
            sp.GetRequiredService<YouTubeDownloader>());

        // Music providers (plugins)
        services.AddSingleton<IMusicProvider, SquidWtfProvider>();
        services.AddSingleton<IMusicProvider, LucidaProvider>();
        services.AddSingleton<IMusicProvider, DoubleDoubleProvider>();
        services.AddSingleton<IMusicProvider, MonochromeProvider>();

        // Registry
        services.AddSingleton<IMusicProviderRegistry, MusicProviderRegistry>();

        // Application services
        services.AddScoped<DownloadManager>();
        services.AddScoped<SyncEngine>();

        return services;
    }

    /// <summary>
    /// Registers the Jellyfin media server sync client.
    /// Requires Jellyfin:BaseUrl and optionally Jellyfin:ApiKey in configuration.
    /// </summary>
    public static IServiceCollection AddJellyfinSync(this IServiceCollection services)
    {
        services.AddHttpClient<JellyfinSync>((sp, client) =>
        {
            var config = sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            client.BaseAddress = new Uri(
                config["Jellyfin:BaseUrl"] ?? "http://localhost:8096");
            var apiKey = config["Jellyfin:ApiKey"];
            if (!string.IsNullOrEmpty(apiKey))
                client.DefaultRequestHeaders.Add("X-Emby-Token", apiKey);
        });
        services.AddSingleton<IMediaServerSync>(sp =>
            sp.GetRequiredService<JellyfinSync>());
        return services;
    }
}
