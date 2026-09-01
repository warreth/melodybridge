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
    private Mock<IDownloaderRegistry> _registry = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _registry = new Mock<IDownloaderRegistry>();

        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"DownloadsTest_{Guid.NewGuid()}")
            .Options;
        var dbFactory = new InMemFactory(options);

        var providers = new List<IDownloader>
        {
            new TestDownloader("ytdlp", "yt-dlp (YouTube)"),
            new TestDownloader("test2", "Second Plugin"),
        };

        _registry.Setup(r => r.GetAll()).Returns(providers);
        _registry.Setup(r => r.IsEnabled("squidwtf")).Returns(true);
        _registry.Setup(r => r.IsEnabled("lucida")).Returns(false);

        _ctx.Services.AddSingleton<IDownloaderRegistry>(_registry.Object);
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(dbFactory);
        _ctx.Services.AddSingleton<IDownloadManager>(new Application.Services.DownloadManager(
            new EmptyRegistry(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Application.Services.DownloadManager>.Instance));
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    [Test]
    public void Downloads_Renders_Title()
    {
        var cut = _ctx.Render<Downloads>();
        Assert.That(cut.Markup, Does.Contain("Download plugins"));
    }

    [Test]
    public void Downloads_ShowsPluginList()
    {
        var cut = _ctx.Render<Downloads>();
        Assert.That(cut.Markup, Does.Contain("yt-dlp (YouTube)"));
        Assert.That(cut.Markup, Does.Contain("Second Plugin"));
    }

    [Test]
    public void Downloads_HasPriorityControls()
    {
        var cut = _ctx.Render<Downloads>();
        Assert.That(cut.Markup, Does.Contain("Priority order"));
        var upButtons = cut.FindAll("button[title='Move up']");
        Assert.That(upButtons.Count, Is.EqualTo(2), "one move-up per plugin");
    }

    [Test]
    public void Downloads_HasLiveProgressPanel()
    {
        var cut = _ctx.Render<Downloads>();
        Assert.That(cut.Markup, Does.Contain("Live downloads"));
    }

    [Test]
    public void Downloads_ShowsAvailabilityBadgePerPlugin()
    {
        var cut = _ctx.Render<Downloads>();
        var badges = cut.FindAll(".provider-row .pill");
        Assert.That(badges.Count, Is.EqualTo(2), "one badge per plugin");
        Assert.That(cut.Markup, Does.Contain("ready"),
            "stub plugins are available, so their badge must read ready");
    }

    [Test]
    public void Downloads_ShowsPluginDescriptions()
    {
        var cut = _ctx.Render<Downloads>();
        Assert.That(cut.Markup, Does.Contain("Downloads from yt-dlp (YouTube)"));
        Assert.That(cut.Markup, Does.Contain("Downloads from Second Plugin"));
    }

    [Test]
    public void TogglePlugin_RendersCheckboxPerPlugin()
    {
        var cut = _ctx.Render<Downloads>();
        var toggles = cut.FindAll("input[type=checkbox]");
        Assert.That(toggles.Count, Is.EqualTo(2), "one toggle per registered plugin");
    }

    private class TestDownloader : IDownloader
    {
        public string Id { get; }
        public string Name { get; }
        public string Description => $"Downloads from {Name}";
        public TestDownloader(string id, string name) { Id = id; Name = name; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DownloaderSearchHit?> SearchAsync(string artist, string title, DownloadQuality quality, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(null);
        public Task<DownloaderDownloadResult> DownloadAsync(string sourceUrl, string outputDirectory, string? melodyId, DownloadQuality? quality = null, CancellationToken ct = default)
            => Task.FromResult(new DownloaderDownloadResult(false, null, "mock"));
    }

    private class InMemFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InMemFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }

    private sealed class EmptyRegistry : IDownloaderRegistry
    {
        public IReadOnlyList<IDownloader> GetAll() => Array.Empty<IDownloader>();
        public IDownloader? Get(string id) => null;
        public IReadOnlyList<IDownloader> GetEnabled() => Array.Empty<IDownloader>();
        public Task SetEnabledAsync(string id, bool enabled) => Task.CompletedTask;
        public bool IsEnabled(string id) => false;
        public Task<int> GetPriorityAsync(string id, CancellationToken ct = default) => Task.FromResult(0);
        public Task SetPriorityAsync(string id, int priority, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
    }
}
