using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Providers;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests;

/// <summary>
/// Integration-level tests that verify multiple components work together.
/// These test real interactions between Core models, Infrastructure providers,
/// and the registry system without external network calls or file I/O.
/// </summary>
[TestFixture]
public class IntegrationTests
{
    private IDbContextFactory<MelodyBridgeDbContext> CreateDbContextFactory()
    {
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"Integration_{Guid.NewGuid()}")
            .Options;

        return new InMemoryDbContextFactory(options);
    }

    private MelodyBridgeDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"IntegrationDb_{Guid.NewGuid()}")
            .Options;
        var db = new MelodyBridgeDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private class InMemoryDbContextFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }

    /// <summary>
    /// Tests the full registry lifecycle: register providers, verify defaults,
    /// toggle enable/disable, verify persistence.
    /// </summary>
    [Test]
    public async Task Registry_ProviderLifecycle()
    {
        var factory = CreateDbContextFactory();
        var providers = new IMusicProvider[]
        {
            new SquidWtfProvider(NullLogger<SquidWtfProvider>.Instance),
            new LucidaProvider(NullLogger<LucidaProvider>.Instance),
            new MonochromeProvider(NullLogger<MonochromeProvider>.Instance),
        };

        var registry = new MusicProviderRegistry(providers, factory,
            NullLogger<MusicProviderRegistry>.Instance);

        // All providers should be registered
        Assert.That(registry.GetAllProviders(), Has.Exactly(3).Items);

        // All should be enabled by default
        foreach (var p in providers)
            Assert.That(registry.IsProviderEnabled(p.Id), Is.True);

        // Disable one
        await registry.SetProviderEnabledAsync("squidwtf", false);
        Assert.That(registry.IsProviderEnabled("squidwtf"), Is.False);
        Assert.That(registry.GetEnabledProviders(), Has.Exactly(2).Items);

        // Re-enable
        await registry.SetProviderEnabledAsync("squidwtf", true);
        Assert.That(registry.IsProviderEnabled("squidwtf"), Is.True);
        Assert.That(registry.GetEnabledProviders(), Has.Exactly(3).Items);
    }

    /// <summary>
    /// Tests that the mapping from quality to platform/source works correctly
    /// and matches the provider capabilities.
    /// </summary>
    [Test]
    public void PlatformQualityMapping_MatchesProviderCapabilities()
    {
        // 320 MP3 should map to Soundcloud/Qobuz via SquidWtf
        var mappings = PlatformQualityMapper.GetPlatformsForQuality(new TrackQuality(320, MediaType.MP3));
        Assert.That(mappings, Has.Some.Matches<(Platform p, DownloadSource s)>(x =>
            x.p == Platform.Qobuz && x.s == DownloadSource.squidwtf));

        // 24 FLAC should map to Qobuz/Amazon/Tidal via SquidWtf
        mappings = PlatformQualityMapper.GetPlatformsForQuality(new TrackQuality(24, MediaType.FLAC));
        Assert.That(mappings, Has.Exactly(3).Items);

        // Verify SquidWtf provider actually supports these platforms
        var provider = new SquidWtfProvider(NullLogger<SquidWtfProvider>.Instance);
        Assert.That(provider.SupportedPlatforms, Has.Some.Matches<Platform>(p =>
            mappings.Any(m => m.platform == p)));
    }

    /// <summary>
    /// Tests that provider quality lists contain expected formats that
    /// match the platform quality mappings.
    /// </summary>
    [Test]
    public void ProviderQualities_AreConsistent()
    {
        // All FLAC qualities should have a mapping
        var flacQualities = ProviderQualities.SquidWtf
            .Where(q => q.Format == MediaType.FLAC)
            .ToList();

        foreach (var quality in flacQualities)
        {
            var mappings = PlatformQualityMapper.GetPlatformsForQuality(quality);
            Assert.That(mappings, Is.Not.Empty,
                $"FLAC quality {quality.Bitrate}/{quality.Format} should have at least one mapping");
        }
    }

    /// <summary>
    /// Tests that a provider's metadata matches its actual implementation.
    /// </summary>
    [Test]
    public void ProviderMetadata_MatchesActualCapabilities()
    {
        var provider = new DoubleDoubleProvider(NullLogger<DoubleDoubleProvider>.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(provider.Id, Is.Not.Null.And.Not.Empty);
            Assert.That(provider.Name, Is.Not.Null.And.Not.Empty);
            Assert.That(provider.Description, Is.Not.Null.And.Not.Empty);
            Assert.That(provider.SupportedPlatforms, Is.Not.Empty);
            Assert.That(provider.SupportedQualities, Is.Not.Empty);
        });

        // All supported platforms should be valid enum values
        foreach (var platform in provider.SupportedPlatforms)
            Assert.That(Enum.IsDefined(platform), Is.True,
                $"Platform {platform} should be a valid enum value");
    }

    /// <summary>
    /// Tests that the database context works with track entities.
    /// </summary>
    [Test]
    public async Task Database_TrackEntityLifecycle()
    {
        using var db = CreateDbContext();

        var track = new TrackEntity
        {
            MelodyId = "integration-test-id",
            Title = "Integration Track",
            Artist = "Test Artist",
            MediaType = ".flac",
            CurrentPath = "/music/test.flac",
        };

        db.Tracks.Add(track);
        await db.SaveChangesAsync();

        var loaded = await db.Tracks.FirstOrDefaultAsync(t => t.MelodyId == "integration-test-id");
        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.Title, Is.EqualTo("Integration Track"));
            Assert.That(loaded.Artist, Is.EqualTo("Test Artist"));
            Assert.That(loaded.CurrentPath, Is.EqualTo("/music/test.flac"));
        });
    }

    /// <summary>
    /// Tests that ProviderStateRow is correctly persisted.
    /// </summary>
    [Test]
    public async Task Database_ProviderStateLifecycle()
    {
        using var db = CreateDbContext();

        var state = new ProviderStateRow
        {
            ProviderId = "test-provider",
            IsEnabled = false,
        };

        db.ProviderStates.Add(state);
        await db.SaveChangesAsync();

        var loaded = await db.ProviderStates.FindAsync("test-provider");
        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded!.IsEnabled, Is.False);
        });

        // Update
        loaded.IsEnabled = true;
        await db.SaveChangesAsync();
        var reloaded = await db.ProviderStates.FindAsync("test-provider");
        Assert.That(reloaded!.IsEnabled, Is.True);
    }

    /// <summary>
    /// Tests the DownloadManager with real provider implementations
    /// (using mocked enable/disable via registry).
    /// </summary>
    [Test]
    public void DownloadManager_WithRegistry_Flow()
    {
        var factory = CreateDbContextFactory();
        var providers = new IMusicProvider[]
        {
            new SquidWtfProvider(NullLogger<SquidWtfProvider>.Instance),
        };

        var registry = new MusicProviderRegistry(providers, factory,
            NullLogger<MusicProviderRegistry>.Instance);

        var manager = new Application.Services.DownloadManager(
            Array.Empty<IAsyncDownloader>(),
            registry,
            NullLogger<Application.Services.DownloadManager>.Instance);

        // SquidWtf doesn't handle non-music URLs
        var result = manager.DownloadAsync(
            "https://example.com/not-music",
            "/tmp",
            "test").Result;

        Assert.That(result, Is.Null);
    }

    /// <summary>
    /// Tests that IMusicProvider implementations follow the contract.
    /// </summary>
    [Test]
    public async Task AllProviders_FollowContract()
    {
        var providers = new IMusicProvider[]
        {
            new SquidWtfProvider(NullLogger<SquidWtfProvider>.Instance),
            new LucidaProvider(NullLogger<LucidaProvider>.Instance),
            new DoubleDoubleProvider(NullLogger<DoubleDoubleProvider>.Instance),
            new MonochromeProvider(NullLogger<MonochromeProvider>.Instance),
        };

        foreach (var provider in providers)
        {
            Assert.Multiple(() =>
            {
                Assert.That(provider.Id, Is.Not.Null.And.Not.Empty, $"{provider.Name}: Id is required");
                Assert.That(provider.Name, Is.Not.Null.And.Not.Empty, $"{provider.Name}: Name is required");
                Assert.That(provider.Description, Is.Not.Null, $"{provider.Name}: Description is required");
                Assert.That(provider.Icon, Is.Not.Null, $"{provider.Name}: Icon is required");
                Assert.That(provider.SupportedPlatforms, Is.Not.Null, $"{provider.Name}: SupportedPlatforms is required");
                Assert.That(provider.SupportedQualities, Is.Not.Null, $"{provider.Name}: SupportedQualities is required");
            });

            // Search should return empty (not throw) on network failure
            var searchResult = await provider.SearchAsync("test search");
            Assert.That(searchResult, Is.Not.Null, $"{provider.Name}: SearchAsync should never return null");

            // GetTrackInfoAsync should return null (not throw) on invalid URL
            var trackInfo = await provider.GetTrackInfoAsync("https://example.com/invalid-url");
            Assert.That(trackInfo, Is.Null, $"{provider.Name}: GetTrackInfoAsync should return null for invalid URL");

            // DownloadAsync should return unsuccessful result (not throw) on invalid input
            var downloadResult = await provider.DownloadAsync("https://example.com/invalid-url",
                new TrackQuality(320, MediaType.MP3), "/tmp");
            Assert.That(downloadResult, Is.Not.Null, $"{provider.Name}: DownloadAsync should never return null");
            Assert.That(downloadResult.Success, Is.False, $"{provider.Name}: DownloadAsync should fail for invalid URL");
        }
    }
}
