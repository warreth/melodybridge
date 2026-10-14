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
        services.AddScoped<MelodyBridge.Infrastructure.Services.DatabaseBackupService>();
        services.AddScoped<MelodyBridge.Infrastructure.Services.MediaServerProfileStore>();
        services.AddSingleton<MelodyBridge.Infrastructure.Scanning.LibraryReconciler>();
        // OS hard links for the dedup feature; stateless, one for all.
        services.AddSingleton<MelodyBridge.Infrastructure.Files.HardLinkService>();

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
        services.AddHttpClient("archiveorg", c =>
        {
            // archive.org asks API clients to identify themselves.
            c.DefaultRequestHeaders.UserAgent.ParseAdd("MelodyBridge/1.0 (+https://melodybridge.app)");
            c.Timeout = TimeSpan.FromMinutes(5);
        });

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

        // Community Hi-Fi rips: Monochrome mirrors TIDAL (search + manifest),
        // DoubleDouble rips many services by direct URL (submit + poll, no
        // metadata search: the frontend search is captcha-gated). The
        // Monochrome client allows long manifest transfers; the plugin caps
        // each API call with its own short timeout.
        services.AddHttpClient("monochrome", c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddSingleton<IDownloader>(sp =>
            new MonochromeDownloader(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("monochrome"),
                sp.GetRequiredService<ILogger<MonochromeDownloader>>()));
        services.AddHttpClient("doubledouble", c => c.Timeout = TimeSpan.FromMinutes(3));
        services.AddSingleton<IDownloader>(sp =>
            new DoubleDoubleDownloader(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("doubledouble"),
                sp.GetRequiredService<ILogger<DoubleDoubleDownloader>>()));

        // DAB Music: community Qobuz rips (dabmusic.xyz), no account
        // needed for search or stream; flaky service, gated by a cheap
        // availability probe.
        services.AddHttpClient("dabmusic", c => c.Timeout = TimeSpan.FromMinutes(5));
        services.AddSingleton<IDownloader>(sp =>
            new DabMusicDownloader(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("dabmusic"),
                sp.GetRequiredService<ILogger<DabMusicDownloader>>()));

        // ISRC enrichment for sources that do not provide one (Spotify
        // embed scrape, YouTube, CSV imports): Deezer's public search API
        // needs no account and returns the ISRC of every hit.
        services.AddHttpClient("deezer", c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("MelodyBridge/1.0 (+https://melodybridge.app)");
            c.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddSingleton(sp => new IsrcResolver(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("deezer"),
            sp.GetRequiredService<ILogger<IsrcResolver>>()));

        services.AddSingleton<IDownloaderRegistry, DownloaderRegistry>();

        // Version read and update button for the shared yt-dlp binary
        // (YouTube and SoundCloud plugins) on the Plugins page.
        services.AddSingleton(sp => new YtDlpUpdater(
            sp.GetRequiredService<ILogger<YtDlpUpdater>>()));

        // Source providers (playlists)
        services.AddSingleton<ISourceProvider, YouTubeSourceProvider>();
        services.AddSingleton<ISourceProvider, SpotifySourceProvider>();

        // Application services. Singleton: the download progress map and
        // the coordinator's run handles must survive page navigations
        // (that is why the progress bar used to vanish between pages).
        services.AddSingleton<IDownloadManager, DownloadManager>();
        services.AddSingleton<DownloadCoordinator>();
        // The auto-sync scheduler starts runs through the Core trigger,
        // so Infrastructure never has to reference Application.
        services.AddSingleton<IDownloadTrigger>(sp => sp.GetRequiredService<DownloadCoordinator>());
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
        services.AddHostedService<SyncJobSchedulingBackgroundService>();

        // File system monitor
        services.AddSingleton<IFileSystemMonitor, FileSystemMonitor>();

        // App-level settings (Settings page, intro flag, advanced toggles)
        services.AddSingleton<SettingsStore>();

        return services;
    }

    /// <summary>
    /// Registers every media-server sync plugin (Jellyfin, Plex, Navidrome)
    /// and their shared connection settings. Connection values are applied
    /// per sync call, so Settings-page changes apply without a restart.
    /// </summary>
    public static IServiceCollection AddMediaServerSyncs(this IServiceCollection services)
    {
        // Jellyfin: a plain named client; per-call connection from
        // IJellyfinSettings (database first, config fallback).
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

        // User picker for the wizard: per-request token, own short-timeout
        // client inside the service, so the sync's BaseAddress mutations
        // never leak into it.
        services.AddSingleton<IMediaServerDirectory, JellyfinUserDirectory>();

        // Sign-in for Jellyfin: playlists belong to a user there, and an
        // API key has no user context, so the wizard signs in and stores the
        // returned session token as the connection credential.
        services.AddHttpClient("jellyfin-login", c => c.Timeout = TimeSpan.FromSeconds(15));
        services.AddSingleton(sp => new JellyfinLoginService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("jellyfin-login"),
            sp.GetRequiredService<ILogger<JellyfinLoginService>>()));

        // Plex: token auth, file-path matching, server:// playlist uris.
        services.AddHttpClient(nameof(PlexSync));
        services.AddSingleton<ConfigPlexSettings>();
        services.AddSingleton<IPlexSettings, DbPlexSettings>();
        services.AddSingleton(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(PlexSync));
            return new PlexSync(http, sp.GetRequiredService<ILogger<PlexSync>>(),
                sp.GetRequiredService<IPlexSettings>());
        });
        services.AddSingleton<IMediaServerSync>(sp => sp.GetRequiredService<PlexSync>());
        services.AddSingleton<IMediaServerDirectory, PlexDirectory>();

        // Navidrome: Subsonic salted-md5 auth, search3 lookup, star favorites.
        services.AddHttpClient(nameof(NavidromeSync));
        services.AddSingleton<ConfigNavidromeSettings>();
        services.AddSingleton<INavidromeSettings, DbNavidromeSettings>();
        services.AddSingleton(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(NavidromeSync));
            return new NavidromeSync(http, sp.GetRequiredService<ILogger<NavidromeSync>>(),
                sp.GetRequiredService<INavidromeSettings>());
        });
        services.AddSingleton<IMediaServerSync>(sp => sp.GetRequiredService<NavidromeSync>());
        services.AddSingleton<IMediaServerDirectory, NavidromeDirectory>();
        return services;
    }
}
