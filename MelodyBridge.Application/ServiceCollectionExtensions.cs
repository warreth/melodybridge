using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Accounts;
using MelodyBridge.Infrastructure.Cloudflare;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.Lucida;
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

        // Cloudflare solver shared by challenge-gated plugins (Lucida).
        services.AddHttpClient("flaresolverr", c => c.Timeout = TimeSpan.FromMinutes(2));
        services.AddOptions<FlareSolverrOptions>();
        services.AddSingleton<IChallengeSolver>(sp =>
            new FlareSolverrSolver(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("flaresolverr"),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<FlareSolverrOptions>>(),
                sp.GetRequiredService<ILogger<FlareSolverrSolver>>()));
        services.AddHttpClient("lucida", c => c.Timeout = TimeSpan.FromMinutes(30));
        services.AddSingleton<IDownloader>(sp =>
            new LucidaDownloader(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("lucida"),
                sp.GetRequiredService<IChallengeSolver>(),
                sp.GetRequiredService<ILogger<LucidaDownloader>>()));

        services.AddSingleton<IDownloaderRegistry, DownloaderRegistry>();

        // Source providers (playlists)
        services.AddSingleton<ISourceProvider, YouTubeSourceProvider>();
        services.AddSingleton<ISourceProvider, SpotifySourceProvider>();

        // Application services
        services.AddScoped<IDownloadManager, DownloadManager>();
        services.AddScoped<SyncEngine>();
        services.AddScoped<ISyncJobRunner, SyncJobRunner>();
        // Account connections (Spotify/YouTube private + liked imports).
        services.AddSingleton<AccountTokenStore>();
        // Concrete type for the pages, interface for PlaylistStore.
        services.AddSingleton<SpotifyAccountProvider>();
        services.AddSingleton<YouTubeAccountProvider>();
        services.AddSingleton<IAccountSourceProvider>(sp => sp.GetRequiredService<SpotifyAccountProvider>());
        services.AddSingleton<IAccountSourceProvider>(sp => sp.GetRequiredService<YouTubeAccountProvider>());
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
        // A plain named client: connection values are applied per sync
        // call from IJellyfinSettings (database first, config fallback),
        // so Settings-page changes apply without a restart.
        services.AddHttpClient(nameof(JellyfinSync));
        services.AddSingleton<ConfigJellyfinSettings>();
        services.AddSingleton<IJellyfinSettings, DbJellyfinSettings>();
        services.AddSingleton(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(JellyfinSync));
            var logger = sp.GetRequiredService<ILogger<JellyfinSync>>();
            return new JellyfinSync(http, logger, sp.GetRequiredService<IJellyfinSettings>());
        });
        services.AddSingleton<IMediaServerSync>(sp =>
            sp.GetRequiredService<JellyfinSync>());
        return services;
    }
}
