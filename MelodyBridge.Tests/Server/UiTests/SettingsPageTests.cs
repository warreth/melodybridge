using TestContext = Bunit.TestContext;
using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Core.Logging;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Pages;
using MelodyBridge.Server.Logging;
using MelodyBridge.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

[TestFixture]
[Category("UI")]
public class SettingsPageTests
{
    private TestContext _ctx = null!;
    private Mock<IDownloaderRegistry> _registry = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _registry = new Mock<IDownloaderRegistry>();

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

        var providers = new List<IDownloader>
        {
            new TestDownloader("squidwtf", "Squid.wtf"),
            new TestDownloader("lucida", "Lucida"),
        };
        _registry.Setup(r => r.GetAll()).Returns(providers);
        _registry.Setup(r => r.IsEnabled(It.IsAny<string>())).Returns(true);

        _ctx.Services.AddSingleton<IDownloaderRegistry>(_registry.Object);
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(dbFactory);

        // Logging services required by the Settings page
        var logCollector = new LogCollector();
        _ctx.Services.AddSingleton<ILogCollector>(logCollector);
        _ctx.Services.AddSingleton(new LogExporter(logCollector));
        _ctx.Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
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
    public void Settings_ShowsQualityVerificationPanel()
    {
        var cut = _ctx.Render<Settings>();
        Assert.That(cut.Markup, Does.Contain("Real quality check"),
            "the spectral verification panel must be on the page");
        Assert.That(cut.Markup, Does.Contain("Spectrum check"));
        Assert.That(cut.Markup, Does.Contain("Cloudflare solver (Lucida)"),
            "the FlareSolverr URL field must be on the page");
        Assert.That(cut.FindAll("select option").Count(o =>
            o.TextContent.Contains("Thorough")), Is.GreaterThanOrEqualTo(1),
            "the thorough mode must be selectable");
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

    private class TestDownloader : IDownloader
    {
        public string Id { get; }
        public string Name { get; }
        public TestDownloader(string id, string name) { Id = id; Name = name; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DownloaderSearchHit?> SearchAsync(string artist, string title, DownloadQuality quality, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(null);
        public Task<DownloaderDownloadResult> DownloadAsync(string sourceUrl, string outputDirectory, string? melodyId, CancellationToken ct = default)
            => Task.FromResult(new DownloaderDownloadResult(false, null, "mock"));
    }

    private class InMemFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InMemFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }
}
