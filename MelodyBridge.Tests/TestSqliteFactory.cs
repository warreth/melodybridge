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
