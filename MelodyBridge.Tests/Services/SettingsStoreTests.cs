using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// SettingsStore against a real SQLite file database (the production store).
/// Round-trips, defaults and boolean parsing.
/// </summary>
[TestFixture]
public class SettingsStoreTests
{
    private static string NewDbPath([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), $"mb-settings-{test}-{Guid.NewGuid():N}.db");

    private static async Task<SettingsStore> NewStoreAsync(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<MelodyBridgeDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        await using (var db = factory.CreateDbContext())
            await db.Database.EnsureCreatedAsync();
        return new SettingsStore(factory);
    }

    [Test]
    public async Task GetAsync_MissingKey_ReturnsFallback()
    {
        var store = await NewStoreAsync(NewDbPath());
        Assert.That(await store.GetAsync("no-such-key", "fallback"), Is.EqualTo("fallback"));
    }

    [Test]
    public async Task SetAsync_ThenGet_RoundTrips()
    {
        var dbPath = NewDbPath();
        try
        {
            var store = await NewStoreAsync(dbPath);
            await store.SetAsync("intro_done", "true");

            // Fresh store, same database: reads the persisted row, not memory.
            var reader = await NewStoreAsync(dbPath);
            Assert.That(await reader.GetAsync("intro_done", "no"), Is.EqualTo("true"));
        }
        finally { TryDelete(dbPath); }
    }

    [Test]
    public async Task SetAsync_Twice_UpdatesInsteadOfDuplicating()
    {
        var dbPath = NewDbPath();
        try
        {
            var store = await NewStoreAsync(dbPath);
            await store.SetAsync("k", "one");
            await store.SetAsync("k", "two");

            var services = new ServiceCollection();
            services.AddDbContextFactory<MelodyBridgeDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
            var sp = services.BuildServiceProvider();
            var factory = sp.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
            await using var db = factory.CreateDbContext();
            Assert.That(db.DownloaderSettings.Count(s => s.Key == "k"), Is.EqualTo(1));
            Assert.That((await db.DownloaderSettings.FirstAsync(s => s.Key == "k")).Value, Is.EqualTo("two"));
        }
        finally { TryDelete(dbPath); }
    }

    [Test]
    public async Task GetBoolAsync_ParsesTrueOneAndGarbage()
    {
        var dbPath = NewDbPath();
        try
        {
            var store = await NewStoreAsync(dbPath);
            await store.SetAsync("a", "true");
            await store.SetAsync("b", "1");
            await store.SetAsync("c", "whatever");
            await store.SetAsync("d", "false");

            Assert.That(await store.GetBoolAsync("a"), Is.True);
            Assert.That(await store.GetBoolAsync("b"), Is.True);
            Assert.That(await store.GetBoolAsync("c"), Is.False, "unrecognized text counts as off");
            Assert.That(await store.GetBoolAsync("d"), Is.False);
            Assert.That(await store.GetBoolAsync("missing", fallback: true), Is.True, "missing key uses fallback");
        }
        finally { TryDelete(dbPath); }
    }

    private static void TryDelete(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(dbPath + suffix); } catch { /* best effort */ }
        }
    }
}
