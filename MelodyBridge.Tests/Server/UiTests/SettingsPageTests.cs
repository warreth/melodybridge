using Bunit;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The tabbed Settings page: each tab reveals its own section, and the
/// FlareSolverr tester lives on the Network tab.
/// </summary>
[TestFixture]
[Category("UI")]
public class SettingsPageTests
{
    private Bunit.TestContext _ctx = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();

        var factory = TestHelpers.CreateInMemFactory();
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);
        _ctx.Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        _ctx.Services.AddDownloadPages(factory);
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(factory, NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
        _ctx.Services.AddSingleton(tokenStore);
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider>.Instance));
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider>.Instance));
        var collector = new MelodyBridge.Server.Services.LogCollector();
        _ctx.Services.AddSingleton<MelodyBridge.Core.Logging.ILogCollector>(collector);
        _ctx.Services.AddSingleton(new MelodyBridge.Server.Services.LogExporter(collector));
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    [Test]
    public void Settings_Renders_Title_And_Tabs()
    {
        var cut = _ctx.Render<Settings>();
        Assert.That(cut.Markup, Does.Contain("Settings"));
        Assert.That(cut.Markup, Does.Contain("Accounts"));
        Assert.That(cut.Markup, Does.Contain("Quality"));
        Assert.That(cut.Markup, Does.Contain("Network"));
    }

    [Test]
    public void Settings_AccountsTab_ShowsByDefault()
    {
        var cut = _ctx.Render<Settings>();
        Assert.That(cut.Markup, Does.Contain("Connect Spotify"));
        Assert.That(cut.Markup, Does.Contain("Connect YouTube"));
        Assert.That(cut.Markup, Does.Contain("not connected"));
    }

    [Test]
    public void Settings_JellyfinTab_ShowsForm()
    {
        var cut = _ctx.Render<Settings>();
        cut.FindAll("button.tab-link").First(b => b.TextContent == "Jellyfin").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Base URL"));
            Assert.That(cut.Markup, Does.Contain("API key"));
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Settings_PathsTab_ShowsPathFields()
    {
        var cut = _ctx.Render<Settings>();
        cut.FindAll("button.tab-link").First(b => b.TextContent == "Paths").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Music path"));
            Assert.That(cut.Markup, Does.Contain("Playlist output folder"));
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Settings_QualityTab_ShowsDefaultPresetAndSpectrum()
    {
        var cut = _ctx.Render<Settings>();
        cut.FindAll("button.tab-link").First(b => b.TextContent == "Quality").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Default audio quality"));
            Assert.That(cut.Markup, Does.Contain("Real quality check"));
            Assert.That(cut.Markup, Does.Contain("Spectrum check"));
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Settings_NetworkTab_ShowsFlareSolverrTester()
    {
        var cut = _ctx.Render<Settings>();
        cut.FindAll("button.tab-link").First(b => b.TextContent == "Network").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("FlareSolverr"));
            Assert.That(cut.Markup, Does.Contain("Test connection"));
            Assert.That(cut.Markup, Does.Contain("Export logs"));
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Settings_HasSaveButton()
    {
        var cut = _ctx.Render<Settings>();
        var btns = cut.FindAll("button");
        Assert.That(btns.Any(b => b.TextContent.Trim().Contains("Save settings")), Is.True);
    }

    [Test]
    public async Task Settings_AccountStatus_ReflectsRealStoredTokens()
    {
        var tokenStore = _ctx.Services
            .GetRequiredService<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>();
        await tokenStore.SaveTokensAsync("Spotify", new MelodyBridge.Core.AccountTokens(
            "access", "refresh", DateTime.UtcNow.AddHours(1)));

        var cut = _ctx.Render<Settings>();
        Assert.That(cut.Markup, Does.Contain("Disconnect"),
            "a connected account shows a disconnect button");
    }
}
