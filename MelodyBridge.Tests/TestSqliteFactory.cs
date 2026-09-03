using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Tests;

/// <summary>
/// SQLite-backed DbContextFactory for tests: real database, real SQL,
/// unlike the InMemory provider. Each instance is its own file.
/// </summary>
public sealed class TestSqliteFactory(string dbPath) : IDbContextFactory<MelodyBridgeDbContext>
{
    public MelodyBridgeDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        return new MelodyBridgeDbContext(options);
    }

    public Task<MelodyBridgeDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(CreateDbContext());
}

/// <summary>Registry with no downloaders at all: the store accepts it happily.</summary>
public sealed class EmptyDownloaderRegistry : IDownloaderRegistry
{
    public IReadOnlyList<IDownloader> GetAll() => Array.Empty<IDownloader>();
    public IDownloader? Get(string id) => null;
    public IReadOnlyList<IDownloader> GetEnabled() => Array.Empty<IDownloader>();
    public Task SetEnabledAsync(string id, bool enabled) => Task.CompletedTask;
    public bool IsEnabled(string id) => false;
    public Task<int> GetPriorityAsync(string id, CancellationToken ct = default) => Task.FromResult(0);
    public Task SetPriorityAsync(string id, int priority, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> GetConfigAsync(string id, string key, CancellationToken ct = default) => Task.FromResult("");
    public Task SetConfigAsync(string id, string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Registry over a fixed list of downloaders, all enabled.</summary>
public sealed class ListDownloaderRegistry(params IDownloader[] downloaders) : IDownloaderRegistry
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
