using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.MediaServers;
using MelodyBridge.Infrastructure.Playlists;
using MelodyBridge.Infrastructure.Scanning;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMelodyBridge(this IServiceCollection services)
    {
        // Infrastructure services
        services.AddScoped<M3uGenerator>();
        services.AddScoped<ILibraryScanner, LibraryScanner>();

        // ── Downloader plugins (the waterfall) ──
        // SoundCloud first (original uploads, often 320 kbps), then the Internet
        // Archive (public MP3s), YouTube last as the widest fallback.
        services.AddSingleton<IDownloader>(sp =>
            new SoundCloudDownloader(sp.GetRequiredService<ILogger<SoundCloudDownloader>>()));
        services.AddSingleton<IDownloader>(sp =>
            new ArchiveOrgDownloader(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("archiveorg"),
                sp.GetRequiredService<ILogger<ArchiveOrgDownloader>>()));
        services.AddSingleton<IDownloader>(sp =>
            new YtDlpDownloader(sp.GetRequiredService<ILogger<YtDlpDownloader>>()));
        services.AddHttpClient("archiveorg", c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddSingleton<IDownloaderRegistry, DownloaderRegistry>();

        // Source providers (playlists)
        services.AddSingleton<ISourceProvider, YouTubeSourceProvider>();
        services.AddSingleton<ISourceProvider, SpotifySourceProvider>();

        // Application services
        services.AddScoped<IDownloadManager, DownloadManager>();
        services.AddScoped<SyncEngine>();
        services.AddScoped<ISyncJobRunner, SyncJobRunner>();
        services.AddScoped<PlaylistStore>();

        // Background services
        services.AddHostedService<AutoSyncBackgroundService>();
        services.AddHostedService<ScanSchedulingBackgroundService>();
        services.AddHostedService<FileSystemMonitoringBackgroundService>();

        // File system monitor
        services.AddSingleton<IFileSystemMonitor, FileSystemMonitor>();

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
