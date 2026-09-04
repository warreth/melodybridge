using AngleSharp.Dom;
using Bunit;
using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// Interaction tests for the Plugins page against the real
/// DownloaderRegistry over a real SQLite file, so toggle and reorder
/// clicks go through the same ProviderStates persistence as production.
/// </summary>
[TestFixture]
[Category("UI")]
public class PluginsInteractionTests
{
    private Bunit.TestContext _ctx = null!;
    private TestSqliteFactory _factory = null!;
    private string _dbPath = null!;
    private DownloaderRegistry _registry = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-plugins-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        using (var db = _factory.CreateDbContext())
            db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        _ctx.Dispose(); // also disposes the page, killing its 1s timer
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
    }

    /// <summary>Wires the page DI like the real app: the real registry for
    /// IDownloaderRegistry, plain in-memory backing for the coordinator's
    /// own dependencies, which the Plugins page never touches.</summary>
    private void RegisterPage(params IDownloader[] plugins)
    {
        _registry = new DownloaderRegistry(plugins, _factory,
            NullLogger<DownloaderRegistry>.Instance);

        var dbFactory = TestHelpers.CreateInMemFactory();
        var manager = new DownloadManager(new EmptyRegistry(),
            NullLogger<DownloadManager>.Instance);
        var store = new PlaylistStore(dbFactory, Array.Empty<ISourceProvider>(),
            manager, NullLogger<PlaylistStore>.Instance);
        _ctx.Services.AddSingleton<IDownloaderRegistry>(_registry);
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(dbFactory);
        _ctx.Services.AddSingleton<IDownloadManager>(manager);
        _ctx.Services.AddSingleton(store);
        _ctx.Services.AddSingleton<DownloadCoordinator>();
        _ctx.Services.AddSingleton(new SettingsStore(dbFactory));
    }

    private IRenderedComponent<Plugins> RenderPage(params IDownloader[] plugins)
    {
        RegisterPage(plugins);
        return _ctx.Render<Plugins>();
    }

    /// <summary>The plugin card that shows the given plugin name.</summary>
    private static IElement CardFor(IRenderedComponent<Plugins> cut, string name) =>
        cut.FindAll(".plugin-card")
            .Single(c => c.QuerySelector("strong")!.TextContent.Trim() == name);

    /// <summary>Fresh read of the persisted plugin state, after any click.</summary>
    private ProviderStateRow StateFor(string id)
    {
        using var db = _factory.CreateDbContext();
        return db.ProviderStates.AsNoTracking().Single(s => s.ProviderId == id);
    }

    [Test]
    public void Toggle_Off_RemovesArrows_AndPersists()
    {
        var cut = RenderPage(
            new TestDownloader("first", "First Plugin"),
            new TestDownloader("second", "Second Plugin"));
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".plugin-card").Count, Is.EqualTo(2)),
            TimeSpan.FromSeconds(3));

        cut.Find("input[aria-label='Enable Second Plugin']").Change(false);

        cut.WaitForAssertion(() =>
            Assert.That(CardFor(cut, "Second Plugin").QuerySelector(".btn-group"),
                Is.Null, "a disabled plugin loses its reorder arrows"),
            TimeSpan.FromSeconds(3));
        Assert.That(
            CardFor(cut, "Second Plugin")
                .QuerySelector("label.toggle-switch input[type='checkbox']"),
            Is.Not.Null, "the card keeps its enable toggle");
        Assert.That(
            CardFor(cut, "First Plugin").QuerySelectorAll(".btn-group button").Count(),
            Is.EqualTo(2), "the enabled plugin keeps both arrows");
        Assert.That(StateFor("second").IsEnabled, Is.False,
            "the click persists to the ProviderStates row");
    }

    [Test]
    public void Toggle_On_RestoresArrows()
    {
        var cut = RenderPage(
            new TestDownloader("first", "First Plugin"),
            new TestDownloader("second", "Second Plugin"));
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".plugin-card").Count, Is.EqualTo(2)),
            TimeSpan.FromSeconds(3));

        var toggle = cut.Find("input[aria-label='Enable Second Plugin']");
        toggle.Change(false);
        toggle.Change(true);

        cut.WaitForAssertion(() =>
            Assert.That(
                CardFor(cut, "Second Plugin").QuerySelectorAll(".btn-group button").Count(),
                Is.EqualTo(2), "re-enabling restores both arrows"),
            TimeSpan.FromSeconds(3));
        Assert.That(StateFor("second").IsEnabled, Is.True,
            "the row is enabled again");
    }

    [Test]
    public void MoveDown_SwapsAdjacentCards_AndPersists()
    {
        var cut = RenderPage(
            new TestDownloader("first", "First Plugin"),
            new TestDownloader("second", "Second Plugin"));
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".plugin-card").Count, Is.EqualTo(2)),
            TimeSpan.FromSeconds(3));

        Assert.That(cut.Find("button[aria-label='Move First Plugin up']")
            .HasAttribute("disabled"), Is.True, "the first card cannot move up");
        var down = cut.Find("button[aria-label='Move First Plugin down']");
        Assert.That(down.HasAttribute("disabled"), Is.False,
            "the first card can move down");

        down.Click();

        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".plugin-card")
                .Select(c => c.QuerySelector("strong")!.TextContent.Trim()).ToList(),
                Is.EqualTo(new[] { "Second Plugin", "First Plugin" }),
                "clicking the down arrow swaps the adjacent cards in the DOM"),
            TimeSpan.FromSeconds(3));

        var second = StateFor("second");
        var first = StateFor("first");
        Assert.That(second.Priority, Is.LessThan(first.Priority),
            "the swap persists through SetOrderAsync");

        var cards = cut.FindAll(".plugin-card");
        Assert.That(cards[0]
            .QuerySelector("button[aria-label='Move Second Plugin up']")
            !.HasAttribute("disabled"), Is.True, "the new first card cannot move up");
        Assert.That(cards[1]
            .QuerySelector("button[aria-label='Move First Plugin down']")
            !.HasAttribute("disabled"), Is.True, "the new last card cannot move down");
    }

    [Test]
    public async Task Arrows_OnlyOnEnabledCards()
    {
        RegisterPage(
            new TestDownloader("first", "First Plugin"),
            new TestDownloader("second", "Second Plugin"));
        await _registry.SetEnabledAsync("second", false);

        var cut = _ctx.Render<Plugins>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".plugin-card").Count, Is.EqualTo(2)),
            TimeSpan.FromSeconds(3));

        Assert.That(CardFor(cut, "Second Plugin").QuerySelector(".btn-group"),
            Is.Null, "a plugin seeded disabled has no arrows");
        var enabled = CardFor(cut, "First Plugin");
        Assert.That(enabled.QuerySelectorAll(".btn-group button").Count(),
            Is.EqualTo(2), "the enabled plugin has both arrows");
        Assert.That(enabled.QuerySelector("button[aria-label='Move First Plugin up']")
            !.HasAttribute("disabled"), Is.True, "the first enabled cannot move up");
        Assert.That(enabled.QuerySelector("button[aria-label='Move First Plugin down']")
            !.HasAttribute("disabled"), Is.True, "the last enabled cannot move down");
    }

    [Test]
    public void Quality_Badges_AreSeparateSpans_WithSpaces()
    {
        var cut = RenderPage(new TestDownloader("ytdlp", "yt-dlp (YouTube)"));
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".plugin-card").Count, Is.EqualTo(1)),
            TimeSpan.FromSeconds(3));

        var badges = CardFor(cut, "yt-dlp (YouTube)")
            .QuerySelectorAll("span.quality-badge")
            .Select(b => b.TextContent.Trim()).ToList();
        Assert.That(badges.Count, Is.EqualTo(3), "one span per badge, no fusing");
        Assert.That(badges, Is.EqualTo(new[]
        {
            "up to 160 kbps (opus)", "re-encodes to any cap", "widest catalog",
        }), "each badge keeps its own text with real spaces");
    }

    [Test]
    public void StatusPill_HasTooltip()
    {
        var cut = RenderPage(
            new TestDownloader("first", "First Plugin"),
            new UnavailableDownloader("second", "Second Plugin"));
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".plugin-card").Count, Is.EqualTo(2)),
            TimeSpan.FromSeconds(3));

        var readyPill = CardFor(cut, "First Plugin").QuerySelector("span.pill")!;
        Assert.That(readyPill.GetAttribute("title"),
            Is.EqualTo("Checked and reachable"),
            "the ready pill explains what the check proved");
        Assert.That(readyPill.TextContent.Trim(), Is.EqualTo("ready"));

        var unavailablePill = CardFor(cut, "Second Plugin").QuerySelector("span.pill")!;
        Assert.That(unavailablePill.GetAttribute("title"),
            Is.EqualTo("Could not reach this plugin"),
            "the unavailable pill explains the failure");
        Assert.That(unavailablePill.TextContent.Trim(), Is.EqualTo("unavailable"));
    }

    [Test]
    public void Toggle_AriaLabel_Present()
    {
        var cut = RenderPage(
            new TestDownloader("first", "First Plugin"),
            new TestDownloader("second", "Second Plugin"));
        cut.WaitForAssertion(() =>
        {
            var toggles = cut.FindAll(
                ".plugin-card label.toggle-switch input[type='checkbox']");
            Assert.That(toggles.Count, Is.EqualTo(2));
            foreach (var toggle in toggles)
                Assert.That(
                    string.IsNullOrWhiteSpace(toggle.GetAttribute("aria-label")),
                    Is.False, "every toggle announces itself to screen readers");
        }, TimeSpan.FromSeconds(3));
    }

    /// <summary>TestDownloader that fails its availability check.</summary>
    private sealed class UnavailableDownloader(string id, string name)
        : TestDownloader(id, name), IDownloader
    {
        Task<bool> IDownloader.IsAvailableAsync(CancellationToken ct)
            => Task.FromResult(false);
    }
}
