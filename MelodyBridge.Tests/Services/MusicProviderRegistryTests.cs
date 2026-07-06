using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

[TestFixture]
public class MusicProviderRegistryTests
{
    private class TestProvider : IMusicProvider
    {
        public string Id { get; }
        public string Name => $"Test {Id}";
        public string Description => $"Test provider {Id}";
        public string Icon => "🧪";
        public IReadOnlyList<Platform> SupportedPlatforms => new[] { Platform.Qobuz };
        public IReadOnlyList<TrackQuality> SupportedQualities => Array.Empty<TrackQuality>();

        public TestProvider(string id) => Id = id;

        public Task<IReadOnlyList<SearchResult>> SearchAsync(string query, Platform? platform = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());

        public Task<TrackInfo?> GetTrackInfoAsync(string url, CancellationToken ct = default)
            => Task.FromResult<TrackInfo?>(null);

        public Task<DownloadResult> DownloadAsync(string trackUrl, TrackQuality quality, string outputDirectory, CancellationToken ct = default)
            => Task.FromResult(new DownloadResult(false, null, "not implemented", null));
    }

    private IDbContextFactory<MelodyBridgeDbContext> CreateDbContextFactory()
    {
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"TestDb_{Guid.NewGuid()}")
            .Options;

        // Create factory manually
        var factory = new MelodyBridgeInMemoryDbContextFactory(options);
        // Ensure DB is created
        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
        return factory;
    }

    /// <summary>
    /// Simple IDbContextFactory that uses InMemory database options.
    /// </summary>
    private class MelodyBridgeInMemoryDbContextFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public MelodyBridgeInMemoryDbContextFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }

    [Test]
    public void GetAllProviders_ReturnsAllRegisteredProviders()
    {
        var providers = new IMusicProvider[]
        {
            new TestProvider("prov-a"),
            new TestProvider("prov-b"),
            new TestProvider("prov-c"),
        };

        var registry = new MusicProviderRegistry(providers, CreateDbContextFactory(), NullLogger<MusicProviderRegistry>.Instance);

        var all = registry.GetAllProviders();
        Assert.That(all, Has.Exactly(3).Items);
    }

    [Test]
    public void GetProvider_ExistingId_ReturnsProvider()
    {
        var providers = new IMusicProvider[] { new TestProvider("my-provider") };
        var registry = new MusicProviderRegistry(providers, CreateDbContextFactory(), NullLogger<MusicProviderRegistry>.Instance);

        var found = registry.GetProvider("my-provider");
        Assert.That(found, Is.Not.Null);
        Assert.That(found!.Id, Is.EqualTo("my-provider"));
    }

    [Test]
    public void GetProvider_UnknownId_ReturnsNull()
    {
        var providers = new IMusicProvider[] { new TestProvider("existing") };
        var registry = new MusicProviderRegistry(providers, CreateDbContextFactory(), NullLogger<MusicProviderRegistry>.Instance);

        var found = registry.GetProvider("non-existent");
        Assert.That(found, Is.Null);
    }

    [Test]
    public void GetProvider_CaseInsensitive()
    {
        var providers = new IMusicProvider[] { new TestProvider("MyProvider") };
        var registry = new MusicProviderRegistry(providers, CreateDbContextFactory(), NullLogger<MusicProviderRegistry>.Instance);

        var found = registry.GetProvider("myprovider");
        Assert.That(found, Is.Not.Null);
    }

    [Test]
    public void NewProviders_DefaultToEnabled()
    {
        var providers = new IMusicProvider[] { new TestProvider("new-prov") };
        var registry = new MusicProviderRegistry(providers, CreateDbContextFactory(), NullLogger<MusicProviderRegistry>.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(registry.IsProviderEnabled("new-prov"), Is.True);
            Assert.That(registry.GetEnabledProviders(), Has.Exactly(1).Items);
        });
    }

    [Test]
    public async Task SetProviderEnabledAsync_DisablesProvider()
    {
        var providers = new IMusicProvider[] { new TestProvider("toggle-me") };
        var registry = new MusicProviderRegistry(providers, CreateDbContextFactory(), NullLogger<MusicProviderRegistry>.Instance);

        Assert.That(registry.IsProviderEnabled("toggle-me"), Is.True, "Should start enabled");

        await registry.SetProviderEnabledAsync("toggle-me", false);

        Assert.Multiple(() =>
        {
            Assert.That(registry.IsProviderEnabled("toggle-me"), Is.False);
            Assert.That(registry.GetEnabledProviders(), Is.Empty);
        });
    }

    [Test]
    public async Task SetProviderEnabledAsync_ReEnableProvider()
    {
        var providers = new IMusicProvider[] { new TestProvider("re-enable") };
        var registry = new MusicProviderRegistry(providers, CreateDbContextFactory(), NullLogger<MusicProviderRegistry>.Instance);

        await registry.SetProviderEnabledAsync("re-enable", false);
        Assert.That(registry.IsProviderEnabled("re-enable"), Is.False);

        await registry.SetProviderEnabledAsync("re-enable", true);
        Assert.Multiple(() =>
        {
            Assert.That(registry.IsProviderEnabled("re-enable"), Is.True);
            Assert.That(registry.GetEnabledProviders(), Has.Exactly(1).Items);
        });
    }

    [Test]
    public async Task SetProviderEnabledAsync_PersistsAcrossNewRegistryInstance()
    {
        var factory = CreateDbContextFactory();
        var providers = new IMusicProvider[] { new TestProvider("persist-me") };

        // First instance: disable
        var registry1 = new MusicProviderRegistry(providers, factory, NullLogger<MusicProviderRegistry>.Instance);
        await registry1.SetProviderEnabledAsync("persist-me", false);

        // Second instance with same factory: should read disabled state
        var registry2 = new MusicProviderRegistry(providers, factory, NullLogger<MusicProviderRegistry>.Instance);
        Assert.That(registry2.IsProviderEnabled("persist-me"), Is.False);
        Assert.That(registry2.GetEnabledProviders(), Is.Empty);
    }

    [Test]
    public async Task MultipleProviders_OnlyEnabledReturned()
    {
        var providers = new IMusicProvider[]
        {
            new TestProvider("enabled-1"),
            new TestProvider("disabled-1"),
            new TestProvider("enabled-2"),
        };

        var registry = new MusicProviderRegistry(providers, CreateDbContextFactory(), NullLogger<MusicProviderRegistry>.Instance);
        await registry.SetProviderEnabledAsync("disabled-1", false);

        var enabled = registry.GetEnabledProviders();
        Assert.Multiple(() =>
        {
            Assert.That(enabled, Has.Exactly(2).Items);
            Assert.That(enabled.Select(p => p.Id), Is.EquivalentTo(new[] { "enabled-1", "enabled-2" }));
        });
    }

    [Test]
    public void EmptyProviderList_ReturnsEmpty()
    {
        var registry = new MusicProviderRegistry(
            Array.Empty<IMusicProvider>(),
            CreateDbContextFactory(),
            NullLogger<MusicProviderRegistry>.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(registry.GetAllProviders(), Is.Empty);
            Assert.That(registry.GetEnabledProviders(), Is.Empty);
            Assert.That(registry.GetProvider("anything"), Is.Null);
        });
    }
}
