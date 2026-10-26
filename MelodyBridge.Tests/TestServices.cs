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
        var changes = new DataChangeTracker();
        var store = new PlaylistStore(dbFactory,
            Array.Empty<ISourceProvider>(), manager,
            NullLogger<PlaylistStore>.Instance, changes: changes);
        services.AddSingleton<IDownloadManager>(manager);
        services.AddSingleton(registry);
        services.AddSingleton(changes);
        services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(dbFactory);
        services.AddSingleton(store);
        // The coordinator resolves the scoped store per run from the
        // provider the TestContext builds, like the real app does.
        services.AddSingleton<DownloadCoordinator>();
        services.AddSingleton<IDownloadCoordinator>(sp => sp.GetRequiredService<DownloadCoordinator>());
        var settingsStore = new SettingsStore(dbFactory);
        services.AddSingleton(settingsStore);
        services.AddSingleton(new MediaServerProfileStore(settingsStore));
        services.AddSingleton(new SyncRuleProfileStore(dbFactory));
        // The Advanced page injects the rename pass; its own tests drive it
        // through a real SQLite file like the library maintenance does.
        services.AddSingleton(new FileNameRenamer(
            dbFactory, settingsStore,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FileNameRenamer>.Instance));
        services.AddSingleton(new LibraryPathMover(dbFactory,
            NullLogger<LibraryPathMover>.Instance));
        services.AddSingleton(new DatabaseBackupService(dbFactory,
            new DatabaseRestore(),
            NullLogger<DatabaseBackupService>.Instance));
        services.AddSingleton(new MelodyBridge.Server.Services.NotificationService());
        services.AddUpdateService(dbFactory);
        // Advanced mode flips this singleton and the sidebar reads it. Tests
        // that need their own instance register it after this call.
        // Account connections: the pages inject the Spotify providers
        // directly, so the real (network-free until used) ones are
        // registered over the test database. Tests that need their own
        // AccountTokenStore register it after this call and win.
        var accountTokens = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(dbFactory,
            NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
        services.AddSingleton(accountTokens);
        services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.SpotifyUnofficialAccountProvider(
            accountTokens,
            NullLogger<MelodyBridge.Infrastructure.Accounts.SpotifyUnofficialAccountProvider>.Instance));
        // The Advanced page injects the ntfy notifier; tests that exercise
        // sending replace it with a recording subclass.
        services.AddSingleton(sp => new MelodyBridge.Server.Services.NtfyNotifier(
            settingsStore,
            sp.GetRequiredService<IHttpClientFactory>(),
            NullLogger<MelodyBridge.Server.Services.NtfyNotifier>.Instance));
        // The Plugins page reads the yt-dlp version; a stub keeps the page
        // tests from shelling out to a real binary.
        services.AddSingleton<MelodyBridge.Infrastructure.Downloaders.YtDlpUpdater>(
            new StubYtDlpUpdater());
        // The Settings and wizard pages sign in to Jellyfin; a stub keeps
        // them off the network.
        services.AddSingleton<MelodyBridge.Infrastructure.MediaServers.JellyfinLoginService>(
            new StubJellyfinLogin());
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
        // Kind-based media-server directory lookup: the components resolve
        // through the factory, which picks up whatever the fixture
        // registered as IMediaServerDirectory.
        services.AddSingleton<IMediaServerDirectoryFactory>(sp =>
            new MelodyBridge.Infrastructure.MediaServers.MediaServerDirectoryFactory(
                sp.GetServices<IMediaServerDirectory>()));
        // Real probe over the fixed settings; with no secrets it reports
        // everything as not configured and never touches the network.
        services.AddSingleton<IMediaServerStatusProbe>(sp =>
            new MelodyBridge.Infrastructure.MediaServers.MediaServerStatusProbe(
                sp.GetRequiredService<MediaServerProfileStore>(),
                sp.GetRequiredService<MelodyBridge.Infrastructure.MediaServers.IJellyfinSettings>(),
                sp.GetRequiredService<MelodyBridge.Infrastructure.MediaServers.IPlexSettings>(),
                sp.GetRequiredService<MelodyBridge.Infrastructure.MediaServers.INavidromeSettings>(),
                sp.GetRequiredService<IMediaServerDirectoryFactory>(),
                NullLogger<MelodyBridge.Infrastructure.MediaServers.MediaServerStatusProbe>.Instance));
    }

    /// <summary>
    /// Registers the update state the shell and Settings inject. Pages that
    /// already called AddDownloadPages have it; layout-only tests call this
    /// with their own database factory.
    /// </summary>
    public static void AddUpdateService(this IServiceCollection services,
        IDbContextFactory<MelodyBridgeDbContext> dbFactory)
    {
        if (!services.Any(d => d.ServiceType == typeof(SettingsStore)))
            services.AddSingleton(new SettingsStore(dbFactory));
        if (!services.Any(d => d.ServiceType == typeof(UpdateCheckService)))
            services.AddSingleton(new UpdateCheckService(new HttpClient()));
        if (!services.Any(d => d.ServiceType == typeof(MelodyBridge.Server.Services.NotificationService)))
            services.AddSingleton(new MelodyBridge.Server.Services.NotificationService());
        services.AddSingleton(sp => new MelodyBridge.Server.Services.UpdateService(
            sp.GetRequiredService<UpdateCheckService>(),
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<MelodyBridge.Server.Services.NotificationService>(),
            new MelodyBridge.Server.Services.InstallState(DatabaseExistedAtStartup: true),
            NullLogger<MelodyBridge.Server.Services.UpdateService>.Instance));
    }

    /// <summary>Plugins page stub: a fixed version and recorded updates.</summary>
    public sealed class StubYtDlpUpdater : MelodyBridge.Infrastructure.Downloaders.YtDlpUpdater
    {
        public string? Version { get; set; } = "2026.08.19";
        public MelodyBridge.Infrastructure.Downloaders.YtDlpUpdateResult NextResult { get; set; } =
            new(true, "Updated with python3 -m pip. Now on 2026.10.14.");
        public int UpdateCalls { get; private set; }

        public StubYtDlpUpdater()
            : base(NullLogger<MelodyBridge.Infrastructure.Downloaders.YtDlpUpdater>.Instance)
        {
        }

        public override Task<string?> GetVersionAsync(CancellationToken ct = default)
            => Task.FromResult(Version);

        public override Task<MelodyBridge.Infrastructure.Downloaders.YtDlpUpdateResult> UpdateAsync(
            CancellationToken ct = default)
        {
            UpdateCalls++;
            Version = "2026.10.14";
            return Task.FromResult(NextResult);
        }
    }

    /// <summary>Jellyfin sign-in stub for page tests: no network, fixed outcome.</summary>
    public sealed class StubJellyfinLogin : MelodyBridge.Infrastructure.MediaServers.JellyfinLoginService
    {
        public MelodyBridge.Infrastructure.MediaServers.JellyfinLoginResult NextResult { get; set; } =
            new(true, "session-token", "user-1", "mbadmin", null);
        public int Calls { get; private set; }

        public StubJellyfinLogin()
            : base(null!, NullLogger<MelodyBridge.Infrastructure.MediaServers.JellyfinLoginService>.Instance)
        {
        }

        public override Task<MelodyBridge.Infrastructure.MediaServers.JellyfinLoginResult> LoginAsync(
            string baseUrl, string username, string password, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(NextResult);
        }

        public MelodyBridge.Infrastructure.MediaServers.JellyfinQuickConnectStart NextQuickConnectStart { get; set; } =
            new(true, "secret-1", "ABCDEF", null);

        public MelodyBridge.Infrastructure.MediaServers.JellyfinLoginResult? NextQuickConnectFinish { get; set; } =
            new(true, "session-token", "user-1", "mbadmin", null);

        /// <summary>Report pending on the first poll, like a user still typing.</summary>
        public bool QuickConnectPendingFirst { get; set; }

        public int QuickConnectStarts { get; private set; }
        public int QuickConnectFinishes { get; private set; }

        public override Task<MelodyBridge.Infrastructure.MediaServers.JellyfinQuickConnectStart> StartQuickConnectAsync(
            string baseUrl, CancellationToken ct = default)
        {
            QuickConnectStarts++;
            return Task.FromResult(NextQuickConnectStart);
        }

        public override Task<MelodyBridge.Infrastructure.MediaServers.JellyfinLoginResult?> FinishQuickConnectAsync(
            string baseUrl, string secret, CancellationToken ct = default)
        {
            QuickConnectFinishes++;
            if (QuickConnectPendingFirst && QuickConnectFinishes == 1)
                return Task.FromResult<MelodyBridge.Infrastructure.MediaServers.JellyfinLoginResult?>(null);
            return Task.FromResult(NextQuickConnectFinish);
        }
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

    /// <summary>
    /// Registers the dashboard's media server probe with no directory behind
    /// it. The tests that use it never configure a server, so the probe only
    /// reports the three known kinds as not configured.
    /// </summary>
    public static void AddMediaServerProbe(this IServiceCollection services,
        IDbContextFactory<MelodyBridgeDbContext> dbFactory)
    {
        services.AddSingleton(new MelodyBridge.Infrastructure.Services.MediaServerProfileStore(
            new SettingsStore(dbFactory)));
        services.AddSingleton<MelodyBridge.Core.IMediaServerStatusProbe>(sp =>
            new MelodyBridge.Infrastructure.MediaServers.MediaServerStatusProbe(
                sp.GetRequiredService<MelodyBridge.Infrastructure.Services.MediaServerProfileStore>(),
                sp.GetRequiredService<MelodyBridge.Infrastructure.MediaServers.IJellyfinSettings>(),
                sp.GetRequiredService<MelodyBridge.Infrastructure.MediaServers.IPlexSettings>(),
                sp.GetRequiredService<MelodyBridge.Infrastructure.MediaServers.INavidromeSettings>(),
                new MelodyBridge.Infrastructure.MediaServers.MediaServerDirectoryFactory(
                    Array.Empty<MelodyBridge.Core.IMediaServerDirectory>()),
                Microsoft.Extensions.Logging.Abstractions
                    .NullLogger<MelodyBridge.Infrastructure.MediaServers.MediaServerStatusProbe>.Instance));
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

/// <summary>
/// A log buffer that writes one line while a snapshot is being taken: the
/// returned snapshot does not contain that line and the version has already
/// moved past it. That is the exact "line landed during the read" race the
/// pages must survive, without a sleep.
/// </summary>
public sealed class SnapshotThenWriteCollector : MelodyBridge.Core.Logging.ILogCollector
{
    private readonly MelodyBridge.Server.Services.LogCollector _inner = new();

    /// <summary>Write a line at the end of the next snapshot.</summary>
    public bool WriteOnNextSnapshot { get; set; }

    public string NextMessage { get; set; } = "a line that landed during the read";

    public long Version => _inner.Version;

    public long TotalCollected => _inner.TotalCollected;

    public int MaxEntries => _inner.MaxEntries;

    public int MaxProblemEntries => _inner.MaxProblemEntries;

    public void Log(MelodyBridge.Core.Logging.LogLevel level, string category, string message,
        string? detail = null, string? group = null, string? area = null)
        => _inner.Log(level, category, message, detail, group, area);

    public void Clear() => _inner.Clear();

    public IReadOnlyList<MelodyBridge.Core.Logging.LogEntry> GetExportEntries()
        => _inner.GetExportEntries();

    public IReadOnlyList<MelodyBridge.Core.Logging.LogEntry> GetEntries()
    {
        var snapshot = _inner.GetEntries();
        if (WriteOnNextSnapshot)
        {
            WriteOnNextSnapshot = false;
            _inner.Log(MelodyBridge.Core.Logging.LogLevel.Info, "Test", NextMessage);
        }
        return snapshot;
    }
}
