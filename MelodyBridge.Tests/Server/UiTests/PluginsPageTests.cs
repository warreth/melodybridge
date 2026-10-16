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
        _ctx.Services.AddSingleton(new MelodyBridge.Server.Services.NotificationService());
        _ctx.Services.AddSingleton<MelodyBridge.Infrastructure.Downloaders.YtDlpUpdater>(
            new TestServices.StubYtDlpUpdater());
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    [Test]
    public void Plugins_Renders_Title_And_Plugin_List()
    {
        var cut = _ctx.Render<Plugins>();

        Assert.That(cut.Markup, Does.Contain("Plugins"));
        Assert.That(cut.Markup, Does.Contain("tried in order until one delivers"),
            "the intro explains the waterfall in the current wording");
        Assert.That(cut.Markup, Does.Contain("yt-dlp (YouTube)"));
        Assert.That(cut.Markup, Does.Contain("Second Plugin"));
    }

    [Test]
    public async Task Plugins_YtDlpStrip_ShowsVersion_AndUpdatesOnClick()
    {
        var updater = new TestServices.StubYtDlpUpdater();
        _ctx.Services.AddSingleton<MelodyBridge.Infrastructure.Downloaders.YtDlpUpdater>(updater);

        var cut = _ctx.Render<Plugins>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("2026.08.19"),
                "the installed yt-dlp version is shown"), TimeSpan.FromSeconds(3));

        var button = cut.FindAll("button").Single(b => b.TextContent.Contains("Update yt-dlp"));
        await cut.InvokeAsync(() => button.Click());

        cut.WaitForAssertion(() =>
        {
            Assert.That(updater.UpdateCalls, Is.EqualTo(1), "the button runs the updater");
            Assert.That(cut.Markup, Does.Contain("Updated with python3 -m pip"),
                "the update output is shown");
            Assert.That(cut.Markup, Does.Contain("2026.10.14"),
                "the new version replaces the old one");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Plugins_ShowsPriorityControls()
    {
        var cut = _ctx.Render<Plugins>();

        Assert.That(cut.Markup, Does.Contain("Try this plugin first"));
        var upButtons = cut.FindAll("button[title='Try this plugin first']");
        Assert.That(upButtons.Count, Is.EqualTo(2), "one move-up per plugin");
    }

    [Test]
    public void Plugins_ShowsQualityBadges()
    {
        var cut = _ctx.Render<Plugins>();

        // Badges derive from the declared capabilities plus a curated
        // note; the stub declares a controlled mp3 band and lossless.
        Assert.That(cut.Markup, Does.Contain("64-320 kbps"), "the capability band renders");
        Assert.That(cut.Markup, Does.Contain("FLAC lossless"), "the lossless level renders");
        Assert.That(cut.Markup, Does.Contain("widest catalog"), "the curated note renders");
    }

    [Test]
    public void Plugins_LiveRunsHiddenWhenNone()
    {
        var cut = _ctx.Render<Plugins>();

        Assert.That(cut.Markup, Does.Not.Contain("Live downloads"), "no run, no panel");
    }

    [Test]
    public void Plugins_ConfigFields_OpenFromTheSettingsRow()
    {
        // Rebuild the registry with a plugin that has config fields,
        // including one advanced and one secret field.
        var configured = new TestDownloader("ytdlp", "yt-dlp (YouTube)",
            new[]
            {
                new PluginConfigField("extractor", "Search preference", "ytmsearch1"),
                new PluginConfigField("command", "Command", null, "Advanced knob", Advanced: true),
                new PluginConfigField("key", "API key", null, null, Secret: true),
            });
        _registry.Setup(r => r.GetAll()).Returns(new List<IDownloader> { configured });
        _registry.Setup(r => r.GetEnabled()).Returns(new List<IDownloader> { configured });
        _registry.Setup(r => r.GetConfigAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("ytmsearch1");

        var cut = _ctx.Render<Plugins>();

        // Collapsed: the row exists, the fields stay out of the DOM.
        Assert.That(cut.Markup, Does.Contain("Settings"), "plugins with fields get a settings row");
        Assert.That(cut.Markup, Does.Not.Contain("Search preference"), "fields stay hidden until opened");

        cut.Find("button.settings-toggle").Click();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Search preference"), "field label renders");
            Assert.That(cut.Markup, Does.Contain("Save settings"), "panel has a save button");
            Assert.That(cut.Markup, Does.Not.Contain("Advanced knob"),
                "advanced fields stay behind the advanced toggle");
            Assert.That(cut.FindAll("input[type=password]").Count, Is.EqualTo(1),
                "secret fields render as password inputs");
        });

        cut.Find("button.advanced-toggle").Click();
        Assert.That(cut.Markup, Does.Contain("Advanced knob"), "advanced fields appear on demand");
    }

    [Test]
    public void Plugins_ChoiceField_RendersAsSelect()
    {
        var configured = new TestDownloader("qobuz", "Qobuz",
            new[]
            {
                new PluginConfigField("quality", "FLAC quality", null, null,
                    Options: new[]
                    {
                        new PluginConfigChoice("6", "CD quality"),
                        new PluginConfigChoice("27", "Hi-res"),
                    }),
            });
        _registry.Setup(r => r.GetAll()).Returns(new List<IDownloader> { configured });
        _registry.Setup(r => r.GetEnabled()).Returns(new List<IDownloader> { configured });

        var cut = _ctx.Render<Plugins>();
        cut.Find("button.settings-toggle").Click();

        var select = cut.Find("select");
        Assert.Multiple(() =>
        {
            Assert.That(select.QuerySelectorAll("option").Length, Is.EqualTo(2),
                "a field with options renders as a select");
            Assert.That(select.QuerySelector("option[selected]")?.GetAttribute("value"), Is.EqualTo("6"),
                "an unset value shows the first option");
        });
    }

    [Test]
    public void Plugins_WithoutConfigFields_HaveNoPanel()
    {
        var cut = _ctx.Render<Plugins>();
        Assert.That(cut.FindAll("button.settings-toggle").Count, Is.EqualTo(0),
            "no fields declared, no settings row rendered");
    }
}
