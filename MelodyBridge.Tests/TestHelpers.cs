using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Tests;

/// <summary>Shared UI-test helpers: in-memory DB factory and registries.</summary>
public sealed class InMemDbFactory(DbContextOptions<MelodyBridgeDbContext> options) : IDbContextFactory<MelodyBridgeDbContext>
{
    public MelodyBridgeDbContext CreateDbContext() => new(options);

    public Task<MelodyBridgeDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(CreateDbContext());
}

public sealed class EmptyRegistry : IDownloaderRegistry
{
    public IReadOnlyList<IDownloader> GetAll() => Array.Empty<IDownloader>();
    public IDownloader? Get(string id) => null;
    public IReadOnlyList<IDownloader> GetEnabled() => Array.Empty<IDownloader>();
    public Task SetEnabledAsync(string id, bool enabled) => Task.CompletedTask;
    public bool IsEnabled(string id) => false;
    public Task<int> GetPriorityAsync(string id, CancellationToken ct = default) => Task.FromResult(0);
    public Task SetPriorityAsync(string id, int priority, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
}

public sealed class TestDownloader(string id, string name) : IDownloader
{
    public string Id => id;
    public string Name => name;
    public string Description => "test downloader";
    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<DownloaderSearchHit?> SearchAsync(string artist, string title, DownloadQuality quality, CancellationToken ct = default)
        => Task.FromResult<DownloaderSearchHit?>(null);

    public Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl, string outputDirectory, string? melodyId,
        DownloadQuality? quality = null, CancellationToken ct = default)
        => Task.FromResult(new DownloaderDownloadResult(false, null, "test"));
}

public static class TestHelpers
{
    public static InMemDbFactory CreateInMemFactory()
    {
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"mb-test-{Guid.NewGuid():N}")
            .Options;
        return new InMemDbFactory(options);
    }
}
