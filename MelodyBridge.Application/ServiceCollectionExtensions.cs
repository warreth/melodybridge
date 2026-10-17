using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Accounts;
using MelodyBridge.Infrastructure.Cloudflare;
using MelodyBridge.Infrastructure.Deezer;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.Jumo;
using MelodyBridge.Infrastructure.Lucida;
using MelodyBridge.Infrastructure.MediaServers;
using MelodyBridge.Infrastructure.Playlists;
using MelodyBridge.Infrastructure.Qobuz;
using MelodyBridge.Infrastructure.Scanning;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Tidal;
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
        // The default order: the fast free sources first (SoundCloud, the
        // Internet Archive), YouTube as the widest lossy fallback, then the
        // subscription sources (Qobuz, Deezer), the free lossless ones
        // (Monochrome, Soulseek), and the community mirrors last (Lucida
        // needs a solver; DoubleDouble reports itself unavailable while its
        // captcha gate is up).
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

        // Qobuz and Deezer run through the user's own tools (qobuz-dlp,
        // deemix): the tools own the login and the download, the app only
        // searches. Both stay unavailable until the tools and credentials
        // are configured.
        services.AddSingleton<QobuzClient>(sp =>
            new QobuzClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("qobuz"),
                sp.GetRequiredService<ILogger<QobuzClient>>()));
        services.AddHttpClient("qobuz", c =>
        {
            // The web player bundle fetch expects a browser-ish client.
            c.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:83.0) Gecko/20100101 Firefox/83.0");
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<IDownloader>(sp =>
            new QobuzDownloader(
                sp.GetRequiredService<QobuzClient>(),
                sp.GetRequiredService<IPluginConfig>(),
                sp.GetRequiredService<ILogger<QobuzDownloader>>()));

        services.AddSingleton<DeezerClient>(sp =>
            new DeezerClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("deezer"),
                sp.GetRequiredService<ILogger<DeezerClient>>()));
        services.AddSingleton<IDownloader>(sp =>
            new DeemixDownloader(
                sp.GetRequiredService<DeezerClient>(),
                sp.GetRequiredService<IPluginConfig>(),
                sp.GetRequiredService<ILogger<DeemixDownloader>>()));

        // Tidal through the user's own tiddl: the tool owns the login
        // (tiddl auth login), the plugin searches with that same session
        // and hands the track URL over.
        services.AddHttpClient("tidal", c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("MelodyBridge/1.0 (+https://melodybridge.app)");
            c.Timeout = TimeSpan.FromSeconds(20);
        });
        services.AddSingleton<TidalClient>(sp =>
            new TidalClient(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("tidal"),
                sp.GetRequiredService<ILogger<TidalClient>>()));
        services.AddSingleton<IDownloader>(sp =>
            new TidalDownloader(
                sp.GetRequiredService<TidalClient>(),
                sp.GetRequiredService<IPluginConfig>(),
                sp.GetRequiredService<ILogger<TidalDownloader>>()));

        // Monochrome streams lossless FLAC from its official API; the
        // client allows long transfers, the plugin caps each call itself.
        services.AddHttpClient("monochrome", c => c.Timeout = TimeSpan.FromMinutes(10));
        services.AddSingleton<IDownloader>(sp =>
            new MonochromeDownloader(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("monochrome"),
                sp.GetRequiredService<IPluginConfig>(),
                sp.GetRequiredService<ILogger<MonochromeDownloader>>()));

        // Soulseek through the user's own slskd instance: lossless and
        // high-bitrate files, no cloud dependency. Unavailable (and
        // skipped) until the URL and API key are set and slskd is logged
        // in; the downloads folder is read from slskd's own options.
        services.AddHttpClient("slskd", c => c.Timeout = TimeSpan.FromMinutes(2));
        services.AddSingleton<IDownloader>(sp =>
            new SlskdDownloader(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("slskd"),
                sp.GetRequiredService<IPluginConfig>(),
                sp.GetRequiredService<ILogger<SlskdDownloader>>()));

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

        // Jumo: a community Qobuz front-end that needs no account. The
        // plugin talks its guarded API for search and stream descriptors,
        // then pulls the audio straight from the signed Qobuz CDN URLs and
        // decrypts locally.
        services.AddHttpClient("jumo", c => c.Timeout = TimeSpan.FromMinutes(3));
        services.AddSingleton<IDownloader>(sp =>
            new JumoDownloader(
                new JumoClient(
                    sp.GetRequiredService<IHttpClientFactory>().CreateClient("jumo"),
                    sp.GetRequiredService<ILogger<JumoClient>>()),
                sp.GetRequiredService<IPluginConfig>(),
                sp.GetRequiredService<ILogger<JumoDownloader>>()));

        // DoubleDouble rips many services by direct URL (submit + poll, no
        // metadata search: the frontend search is captcha-gated). It sits
        // last because the site is captcha-gated on every submission and
        // the plugin reports itself unavailable; it stays registered so a
        // working status is picked up automatically if the gate ever drops.
        services.AddHttpClient("doubledouble", c => c.Timeout = TimeSpan.FromMinutes(3));
        services.AddSingleton<IDownloader>(sp =>
            new DoubleDoubleDownloader(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("doubledouble"),
                sp.GetRequiredService<ILogger<DoubleDoubleDownloader>>()));

        // ISRC enrichment for sources that do not provide one (Spotify
        // embed scrape, YouTube, CSV imports): Deezer's public search API
        // needs no account and returns the ISRC of every hit.
        services.AddHttpClient("deezer", c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("MelodyBridge/1.0 (+https://melodybridge.app)");
            c.Timeout = TimeSpan.FromSeconds(15);
        });
        // ListenBrainz asks clients to identify themselves too.
        services.AddHttpClient("listenbrainz", c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("MelodyBridge/1.0 (+https://melodybridge.app)");
            c.Timeout = TimeSpan.FromSeconds(20);
        });
        // MusicBrainz is the ISRC fallback when Deezer has no confident
        // match: it asks for a real User-Agent and at most one request per
        // second, both handled by the resolver.
        services.AddHttpClient("musicbrainz", c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("MelodyBridge/1.0 (+https://melodybridge.app)");
            c.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddSingleton(sp => new IsrcResolver(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("deezer"),
            sp.GetRequiredService<ILogger<IsrcResolver>>(),
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("musicbrainz")));

        // Download metadata: the cover art on a source payload and the
        // lyrics from LRCLIB. Both are best effort enrichment; a missing
        // answer never fails a download.
        services.AddHttpClient("coverart", c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("MelodyBridge/1.0 (+https://melodybridge.app)");
            c.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddSingleton(sp => new CoverArtFetcher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("coverart"),
            sp.GetRequiredService<ILogger<CoverArtFetcher>>()));
        services.AddHttpClient("lrclib", c =>
        {
            // LRCLIB asks clients to identify themselves.
            c.DefaultRequestHeaders.UserAgent.ParseAdd("MelodyBridge/1.0 (+https://melodybridge.app)");
            c.Timeout = TimeSpan.FromSeconds(15);
        });
        services.AddSingleton(sp => new LyricsResolver(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("lrclib"),
            sp.GetRequiredService<ILogger<LyricsResolver>>()));

        services.AddSingleton<IPluginConfig, PluginConfigStore>();
        services.AddSingleton<IDownloaderRegistry, DownloaderRegistry>();

        // Version read and update button for the shared yt-dlp binary
        // (YouTube and SoundCloud plugins) on the Plugins page.
        services.AddSingleton(sp => new YtDlpUpdater(
            sp.GetRequiredService<ILogger<YtDlpUpdater>>()));

        // Source providers (playlists)
        services.AddSingleton<ISourceProvider, YouTubeSourceProvider>();
        services.AddSingleton<ISourceProvider, SpotifySourceProvider>();
        // Public playlist sources: Deezer, SoundCloud sets and
        // ListenBrainz need no account.
        services.AddSingleton<ISourceProvider>(sp =>
            new DeezerSourceProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("deezer"),
                sp.GetRequiredService<ILogger<DeezerSourceProvider>>()));
        services.AddSingleton<ISourceProvider>(sp =>
            new SoundCloudSourceProvider(sp.GetRequiredService<ILogger<SoundCloudSourceProvider>>()));
        services.AddSingleton<ISourceProvider>(sp =>
            new ListenBrainzSourceProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("listenbrainz"),
                sp.GetRequiredService<ILogger<ListenBrainzSourceProvider>>()));

        // Application services. Singleton: the download progress map and
        // the coordinator's run handles must survive page navigations
        // (that is why the progress bar used to vanish between pages).
        // Rolling health per plugin: the waterfall skips providers whose
        // recent attempts kept failing and demotes the shaky ones.
        services.AddSingleton<ProviderHealth>();
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

        // Subsonic (Navidrome, gonic, Airsonic, Ampache, Funkwhale and
        // Nextcloud Music all speak this API): salted-md5 auth, search3
        // lookup, star favorites. The settings keep their historical
        // navidrome_* keys so existing installs stay configured.
        services.AddHttpClient(nameof(SubsonicSync));
        services.AddSingleton<ConfigNavidromeSettings>();
        services.AddSingleton<INavidromeSettings, DbNavidromeSettings>();
        services.AddSingleton(sp =>
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(SubsonicSync));
            return new SubsonicSync(http, sp.GetRequiredService<ILogger<SubsonicSync>>(),
                sp.GetRequiredService<INavidromeSettings>());
        });
        services.AddSingleton<IMediaServerSync>(sp => sp.GetRequiredService<SubsonicSync>());
        services.AddSingleton<IMediaServerDirectory, SubsonicDirectory>();

        // Kind-based directory lookup: three directories are registered and
        // a single IMediaServerDirectory resolve would silently return the
        // last one, so every kind-based lookup goes through this factory.
        services.AddSingleton<IMediaServerDirectoryFactory>(sp =>
            new MediaServerDirectoryFactory(sp.GetServices<IMediaServerDirectory>()));
        return services;
    }
}
