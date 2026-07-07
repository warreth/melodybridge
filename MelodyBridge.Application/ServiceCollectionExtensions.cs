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

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMelodyBridge(this IServiceCollection services)
    {
        // Infrastructure services
        services.AddScoped<M3uGenerator>();
        services.AddScoped<ILibraryScanner, LibraryScanner>();
        services.AddSingleton<MusicProviderRegistry>();

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

        // Source providers
        services.AddSingleton<ISourceProvider, YouTubeSourceProvider>();

        // Application services
        services.AddScoped<IDownloadManager, DownloadManager>();
        services.AddScoped<DownloadManager>();
        services.AddScoped<SyncEngine>();
        services.AddScoped<ISyncJobRunner, SyncJobRunner>();
        services.AddScoped<IMusicSourceManager, MusicSourceManager>();

        // Background services
        services.AddHostedService<AutoSyncBackgroundService>();
        services.AddHostedService<ScanSchedulingBackgroundService>();

        return services;
    }

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
