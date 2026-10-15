using MelodyBridge.Application;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The real application composition for download tests: the same
/// AddMelodyBridge() the server boots, over a private SQLite file. Tests
/// resolve the waterfall from here instead of assembling their own plugin
/// list, so what runs is exactly what every install runs. Changing the
/// waterfall from a test is only allowed the way a user does it: through
/// IDownloaderRegistry (enable/disable, order).
///
/// Every log line the app writes is captured (see Logs), so a failing
/// test can show what the plugins reported.
/// </summary>
public sealed class AppDownloadHost : IDisposable
{
    private readonly string _root;

    public ServiceProvider Services { get; }
    public IDbContextFactory<MelodyBridgeDbContext> DbFactory { get; }

    /// <summary>The app's own log lines, for failure diagnostics.</summary>
    public CollectingLoggerProvider Logs { get; }

    private AppDownloadHost(string root, ServiceProvider services,
        IDbContextFactory<MelodyBridgeDbContext> dbFactory, CollectingLoggerProvider logs)
    {
        _root = root;
        Services = services;
        DbFactory = dbFactory;
        Logs = logs;
    }

    /// <summary>The app-wide waterfall engine, with the shipped plugin set.</summary>
    public IDownloadManager DownloadManager => Services.GetRequiredService<IDownloadManager>();

    /// <summary>The persisted enable/order state of the waterfall.</summary>
    public IDownloaderRegistry Registry => Services.GetRequiredService<IDownloaderRegistry>();

    /// <summary>The real store the app downloads through, including the
    /// source providers and the ISRC resolver.</summary>
    public PlaylistStore Store => Services.GetRequiredService<PlaylistStore>();

    public static AppDownloadHost Create(string? dbPath = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"mb-apphost-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var database = dbPath ?? Path.Combine(root, "app.db");

        var logs = new CollectingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(logs));
        services.AddDbContextFactory<MelodyBridgeDbContext>(
            o => o.UseSqlite($"Data Source={database};Pooling=False"));
        services.AddMelodyBridge();

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using (var db = factory.CreateDbContext())
            db.Database.EnsureCreated();

        return new AppDownloadHost(root, provider, factory, logs);
    }

    public void Dispose()
    {
        Services.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
