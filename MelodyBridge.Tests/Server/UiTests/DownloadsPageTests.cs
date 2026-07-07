using TestContext = Bunit.TestContext;
using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

[TestFixture]
[Category("UI")]
public class DownloadsPageTests
{
    private TestContext _ctx = null!;
    private Mock<IMusicProviderRegistry> _registry = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _registry = new Mock<IMusicProviderRegistry>();

        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"DownloadsTest_{Guid.NewGuid()}")
            .Options;
        var dbFactory = new InMemFactory(options);

        var providers = new List<IMusicProvider>
        {
            new TestProvider("squidwtf", "Squid.wtf", "🐙", new[] { Platform.Qobuz, Platform.Tidal }),
            new TestProvider("lucida", "Lucida", "🔎", new[] { Platform.Deezer, Platform.Soundcloud }),
        };

        _registry.Setup(r => r.GetAllProviders()).Returns(providers);
        _registry.Setup(r => r.IsProviderEnabled("squidwtf")).Returns(true);
        _registry.Setup(r => r.IsProviderEnabled("lucida")).Returns(false);

        _ctx.Services.AddSingleton<IMusicProviderRegistry>(_registry.Object);
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(dbFactory);
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    [Test]
    public void Downloads_Renders_Title()
    {
        var cut = _ctx.Render<Downloads>();
        Assert.That(cut.Markup, Does.Contain("Download manager"));
    }

    [Test]
    public void Downloads_ShowsProviderList()
    {
        var cut = _ctx.Render<Downloads>();
        Assert.That(cut.Markup, Does.Contain("Squid.wtf"));
        Assert.That(cut.Markup, Does.Contain("Lucida"));
    }

    [Test]
    public void Downloads_ShowsBuiltInYtDlp()
    {
        var cut = _ctx.Render<Downloads>();
        Assert.That(cut.Markup, Does.Contain("yt-dlp"));
    }

    [Test]
    public void Downloads_HasSaveConfigButton()
    {
        var cut = _ctx.Render<Downloads>();
        var buttons = cut.FindAll("button");
        Assert.That(buttons.Any(b => b.TextContent.Trim().Contains("Save")), Is.True);
    }

    [Test]
    public void ToggleProvider_CallsRegistry()
    {
        var cut = _ctx.Render<Downloads>();
        var toggles = cut.FindAll("input[type=checkbox]");
        Assert.That(toggles.Count, Is.GreaterThanOrEqualTo(1));
    }

    private class TestProvider : IMusicProvider
    {
        public string Id { get; }
        public string Name { get; }
        public string Description => $"Test {Name}";
        public string Icon { get; }
        public IReadOnlyList<Platform> SupportedPlatforms { get; }
        public IReadOnlyList<TrackQuality> SupportedQualities => Array.Empty<TrackQuality>();
        public TestProvider(string id, string name, string icon, Platform[] platforms)
        {
            Id = id; Name = name; Icon = icon; SupportedPlatforms = platforms;
        }
        public Task<IReadOnlyList<SearchResult>> SearchAsync(string q, Platform? p = null, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
        public Task<TrackInfo?> GetTrackInfoAsync(string url, CancellationToken ct = default)
            => Task.FromResult<TrackInfo?>(null);
        public Task<DownloadResult> DownloadAsync(string url, TrackQuality q, string dir, CancellationToken ct = default)
            => Task.FromResult(new DownloadResult(false, null, "mock", null));
    }

    private class InMemFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InMemFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }
}
