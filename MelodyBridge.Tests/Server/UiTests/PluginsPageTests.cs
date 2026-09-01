using TestContext = Bunit.TestContext;
using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The merged Plugins page: waterfall order, availability pills, live runs
/// from the coordinator.
/// </summary>
[TestFixture]
[Category("UI")]
public class PluginsPageTests
{
    private TestContext _ctx = null!;
    private Mock<IDownloaderRegistry> _registry = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _registry = new Mock<IDownloaderRegistry>();

        var dbFactory = TestHelpers.CreateInMemFactory();

        var providers = new List<IDownloader>
        {
            new TestDownloader("ytdlp", "yt-dlp (YouTube)"),
            new TestDownloader("test2", "Second Plugin"),
        };

        _registry.Setup(r => r.GetAll()).Returns(providers);
        _registry.Setup(r => r.GetEnabled()).Returns(providers);
        _registry.Setup(r => r.IsEnabled(It.IsAny<string>())).Returns(true);

        _ctx.Services.AddSingleton<IDownloaderRegistry>(_registry.Object);
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(dbFactory);
        // Reuse the shared page-services helper for manager/store/coordinator/settings.
        var manager = new Application.Services.DownloadManager(
            new EmptyRegistry(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Application.Services.DownloadManager>.Instance);
        var store = new MelodyBridge.Infrastructure.Services.PlaylistStore(
            dbFactory,
            Array.Empty<ISourceProvider>(),
            manager,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MelodyBridge.Infrastructure.Services.PlaylistStore>.Instance);
        _ctx.Services.AddSingleton<IDownloadManager>(manager);
        _ctx.Services.AddSingleton(store);
        // The coordinator resolves the store and db factory from the
        // provider per run, like the real app does.
        _ctx.Services.AddSingleton<Application.Services.DownloadCoordinator>();
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Services.SettingsStore(dbFactory));
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    [Test]
    public void Plugins_Renders_Title_And_Plugin_List()
    {
        var cut = _ctx.Render<Plugins>();

        Assert.That(cut.Markup, Does.Contain("Download plugins"));
        Assert.That(cut.Markup, Does.Contain("yt-dlp (YouTube)"));
        Assert.That(cut.Markup, Does.Contain("Second Plugin"));
    }

    [Test]
    public void Plugins_ShowsPriorityControls()
    {
        var cut = _ctx.Render<Plugins>();

        Assert.That(cut.Markup, Does.Contain("Move up"));
        var upButtons = cut.FindAll("button[title='Move up']");
        Assert.That(upButtons.Count, Is.EqualTo(2), "one move-up per plugin");
    }

    [Test]
    public void Plugins_ShowsQualityBadges()
    {
        var cut = _ctx.Render<Plugins>();

        Assert.That(cut.Markup, Does.Contain("re-encodes to any cap"), "yt-dlp badges render");
    }

    [Test]
    public void Plugins_LiveRunsHiddenWhenNone()
    {
        var cut = _ctx.Render<Plugins>();

        Assert.That(cut.Markup, Does.Not.Contain("Live downloads"), "no run, no panel");
    }
}
