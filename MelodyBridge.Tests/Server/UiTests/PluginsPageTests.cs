using TestContext = Bunit.TestContext;
using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Pages;
using MelodyBridge.Server.Services;
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
    private NotificationService _notifications = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddPluginAuthMonitor();
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
        _notifications = new MelodyBridge.Server.Services.NotificationService(
            autoDismissAfter: TimeSpan.FromMinutes(1));
        _ctx.Services.AddSingleton(_notifications);
        _ctx.Services.AddSingleton<MelodyBridge.Infrastructure.Downloaders.YtDlpUpdater>(
            new TestServices.StubYtDlpUpdater());
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    private sealed class AuthTestDownloader : IDownloader, IPluginAuthStatusProvider, IPluginAvailabilityRefresh
    {
        public AuthTestDownloader(string id, string name, PluginConfigField[]? fields = null)
        {
            Id = id;
            Name = name;
            ConfigFields = fields ?? Array.Empty<PluginConfigField>();
        }

        public string Id { get; }
        public string Name { get; }
        public string Description => "auth test downloader";
        public PluginCapabilities Capabilities { get; set; } = PluginCapabilities.Any;
        public IReadOnlyList<PluginConfigField> ConfigFields { get; }
        public PluginAuthStatus AuthStatus { get; set; } = PluginAuthStatus.Unknown;
        public bool Available { get; set; } = true;
        public int Probes { get; private set; }
        public int Refreshes { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            Probes++;
            return Task.FromResult(Available);
        }

        public void RefreshAvailability() => Refreshes++;

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(null);

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
            => Task.FromResult(new DownloaderDownloadResult(false, null, "test"));
    }

    private sealed class GatedAvailabilityDownloader : IDownloader, IPluginAvailabilityRefresh
    {
        public string Id => "qobuz";
        public string Name => "Qobuz";
        public string Description => "gated test downloader";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;
        public IReadOnlyList<PluginConfigField> ConfigFields { get; } =
            [new PluginConfigField("token", "Token", null)];
        public bool Available { get; set; }
        public int Probes;
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        {
            var probe = Interlocked.Increment(ref Probes);
            var result = Available;
            if (probe == 2)
            {
                Entered.TrySetResult();
                await _release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            }
            return result;
        }

        public void Release() => _release.TrySetResult();
        public void RefreshAvailability() { }

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(null);

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
            => Task.FromResult(new DownloaderDownloadResult(false, null, "test"));
    }

    [Test]
    public void Plugins_ShowsTheLoginBanner_AndRedFlagForAnExpiredToken()
    {
        var broken = new AuthTestDownloader("qobuz", "Qobuz")
        {
            Available = false,
            AuthStatus = new PluginAuthStatus(PluginAuthState.Expired,
                "The Qobuz token expired; paste a fresh one"),
        };
        _registry.Setup(r => r.GetAll()).Returns(new List<IDownloader> { broken });
        _registry.Setup(r => r.GetEnabled()).Returns(new List<IDownloader> { broken });

        var cut = _ctx.Render<Plugins>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("One plugin needs your login")),
            TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("One plugin needs your login"));
            Assert.That(cut.Markup, Does.Contain("The Qobuz token expired; paste a fresh one"));
            Assert.That(cut.FindAll(".plugin-auth-flag").Count, Is.EqualTo(1),
                "the plugin icon carries a red exclamation mark");
            Assert.That(cut.FindAll(".plugin-card.auth-error").Count, Is.EqualTo(1));
            Assert.That(cut.Markup, Does.Contain("token expired"),
                "the status pill names the login state");
        });
    }

    [Test]
    public void Plugins_MissingLogin_IsUnavailableButNotARedAlert()
    {
        var unconfigured = new AuthTestDownloader("qobuz", "Qobuz")
        {
            Available = false,
            AuthStatus = new PluginAuthStatus(PluginAuthState.Missing, "No Qobuz token yet"),
        };
        _registry.Setup(r => r.GetAll()).Returns(new List<IDownloader> { unconfigured });
        _registry.Setup(r => r.GetEnabled()).Returns(new List<IDownloader> { unconfigured });

        var cut = _ctx.Render<Plugins>();

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Not.Contain("needs your login"));
            Assert.That(cut.FindAll(".plugin-auth-flag").Count, Is.EqualTo(0),
                "a plugin that was never set up is not a broken login");
            Assert.That(cut.Markup, Does.Contain("No Qobuz token yet"));
        });
    }

    [Test]
    public async Task Plugins_OpenSettingsFromTheLoginBanner()
    {
        var broken = new AuthTestDownloader("qobuz", "Qobuz", new[]
        {
            new PluginConfigField("user_auth_token", "Qobuz token", null, null, Secret: true),
        })
        {
            Available = false,
            AuthStatus = new PluginAuthStatus(PluginAuthState.Rejected, "The Qobuz token was rejected"),
        };
        _registry.Setup(r => r.GetAll()).Returns(new List<IDownloader> { broken });
        _registry.Setup(r => r.GetEnabled()).Returns(new List<IDownloader> { broken });
        _registry.Setup(r => r.GetConfigAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("");

        var cut = _ctx.Render<Plugins>();
        Assert.That(cut.FindAll("input[type=password]").Count, Is.EqualTo(0),
            "settings stay closed until asked");

        var monitor = _ctx.Services.GetRequiredService<PluginAuthMonitor>();
        cut.WaitForAssertion(() =>
            Assert.That(monitor.Snapshot(), Is.Not.Empty),
            TimeSpan.FromSeconds(5));
        // The async probe can land after bUnit returned the first render;
        // the real page's timer does this render on its next tick.
        cut.Render();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".auth-alert-list button").Count, Is.GreaterThan(0)),
            TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => cut.FindAll(".auth-alert-list button").Single().Click());

        Assert.That(cut.FindAll("input[type=password]").Count, Is.EqualTo(1),
            "the banner's Open settings button opens the card's panel");
    }

    [Test]
    public async Task Plugins_SaveSettings_ForcesARealProbe()
    {
        var plugin = new AuthTestDownloader("qobuz", "Qobuz", new[]
        {
            new PluginConfigField("user_auth_token", "Qobuz token", null, null, Secret: true),
        })
        {
            Available = false,
            AuthStatus = new PluginAuthStatus(PluginAuthState.Expired, "The token expired"),
        };
        _registry.Setup(r => r.GetAll()).Returns(new List<IDownloader> { plugin });
        _registry.Setup(r => r.GetEnabled()).Returns(new List<IDownloader> { plugin });
        _registry.Setup(r => r.GetConfigAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("");

        var cut = _ctx.Render<Plugins>();
        await cut.InvokeAsync(() => cut.Find("button.settings-toggle").Click());
        await cut.InvokeAsync(() => cut.FindAll("button").Single(b => b.TextContent.Contains("Save settings")).Click());

        Assert.Multiple(() =>
        {
            Assert.That(plugin.Refreshes, Is.EqualTo(1),
                "saving a token drops the old negative availability cache");
            Assert.That(plugin.Probes, Is.GreaterThanOrEqualTo(2),
                "the page probes once on load and again after the save");
        });
    }

    [Test]
    public async Task Plugins_ASweepStartedBeforeASave_DoesNotOverwriteTheSavedResult()
    {
        var plugin = new GatedAvailabilityDownloader();
        _registry.Setup(r => r.GetAll()).Returns(new List<IDownloader> { plugin });
        _registry.Setup(r => r.GetEnabled()).Returns(new List<IDownloader> { plugin });
        _registry.Setup(r => r.GetConfigAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("");

        var cut = _ctx.Render<Plugins>();
        cut.WaitForAssertion(() => Assert.That(
            cut.Find(".plugin-card .pill").TextContent.Trim(), Is.EqualTo("unavailable")),
            TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => cut.Find("button.settings-toggle").Click());

        var sweep = cut.InvokeAsync(() => cut.Instance.RefreshAvailability());
        await plugin.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        plugin.Available = true;
        await cut.InvokeAsync(() => cut.FindAll("button")
            .Single(button => button.TextContent.Contains("Save settings")).Click());
        cut.WaitForAssertion(() => Assert.That(
            cut.Find(".plugin-card .pill").TextContent.Trim(), Is.EqualTo("ready")),
            TimeSpan.FromSeconds(5));

        plugin.Release();
        await sweep;
        cut.Render();

        Assert.That(cut.Find(".plugin-card .pill").TextContent.Trim(), Is.EqualTo("ready"),
            "the sweep probed before the save and must not win afterwards");
    }

    [Test]
    public void Plugins_ValidLogin_ShowsAccountTierAndExpiry()
    {
        var plugin = new AuthTestDownloader("qobuz", "Qobuz")
        {
            Available = true,
            AuthStatus = new PluginAuthStatus(
                PluginAuthState.Valid,
                Account: "Studio",
                Tier: "Hi-res up to 96 kHz",
                ExpiresAtUtc: DateTimeOffset.UtcNow.AddDays(12)),
        };
        _registry.Setup(r => r.GetAll()).Returns(new List<IDownloader> { plugin });
        _registry.Setup(r => r.GetEnabled()).Returns(new List<IDownloader> { plugin });

        var cut = _ctx.Render<Plugins>();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Studio"));
            Assert.That(cut.Markup, Does.Contain("Hi-res up to 96 kHz"));
            Assert.That(cut.Markup, Does.Contain("expires in 12 days"));
            Assert.That(cut.Markup, Does.Contain("checked just now"));
            Assert.That(cut.Markup, Does.Contain("Setup guide"));
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Plugins_TestButton_ProbesOnlyThatPlugin()
    {
        var plugin = new AuthTestDownloader("qobuz", "Qobuz")
        {
            Available = false,
            AuthStatus = new PluginAuthStatus(PluginAuthState.Expired, "The token expired"),
        };
        _registry.Setup(r => r.GetAll()).Returns(new List<IDownloader> { plugin });
        _registry.Setup(r => r.GetEnabled()).Returns(new List<IDownloader> { plugin });

        var cut = _ctx.Render<Plugins>();
        cut.WaitForAssertion(() => Assert.That(cut.FindAll(".plugin-test").Count, Is.EqualTo(1)));
        var before = plugin.Probes;

        await cut.InvokeAsync(() => cut.Find(".plugin-test").Click());

        Assert.Multiple(() =>
        {
            Assert.That(plugin.Probes, Is.EqualTo(before + 1), "the test button probes once");
            Assert.That(plugin.Refreshes, Is.EqualTo(1), "it drops the cached availability first");
            Assert.That(_notifications.Snapshot().Any(n => n.Message.Contains("Qobuz")),
                Is.True, "the result reaches the user as a toast");
        });
    }

    [Test]
    public void Plugins_RoutePreview_NamesTheLosslessChainAndSkipsLossy()
    {
        var lossless = new AuthTestDownloader("qobuz", "Qobuz")
        {
            Available = true,
            Capabilities = PluginCapabilities.LosslessSources(),
        };
        var lossy = new AuthTestDownloader("soundcloud", "SoundCloud")
        {
            Available = true,
            Capabilities = PluginCapabilities.Lossy(AudioFormat.Mp3, 128, 320),
        };
        _registry.Setup(r => r.GetAll()).Returns(new List<IDownloader> { lossy, lossless });
        _registry.Setup(r => r.GetEnabled()).Returns(new List<IDownloader> { lossy, lossless });

        var cut = _ctx.Render<Plugins>();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Find(".route-chain").TextContent, Does.Contain("Qobuz"));
            Assert.That(cut.Find(".route-chain").TextContent, Does.Not.Contain("SoundCloud"));
            Assert.That(cut.Find(".route-skipped").TextContent,
                Does.Contain("SoundCloud (no lossless source)"));
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Plugins_DismissBanner_StaysHiddenUntilTheReasonChanges()
    {
        var plugin = new AuthTestDownloader("qobuz", "Qobuz")
        {
            Available = false,
            AuthStatus = new PluginAuthStatus(PluginAuthState.Expired, "The token expired"),
        };
        _registry.Setup(r => r.GetAll()).Returns(new List<IDownloader> { plugin });
        _registry.Setup(r => r.GetEnabled()).Returns(new List<IDownloader> { plugin });
        var monitor = _ctx.Services.GetRequiredService<PluginAuthMonitor>();

        var cut = _ctx.Render<Plugins>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("needs your login")), TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Find(".auth-alert-head button").Click());
        Assert.That(cut.FindAll("section.auth-alert").Count, Is.EqualTo(0), "dismiss hides the banner");

        monitor.Report("qobuz", "Qobuz", "The token expired");
        await cut.InvokeAsync(() => cut.Instance.RefreshAvailability());
        cut.Render();
        Assert.That(cut.FindAll("section.auth-alert").Count, Is.EqualTo(0),
            "the same verdict does not bring it back");

        monitor.Report("qobuz", "Qobuz", "Qobuz blocked this account");
        await cut.InvokeAsync(() => cut.Instance.RefreshAvailability());
        cut.Render();
        Assert.That(cut.FindAll("section.auth-alert").Count, Is.EqualTo(1),
            "a changed reason brings it back");
    }

    [Test]
    public async Task Plugins_CopyDiagnostics_CopiesTheLoginAndReason()
    {
        _ctx.JSInterop.Setup<bool>("melody.copyText", _ => true).SetResult(true);
        var plugin = new AuthTestDownloader("qobuz", "Qobuz", new[]
        {
            new PluginConfigField("user_auth_token", "Qobuz token", null, null, Secret: true),
        })
        {
            Available = false,
            AuthStatus = new PluginAuthStatus(PluginAuthState.Rejected, "The Qobuz token was rejected"),
        };
        _registry.Setup(r => r.GetAll()).Returns(new List<IDownloader> { plugin });
        _registry.Setup(r => r.GetEnabled()).Returns(new List<IDownloader> { plugin });
        _registry.Setup(r => r.GetConfigAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("secret-token");

        var cut = _ctx.Render<Plugins>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".plugin-note-action").Count, Is.EqualTo(1)),
            TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Find(".plugin-note-action").Click());

        var text = _ctx.JSInterop.Invocations
            .Where(i => i.Identifier == "melody.copyText")
            .SelectMany(i => i.Arguments)
            .OfType<string>()
            .Single();
        Assert.Multiple(() =>
        {
            Assert.That(text, Does.Contain("Plugin: Qobuz (qobuz)"));
            Assert.That(text, Does.Contain("Login: Rejected"));
            Assert.That(text, Does.Contain("The Qobuz token was rejected"));
            Assert.That(text, Does.Contain("Qobuz token: set"), "secrets are masked");
            Assert.That(text, Does.Not.Contain("secret-token"));
            Assert.That(_notifications.Snapshot().Any(n => n.Message.Contains("Diagnostics")),
                Is.True);
        });
    }

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
    public void Plugins_LinksToTheDownloadsOverview()
    {
        var cut = _ctx.Render<Plugins>();

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll("a[href='/downloads']"), Is.Not.Empty,
                "live activity moved to its own page");
            Assert.That(cut.FindAll("#live-downloads"), Is.Empty,
                "the plugin page no longer owns the run panel");
        });
    }

    [Test]
    public async Task Plugins_ConfigFields_OpenFromTheSettingsRow()
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

        await cut.InvokeAsync(() => cut.Find("button.settings-toggle").Click());

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
    public async Task Plugins_ChoiceField_RendersAsSelect()
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
        await cut.InvokeAsync(() => cut.Find("button.settings-toggle").Click());

        var select = cut.Find(".settings-body select");
        Assert.Multiple(() =>
        {
            Assert.That(select.QuerySelectorAll("option").Length, Is.EqualTo(2),
                "a field with options renders as a select");
            Assert.That(select.QuerySelector("option[selected]")?.GetAttribute("value"), Is.EqualTo("6"),
                "an unset value shows the first option");
        });
    }

    [Test]
    public async Task Plugins_AdvancedOnlyFields_ShowWithoutTheToggle()
    {
        // Monochrome ships one field and it is advanced: a lone Advanced
        // toggle would be a pointless extra click.
        var configured = new TestDownloader("monochrome", "Monochrome",
            new[]
            {
                new PluginConfigField("base_url", "API base URL", null, null, Advanced: true),
            });
        _registry.Setup(r => r.GetAll()).Returns(new List<IDownloader> { configured });
        _registry.Setup(r => r.GetEnabled()).Returns(new List<IDownloader> { configured });

        var cut = _ctx.Render<Plugins>();
        await cut.InvokeAsync(() => cut.Find("button.settings-toggle").Click());

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("API base URL"),
                "all-advanced fields render straight away");
            Assert.That(cut.FindAll("button.advanced-toggle").Count, Is.EqualTo(0),
                "no Advanced toggle when there is nothing behind it");
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
