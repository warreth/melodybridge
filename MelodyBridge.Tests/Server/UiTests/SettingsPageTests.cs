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
public class SettingsPageTests
{
    private TestContext _ctx = null!;
    private Mock<IMusicProviderRegistry> _registry = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _registry = new Mock<IMusicProviderRegistry>();

        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"SettingsTest_{Guid.NewGuid()}")
            .Options;
        var dbFactory = new InMemFactory(options);

        using (var db = dbFactory.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity { Key = "jellyfin_url", Value = "http://jellyfin:8096" });
            db.DownloaderSettings.Add(new DownloaderSettingEntity { Key = "music_path", Value = "/custom/music" });
            db.SaveChanges();
        }

        var providers = new List<IMusicProvider>
        {
            new TestProvider("squidwtf", "Squid.wtf"),
            new TestProvider("lucida", "Lucida"),
        };
        _registry.Setup(r => r.GetAllProviders()).Returns(providers);
        _registry.Setup(r => r.IsProviderEnabled(It.IsAny<string>())).Returns(true);

        _ctx.Services.AddSingleton<IMusicProviderRegistry>(_registry.Object);
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(dbFactory);
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    [Test]
    public void Settings_Renders_Title()
    {
        var cut = _ctx.Render<Settings>();
        Assert.That(cut.Markup, Does.Contain("Settings"));
    }

    [Test]
    public void Settings_ShowsJellyfinForm()
    {
        var cut = _ctx.Render<Settings>();
        Assert.That(cut.Markup, Does.Contain("Jellyfin"));
        Assert.That(cut.Markup, Does.Contain("Base URL"));
        Assert.That(cut.Markup, Does.Contain("API key"));
    }

    [Test]
    public void Settings_ShowsPathConfig()
    {
        var cut = _ctx.Render<Settings>();
        Assert.That(cut.Markup, Does.Contain("Music path"));
        Assert.That(cut.Markup, Does.Contain("Playlist output folder"));
    }

    [Test]
    public void Settings_LoadsSavedSettings()
    {
        var cut = _ctx.Render<Settings>();
        Assert.That(cut.Markup, Does.Contain("http://jellyfin:8096"));
    }

    [Test]
    public void Settings_ShowsProviderToggles()
    {
        var cut = _ctx.Render<Settings>();
        Assert.That(cut.Markup, Does.Contain("Squid.wtf"));
        Assert.That(cut.Markup, Does.Contain("Lucida"));
    }

    [Test]
    public void Settings_HasSaveButton()
    {
        var cut = _ctx.Render<Settings>();
        var btns = cut.FindAll("button");
        Assert.That(btns.Any(b => b.TextContent.Trim().Contains("Save all settings")), Is.True);
    }

    private class TestProvider : IMusicProvider
    {
        public string Id { get; }
        public string Name { get; }
        public string Description => $"Test {Name}";
        public string Icon => "🧪";
        public IReadOnlyList<Platform> SupportedPlatforms => new[] { Platform.Qobuz };
        public IReadOnlyList<TrackQuality> SupportedQualities => Array.Empty<TrackQuality>();
        public TestProvider(string id, string name) { Id = id; Name = name; }
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
