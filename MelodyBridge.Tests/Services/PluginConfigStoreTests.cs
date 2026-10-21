using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Plugin configuration against a real SQLite file: one snapshot read
/// serves every field, saves update it, and a fresh store sees persisted
/// rows. The counting factory makes the query count visible instead of
/// trusting a timer.
/// </summary>
[TestFixture]
public class PluginConfigStoreTests
{
    private string _dbPath = null!;
    private TestSqliteFactory _db = null!;

    [SetUp]
    public void SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-plugin-config-{Guid.NewGuid():N}.db");
        _db = new TestSqliteFactory(_dbPath);
        using var db = _db.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
    }

    private sealed class CountingFactory(IDbContextFactory<MelodyBridgeDbContext> inner)
        : IDbContextFactory<MelodyBridgeDbContext>
    {
        public int Contexts { get; private set; }

        public MelodyBridgeDbContext CreateDbContext()
        {
            Contexts++;
            return inner.CreateDbContext();
        }

        public Task<MelodyBridgeDbContext> CreateDbContextAsync(CancellationToken ct = default)
        {
            Contexts++;
            return inner.CreateDbContextAsync(ct);
        }
    }

    [Test]
    public async Task ReadingManyFields_LoadsThePluginSnapshotOnce()
    {
        var counter = new CountingFactory(_db);
        var store = new PluginConfigStore(counter);

        Assert.That(await store.GetAsync("alpha", "region"), Is.EqualTo(""));
        Assert.That(await store.GetAsync("beta", "token"), Is.EqualTo(""));
        Assert.That(counter.Contexts, Is.EqualTo(1),
            "the first read loads every plugin row; later fields come from memory");
    }

    [Test]
    public async Task SetAsync_UpdatesTheCacheWithoutAnotherRead()
    {
        var counter = new CountingFactory(_db);
        var store = new PluginConfigStore(counter);

        Assert.That(await store.GetAsync("gamma", "region"), Is.EqualTo(""));
        await store.SetAsync("gamma", "region", "eu");
        var contextsAfterSave = counter.Contexts;

        Assert.That(await store.GetAsync("gamma", "region"), Is.EqualTo("eu"));
        Assert.That(counter.Contexts, Is.EqualTo(contextsAfterSave),
            "the saved value must be served from the updated snapshot");
    }

    [Test]
    public async Task Clear_RemovesTheCachedValue()
    {
        var store = new PluginConfigStore(_db);
        await store.SetAsync("gamma", "region", "eu");
        await store.SetAsync("gamma", "region", "");

        Assert.That(await store.GetAsync("gamma", "region"), Is.EqualTo(""));
    }

    [Test]
    public async Task SavedValue_IsVisibleToAFreshStore()
    {
        await new PluginConfigStore(_db).SetAsync("gamma", "region", "eu");

        var reader = new PluginConfigStore(_db);
        Assert.That(await reader.GetAsync("gamma", "region"), Is.EqualTo("eu"),
            "the write must reach the database, not only the cache");
    }

    [Test]
    public async Task EmptyRows_AreTreatedAsUnset()
    {
        using (var db = _db.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity
            {
                Key = "plugin:alpha:region",
                Value = "",
            });
            await db.SaveChangesAsync();
        }

        Assert.That(await new PluginConfigStore(_db).GetAsync("alpha", "region"), Is.EqualTo(""));
    }
}
