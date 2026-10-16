using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Honest tests for the waterfall order persistence: a real SQLite
/// database, real registry, real plugin state rows. No mocks on the
/// persistence path. The plugins here are neutral stubs on purpose: what
/// the shipped waterfall looks like is pinned by DownloaderCompositionTests
/// over the real app composition, so this fixture only proves the registry
/// mechanics.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class DownloaderRegistryOrderTests
{
    private IDbContextFactory<MelodyBridgeDbContext> _dbFactory = null!;
    private DownloaderRegistry _registry = null!;

    private static IDownloader[] Plugins() => new IDownloader[]
    {
        new StubDownloader("alpha", "Alpha"),
        new StubDownloader("beta", "Beta"),
        new StubDownloader("gamma", "Gamma"),
    };

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseSqlite($"Data Source=file:memdb{Guid.NewGuid():N}?mode=memory&cache=shared")
            .Options;

        _dbFactory = new InlineFactory(options);
        using var db = _dbFactory.CreateDbContext();
        db.Database.EnsureCreated();

        _registry = new DownloaderRegistry(Plugins(), _dbFactory,
            new PluginConfigStore(_dbFactory),
            NullLogger<DownloaderRegistry>.Instance);
    }

    [Test]
    public void GetEnabled_InitialOrder_FollowsRegistration()
    {
        var ids = _registry.GetEnabled().Select(d => d.Id).ToList();
        Assert.That(ids, Is.EqualTo(new[] { "alpha", "beta", "gamma" }));
    }

    [Test]
    public async Task SetOrderAsync_ReordersWaterfallAndPersists()
    {
        await _registry.SetOrderAsync(new[] { "gamma", "alpha", "beta" });

        var ids = _registry.GetEnabled().Select(d => d.Id).ToList();
        Assert.That(ids, Is.EqualTo(new[] { "gamma", "alpha", "beta" }),
            "SetOrderAsync must define the exact waterfall order");

        // Persisted: a fresh registry over the same DB reads the same order.
        var fresh = new DownloaderRegistry(Plugins(), _dbFactory,
            new PluginConfigStore(_dbFactory),
            NullLogger<DownloaderRegistry>.Instance);
        var persistedIds = fresh.GetEnabled().Select(d => d.Id).ToList();
        Assert.That(persistedIds, Is.EqualTo(new[] { "gamma", "alpha", "beta" }),
            "the order must survive a restart");
    }

    [Test]
    public async Task MoveUpSimulation_SwappingNeighborsKeepsDensePriorities()
    {
        // The exact operation the Downloads page arrows perform.
        var order = _registry.GetEnabled().Select(d => d.Id).ToList();
        (order[1], order[2]) = (order[2], order[1]); // gamma moves up above beta
        await _registry.SetOrderAsync(order);

        var ids = _registry.GetEnabled().Select(d => d.Id).ToList();
        Assert.That(ids, Is.EqualTo(new[] { "alpha", "gamma", "beta" }));

        // Priorities are dense 0..2, never drifting apart.
        var priorities = new List<int>();
        foreach (var id in ids)
            priorities.Add(await _registry.GetPriorityAsync(id));
        Assert.That(priorities, Is.EqualTo(new[] { 0, 1, 2 }));
    }

    [Test]
    public async Task SetOrderAsync_UnknownIdsAreAppended()
    {
        await _registry.SetOrderAsync(new[] { "gamma", "nonexistent-plugin" });

        var ids = _registry.GetEnabled().Select(d => d.Id).ToList();
        Assert.That(ids[0], Is.EqualTo("gamma"));
        Assert.That(ids, Does.Not.Contain("nonexistent-plugin"),
            "unknown ids must not appear in the waterfall");
    }

    [Test]
    public async Task ConfigValues_PersistAndReload()
    {
        await _registry.SetConfigAsync("gamma", "region", "eu");
        Assert.That(await _registry.GetConfigAsync("gamma", "region"), Is.EqualTo("eu"));

        // Unset keys come back empty, not null: the UI binds them to inputs.
        Assert.That(await _registry.GetConfigAsync("alpha", "region"), Is.EqualTo(""));

        // A fresh registry over the same DB reads the same value: persisted.
        var fresh = new DownloaderRegistry(
            new IDownloader[] { new StubDownloader("alpha", "Alpha") },
            _dbFactory, new PluginConfigStore(_dbFactory), NullLogger<DownloaderRegistry>.Instance);
        Assert.That(await fresh.GetConfigAsync("gamma", "region"), Is.EqualTo("eu"),
            "plugin config must survive a restart");

        // Overwrite works and does not leak into other plugins.
        await _registry.SetConfigAsync("gamma", "region", "us");
        Assert.That(await _registry.GetConfigAsync("gamma", "region"), Is.EqualTo("us"));
        Assert.That(await _registry.GetConfigAsync("alpha", "region"), Is.EqualTo(""));
    }

    /// <summary>The real plugin ids, in the new shipped order.</summary>
    private static IDownloader[] NewShippedPlugins() => new IDownloader[]
    {
        new StubDownloader("soundcloud", "SoundCloud"),
        new StubDownloader("archiveorg", "Internet Archive"),
        new StubDownloader("ytdlp", "yt-dlp"),
        new StubDownloader("qobuz", "Qobuz"),
        new StubDownloader("deemix", "Deezer (deemix)"),
        new StubDownloader("monochrome", "Monochrome"),
        new StubDownloader("slskd", "Soulseek"),
        new StubDownloader("doubledouble", "DoubleDouble"),
        new StubDownloader("lucida", "Lucida"),
    };

    private DownloaderRegistry RegistryOver(params IDownloader[] plugins)
        => new(plugins, _dbFactory, new PluginConfigStore(_dbFactory),
            NullLogger<DownloaderRegistry>.Instance);

    private async Task SeedOrderAsync(params string[] ids)
    {
        await using var db = _dbFactory.CreateDbContext();
        for (var i = 0; i < ids.Length; i++)
            db.ProviderStates.Add(new ProviderStateRow
            {
                ProviderId = ids[i],
                IsEnabled = true,
                Priority = i,
            });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task UntouchedOldDefaultOrder_MigratesToTheNewDefault()
    {
        // An existing install that never reordered: the DAB Music era
        // order sits in the database, seeded directly the way the old
        // version left it.
        await SeedOrderAsync(
            "soundcloud", "archiveorg", "ytdlp", "lucida", "monochrome", "doubledouble", "dabmusic");

        // The next start, with the new plugin set, moves it to the new
        // default instead of leaving Lucida in front of everything.
        var fresh = RegistryOver(NewShippedPlugins());
        Assert.That(fresh.GetEnabled().Select(d => d.Id), Is.EqualTo(new[]
        {
            "soundcloud", "archiveorg", "ytdlp", "qobuz", "deemix",
            "monochrome", "slskd", "lucida", "doubledouble",
        }), "an untouched default order follows the new default; the retired plugin trails");
    }

    [Test]
    public async Task UntouchedCleanupEraOrder_AlsoMigrates()
    {
        // The order the cleanup commit shipped, before Qobuz and Deezer.
        await SeedOrderAsync(
            "soundcloud", "archiveorg", "ytdlp", "monochrome", "slskd", "doubledouble", "lucida");

        var fresh = RegistryOver(NewShippedPlugins());
        Assert.That(fresh.GetEnabled().Select(d => d.Id), Is.EqualTo(new[]
        {
            "soundcloud", "archiveorg", "ytdlp", "qobuz", "deemix",
            "monochrome", "slskd", "lucida", "doubledouble",
        }), "a shipped default from before the premium plugins also migrates");
    }

    [Test]
    public async Task UntouchedDoubleDoubleEraOrder_MigratesToTheNewDefault()
    {
        // The order shipped just before DoubleDouble turned captcha-gated.
        // It must be recognized as untouched so the retired plugin drops
        // behind the working sources instead of running in the middle.
        await SeedOrderAsync(
            "soundcloud", "archiveorg", "ytdlp", "qobuz", "deemix",
            "monochrome", "slskd", "doubledouble", "lucida");

        var fresh = RegistryOver(NewShippedPlugins());
        Assert.That(fresh.GetEnabled().Select(d => d.Id), Is.EqualTo(new[]
        {
            "soundcloud", "archiveorg", "ytdlp", "qobuz", "deemix",
            "monochrome", "slskd", "lucida", "doubledouble",
        }), "the last shipped order migrates; a user's own order would not");
    }

    [Test]
    public async Task CustomOrder_IsNeverRewrittenByTheMigration()
    {
        // A user's own order, seeded the way the old version left it.
        await SeedOrderAsync(
            "lucida", "soundcloud", "archiveorg", "ytdlp", "monochrome", "doubledouble", "dabmusic");

        var fresh = RegistryOver(NewShippedPlugins());
        Assert.That(fresh.GetEnabled().Select(d => d.Id), Is.EqualTo(new[]
        {
            "lucida", "soundcloud", "archiveorg", "ytdlp", "monochrome", "doubledouble",
            "qobuz", "deemix", "slskd",
        }), "a user's own order is left alone; the new plugins ride at the end");
    }

    private sealed class InlineFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InlineFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }

    private sealed class StubDownloader : IDownloader
    {
        public StubDownloader(string id, string name) { Id = id; Name = name; }
        public string Id { get; }
        public string Name { get; }
        public string Description => string.Empty;
        public PluginCapabilities Capabilities => PluginCapabilities.Any;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(null);
        public Task<DownloaderDownloadResult> DownloadAsync(string sourceUrl, string outputDirectory, string? melodyId, DownloadQuality? quality = null, CancellationToken ct = default)
            => Task.FromResult(new DownloaderDownloadResult(false, null, "stub"));
    }
}
