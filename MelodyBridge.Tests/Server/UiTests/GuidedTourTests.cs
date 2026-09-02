using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Core.Logging;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The dashboard guided tour: every Next/Back click re-runs the spotlight
/// JS with the new step's selector, so the highlight actually moves
/// across the dashboard instead of staying glued to the first element.
/// </summary>
[TestFixture]
[Category("UI")]
public class GuidedTourTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-tour-{Guid.NewGuid():N}.db");
        var factory = new TestSqliteFactory(_dbPath);
        using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            // Skip the intro so the dashboard (and its tour) renders.
            db.DownloaderSettings.Add(new DownloaderSettingEntity
                { Key = "intro_dismissed", Value = "true" });
            db.SaveChanges();
        }
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);
        _ctx.Services.AddDownloadPages(factory);
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(
            factory, NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
        _ctx.Services.AddSingleton(tokenStore);
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider>.Instance));
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider>.Instance));
        _ctx.Services.AddSingleton<MelodyBridge.Infrastructure.MediaServers.IJellyfinSettings>(
            new MelodyBridge.Infrastructure.MediaServers.ConfigJellyfinSettings(
                new ConfigurationBuilder().Build()));
        _ctx.Services.AddSingleton<ILibraryScanner>(new Moq.Mock<ILibraryScanner>().Object);
        _ctx.Services.AddSingleton(new MelodyBridge.Server.Services.DevPanelService());
        _ctx.Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        var collector = new MelodyBridge.Server.Services.LogCollector();
        _ctx.Services.AddSingleton<ILogCollector>(collector);
        _ctx.Services.AddSingleton(new MelodyBridge.Server.Services.LogExporter(collector));
        // Loose mode: melody.spotlight / melody.hideSpotlight run freely;
        // the assertions below read the recorded invocations.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [TearDown]
    public void TearDown()
    {
        _ctx.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
        }
    }

    private IReadOnlyList<string> SpotlightSelectors() =>
        _ctx.JSInterop.Invocations["melody.spotlight"]
            .Select(i => i.Arguments[0]?.ToString() ?? "").ToList();

    [Test]
    public async Task Tour_NextClicks_RunSpotlight_WithEachStepSelectorInOrder()
    {
        var cut = _ctx.Render<Home>();

        // Step 1 renders the tour and spotlights the stats grid.
        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Tour 1 of 4"));
            Assert.That(SpotlightSelectors(), Does.Contain(".stat-grid"));
        }, TimeSpan.FromSeconds(3));

        var expected = new[] { ".stat-grid", ".stat-grid .stat-card:last-child", ".provider-list", "a[href='/sync-jobs']" };

        // Click through steps 2-4: each Next must re-run the spotlight JS
        // with that step's selector, in order. Re-find the button every
        // time: the card re-renders and old element handles go stale.
        for (var step = 1; step < expected.Length; step++)
        {
            var next = cut.Find(".tour-card .btn-modern.primary");
            await cut.InvokeAsync(() => next.Click());
            var selectors = SpotlightSelectors();
            Assert.That(selectors[^1], Is.EqualTo(expected[step]),
                $"step {step + 1} must spotlight {expected[step]}");
            Assert.That(cut.Markup, Does.Contain($"Tour {step + 1} of 4"));
        }
    }

    [Test]
    public async Task Tour_BackClick_RerunsSpotlight_OfPreviousStep()
    {
        var cut = _ctx.Render<Home>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Tour 1 of 4")), TimeSpan.FromSeconds(3));

        await cut.InvokeAsync(() => cut.Find(".tour-card .btn-modern.primary").Click()); // -> step 2
        await cut.InvokeAsync(() => cut.Find(".tour-card .btn-modern.primary").Click()); // -> step 3

        await cut.InvokeAsync(() => cut.Find(".tour-card .btn-modern.secondary").Click());

        var selectors = SpotlightSelectors();
        Assert.That(selectors[^1], Is.EqualTo(".stat-grid .stat-card:last-child"),
            "Back from step 3 must re-run the spotlight on step 2's element");
        Assert.That(cut.Markup, Does.Contain("Tour 2 of 4"));
    }

    [Test]
    public async Task Tour_DoneOnLastStep_HidesSpotlightAndPersists()
    {
        var cut = _ctx.Render<Home>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Tour 1 of 4")), TimeSpan.FromSeconds(3));

        for (var i = 0; i < 4; i++)
            await cut.InvokeAsync(() => cut.Find(".tour-card .btn-modern.primary").Click());

        Assert.That(_ctx.JSInterop.Invocations["melody.hideSpotlight"].Count, Is.GreaterThanOrEqualTo(1),
            "finishing the tour hides the spotlight");
        Assert.That(cut.Markup, Does.Not.Contain("tour-card"),
            "the tour card is gone after Done");

        var factory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.That(await db.DownloaderSettings.AsNoTracking()
            .AnyAsync(s => s.Key == "tour_dismissed" && s.Value == "true"), Is.True,
            "finishing writes the persistence flag so the tour does not return");
    }

    [Test]
    public async Task Tour_EveryStep_SelectorMatchesADashboardElement()
    {
        // The selectors must point at real markup: each one appears as a
        // class/attribute the dashboard actually renders.
        var cut = _ctx.Render<Home>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Tour 1 of 4")), TimeSpan.FromSeconds(3));

        // AngleSharp applies the real CSS selectors against the rendered
        // DOM: each one must resolve to at least one element.
        var expected = new[] { ".stat-grid", ".stat-grid .stat-card:last-child", ".provider-list", "a[href='/sync-jobs']" };
        foreach (var selector in expected)
        {
            Assert.That(cut.FindAll(selector), Is.Not.Empty,
                $"{selector} must match something the dashboard renders");
        }
    }
}
