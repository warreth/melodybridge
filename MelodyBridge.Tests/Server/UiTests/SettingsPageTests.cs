using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

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
        // Pages inject the directory; default mock does nothing until a test sets it up.
        _ctx.Services.AddSingleton(new Moq.Mock<MelodyBridge.Core.IMediaServerDirectory>().Object);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    /// <summary>Swaps the setup's default directory mock for a test's own.</summary>
    private void ReplaceService(Mock<MelodyBridge.Core.IMediaServerDirectory> directory)
    {
        var descriptor = _ctx.Services.FirstOrDefault(d =>
            d.ServiceType == typeof(MelodyBridge.Core.IMediaServerDirectory));
        if (descriptor is not null) _ctx.Services.Remove(descriptor);
        _ctx.Services.AddSingleton<MelodyBridge.Core.IMediaServerDirectory>(directory.Object);
    }

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
    public void Settings_SpotifyHelp_AnswersTheApiPicker()
    {
        // Spotify's create-app dialog asks "which APIs are you planning to
        // use" with five cards; the help text must name the right one.
        var cut = _ctx.Render<Settings>();
        Assert.That(cut.Markup, Does.Contain("Web API"),
            "the Spotify steps must tell the user to pick Web API");
        Assert.That(cut.Markup, Does.Contain("development mode"),
            "dev mode (Premium owner, allowlist) must be explained");
    }

    [Test]
    public void Settings_YouTubeHelp_MentionsTestUserAndConsentScreen()
    {
        var cut = _ctx.Render<Settings>();
        Assert.That(cut.Markup, Does.Contain("Test user"),
            "the YouTube steps must mention adding yourself as test user");
        Assert.That(cut.Markup, Does.Contain("unverified app"),
            "the unverified-app warning must be explained, not a surprise");
    }

    [Test]
    public void Settings_NoStandaloneJellyfinTab_MediaServersInstead()
    {
        var cut = _ctx.Render<Settings>();

        var labels = cut.FindAll("button.tab-link").Select(b => b.TextContent.Trim()).ToList();
        Assert.That(labels, Does.Not.Contain("Jellyfin"),
            "the standalone Jellyfin tab is gone; profiles own servers now");
        Assert.That(labels, Does.Contain("Media servers"),
            "the connections tab is labelled Media servers");
    }

    [Test]
    public void Settings_PathsTab_ShowsPathFields()
    {
        var cut = _ctx.Render<Settings>();
        cut.FindAll("button.tab-link").First(b => b.TextContent == "Default paths").Click();

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
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Settings_NetworkTab_AutoIsTheDefaultWhenNoRowExists()
    {
        var cut = _ctx.Render<Settings>();
        cut.FindAll("button.tab-link").First(b => b.TextContent == "Network").Click();

        cut.WaitForAssertion(() =>
        {
            // No flaresolverr_url row: the appsettings "auto" must land
            // in the field so compose users never touch it.
            var field = cut.Find("input[placeholder='auto, http://flaresolverr:8191 or off']");
            Assert.That(field.GetAttribute("value"), Is.EqualTo("auto"),
                "the URL field starts at auto when no DB row overrides it");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Settings_NetworkTab_HintExplainsAutoDetection()
    {
        var cut = _ctx.Render<Settings>();
        cut.FindAll("button.tab-link").First(b => b.TextContent == "Network").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("looks for that container on the Docker network"),
                "the hint must tell compose users they can leave auto alone");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Settings_NetworkTab_TestButtonExistsAndIsEnabled()
    {
        // The auto sweep itself is covered by FlareSolverrSolverTests with
        // stubbed HTTP; the page's own HttpClient is not stubbable here,
        // so the UI side only pins the button down.
        var cut = _ctx.Render<Settings>();
        cut.FindAll("button.tab-link").First(b => b.TextContent == "Network").Click();

        cut.WaitForAssertion(() =>
        {
            var button = cut.FindAll("button").First(b => b.TextContent.Trim() == "Test connection");
            Assert.That(button.HasAttribute("disabled"), Is.False,
                "the tester starts enabled, ready to probe");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Settings_ConnectionsTab_ManagesProfilesThroughRealStore()
    {
        var cut = _ctx.Render<Settings>();
        cut.FindAll("button.tab-link").First(b => b.TextContent == "Media servers").Click();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("No profiles yet"),
                "a fresh database shows the empty state"), TimeSpan.FromSeconds(3));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Add profile").Click();

        var name = cut.Find("input[placeholder='Living room Jellyfin']");
        name.Change("Main server");
        var url = cut.Find("input[placeholder='http://jellyfin:8096']");
        url.Change("http://media:8096");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save profile").Click();

        // The saved profile must exist through the real store, not just markup.
        var profiles = _ctx.Services.GetRequiredService<MelodyBridge.Infrastructure.Services.MediaServerProfileStore>();
        cut.WaitForAssertion(() =>
        {
            var all = profiles.GetAllAsync().GetAwaiter().GetResult();
            Assert.That(all.Count, Is.EqualTo(1), "the profile was persisted");
            Assert.That(all[0].Name, Is.EqualTo("Main server"));
            Assert.That(all[0].BaseUrl, Is.EqualTo("http://media:8096"));
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Settings_ConnectionsTab_TestButton_UsesJellyfinDirectory()
    {
        var directory = new Mock<MelodyBridge.Core.IMediaServerDirectory>();
        directory.Setup(d => d.TestConnectionAsync(
                It.Is<MediaServerConnection>(c => c.BaseUrl == "http://media:8096" && c.ApiKey == "key1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        ReplaceService(directory);

        var profileStore = _ctx.Services.GetRequiredService<MelodyBridge.Infrastructure.Services.MediaServerProfileStore>();
        profileStore.SaveAsync(new MelodyBridge.Infrastructure.Services.MediaServerProfile
        {
            Name = "Main server",
            BaseUrl = "http://media:8096",
            ApiKey = "key1",
            Kind = "Jellyfin",
        }).GetAwaiter().GetResult();

        var cut = _ctx.Render<Settings>();
        cut.FindAll("button.tab-link").First(b => b.TextContent == "Media servers").Click();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Main server")), TimeSpan.FromSeconds(3));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Test").Click();

        cut.WaitForAssertion(() =>
        {
            directory.Verify(d => d.TestConnectionAsync(
                It.Is<MediaServerConnection>(c => c.BaseUrl == "http://media:8096" && c.ApiKey == "key1"),
                It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(cut.Markup, Does.Contain("connected"),
                "the ok pill shows next to the row after a passing test");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Settings_ConnectionsTab_MakeAppDefault_WritesGlobalJellyfinRows()
    {
        var profileStore = _ctx.Services.GetRequiredService<MelodyBridge.Infrastructure.Services.MediaServerProfileStore>();
        profileStore.SaveAsync(new MelodyBridge.Infrastructure.Services.MediaServerProfile
        {
            Name = "Main server",
            BaseUrl = "http://media:8096",
            ApiKey = "key1",
            Kind = "Jellyfin",
        }).GetAwaiter().GetResult();

        var cut = _ctx.Render<Settings>();
        cut.FindAll("button.tab-link").First(b => b.TextContent == "Media servers").Click();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Main server")), TimeSpan.FromSeconds(3));

        Assert.That(cut.Markup, Does.Not.Contain(">default<"),
            "no default pill before the user picks one");

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Use as app default").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain(">default<"),
                "the active profile carries the default pill");
        }, TimeSpan.FromSeconds(3));

        var factory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using (var db = factory.CreateDbContext())
        {
            var settings = db.DownloaderSettings.ToList();
            Assert.That(settings.First(s => s.Key == "jellyfin_url").Value,
                Is.EqualTo("http://media:8096"), "jellyfin_url row written from the profile");
            Assert.That(settings.First(s => s.Key == "jellyfin_key").Value,
                Is.EqualTo("key1"), "jellyfin_key row written from the profile");
        }
    }

    [Test]
    public void Settings_ConnectionsTab_SeedsDefaultProfile_FromOldJellyfinSettings()
    {
        var factory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.DownloaderSettings.Add(new DownloaderSettingEntity
            { Key = "jellyfin_url", Value = "http://old-jellyfin:8096" });
            db.DownloaderSettings.Add(new DownloaderSettingEntity
            { Key = "jellyfin_key", Value = "old-key" });
            db.SaveChanges();
        }

        var cut = _ctx.Render<Settings>();
        cut.FindAll("button.tab-link").First(b => b.TextContent == "Media servers").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Default"),
                "the old single-server settings became one profile");
            Assert.That(cut.Markup, Does.Contain("old single-server Jellyfin settings moved into a profile"),
                "the hint explains the migration");
        }, TimeSpan.FromSeconds(3));

        var profileStore = _ctx.Services.GetRequiredService<MelodyBridge.Infrastructure.Services.MediaServerProfileStore>();
        var all = profileStore.GetAllAsync().GetAwaiter().GetResult();
        Assert.That(all.Count, Is.EqualTo(1), "exactly one seeded profile");
        Assert.That(all[0].Name, Is.EqualTo("Default"));
        Assert.That(all[0].BaseUrl, Is.EqualTo("http://old-jellyfin:8096"));
        Assert.That(all[0].ApiKey, Is.EqualTo("old-key"));
    }

    [Test]
    public void Settings_AboutTab_ShowsVersionUpdateCheckAndBackup()
    {
        var cut = _ctx.Render<Settings>();
        cut.FindAll("button.tab-link").First(b => b.TextContent == "About").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain($"MelodyBridge {MelodyBridge.Core.AppInfo.Version}"),
                "the About tab shows the running version");
            Assert.That(cut.Markup, Does.Contain("Check for updates"));
            Assert.That(cut.Markup, Does.Contain("Export zip"));
            Assert.That(cut.Markup, Does.Contain("guided tour"));
            Assert.That(cut.Markup, Does.Contain("Export logs"),
                "logs export moved from Network to About");
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
    public async Task Settings_SaveAll_NoLongerWritesJellyfinRows()
    {
        var factory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        var cut = _ctx.Render<Settings>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save settings").Click();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Settings saved.")), TimeSpan.FromSeconds(3));

        using (var db = factory.CreateDbContext())
        {
            var keys = db.DownloaderSettings.Select(s => s.Key).ToList();
            Assert.That(keys, Does.Not.Contain("jellyfin_url"),
                "the old jellyfin fields are gone from SaveAll");
            Assert.That(keys, Does.Not.Contain("jellyfin_key"));
            Assert.That(keys, Does.Not.Contain("jellyfin_user"));
            Assert.That(keys, Does.Contain("music_path"),
                "the remaining settings still save");
        }
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

    [Test]
    public void Settings_DefaultTab_IsMarkedActiveExactlyOnce()
    {
        var cut = _ctx.Render<Settings>();

        var active = cut.FindAll("button.tab-link.active");
        Assert.That(active.Count, Is.EqualTo(1),
            "exactly one tab carries the active class");
        Assert.That(active[0].TextContent.Trim(), Is.EqualTo("Accounts"),
            "the Accounts tab is active by default");
        Assert.That(active[0].GetAttribute("aria-current"), Is.EqualTo("tab"),
            "the active tab announces itself to screen readers");

        var selected = cut.FindAll("button.tab-link[aria-selected='true']");
        Assert.That(selected.Count, Is.EqualTo(1),
            "exactly one tab is aria-selected");
    }

    [Test]
    public void Settings_TabClick_MovesActiveStateAndPanel()
    {
        var cut = _ctx.Render<Settings>();

        cut.FindAll("button.tab-link").Single(b => b.TextContent.Trim() == "About").Click();

        var active = cut.FindAll("button.tab-link.active").Single();
        Assert.That(active.TextContent.Trim(), Is.EqualTo("About"),
            "clicking a tab moves the active marker");

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Check for updates"),
                "the About panel is visible");
            Assert.That(cut.Markup, Does.Not.Contain("Connect Spotify"),
                "the Accounts panel is hidden");
        }, TimeSpan.FromSeconds(3));

        cut.FindAll("button.tab-link").Single(b => b.TextContent.Trim() == "Accounts").Click();

        cut.WaitForAssertion(() =>
        {
            var back = cut.FindAll("button.tab-link.active").Single();
            Assert.That(back.TextContent.Trim(), Is.EqualTo("Accounts"),
                "clicking back returns the marker");
            Assert.That(cut.Markup, Does.Contain("Connect Spotify"),
                "the Accounts panel is back");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Settings_TabClick_RewritesUrlFragment()
    {
        var cut = _ctx.Render<Settings>();

        cut.FindAll("button.tab-link").Single(b => b.TextContent.Trim() == "Quality").Click();

        var nav = _ctx.Services.GetRequiredService<NavigationManager>();
        Assert.That(nav.Uri, Does.EndWith("#quality"),
            "the tab id lands in the fragment so refresh keeps the tab");
    }

    [Test]
    public void Settings_Fragment_OpensTheNamedTab()
    {
        // The auth callback links /settings#accounts; the dashboard links
        // other sections. The fragment must pick the tab, not the default.
        var fixedNav = new FixedUriNavigationManager("http://localhost/", "http://localhost/settings#quality");
        var navDescriptor = _ctx.Services.FirstOrDefault(
            d => d.ServiceType == typeof(Microsoft.AspNetCore.Components.NavigationManager));
        if (navDescriptor is not null) _ctx.Services.Remove(navDescriptor);
        _ctx.Services.AddSingleton<Microsoft.AspNetCore.Components.NavigationManager>(fixedNav);

        var cut = _ctx.Render<Settings>();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Audio quality"),
                "the #quality fragment opens the quality tab");
            Assert.That(cut.Markup, Does.Contain("Space Saver"),
                "the quality tab carries the preset dropdown");
            Assert.That(cut.Markup, Does.Not.Contain("Media server profiles"),
                "and not the media servers tab");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Settings_TabClick_UpdatesTheUrlFragment()
    {
        var cut = _ctx.Render<Settings>();
        cut.FindAll("button.tab-link").First(b => b.TextContent.Trim() == "Network").Click();

        cut.WaitForAssertion(() =>
            Assert.That(_ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri,
                Does.EndWith("#network"),
                "the fragment follows the tab, so refresh keeps it"),
            TimeSpan.FromSeconds(3));
    }
}
