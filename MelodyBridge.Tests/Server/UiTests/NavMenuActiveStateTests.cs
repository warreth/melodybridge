using Bunit;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Layout;
using MelodyBridge.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NavigationManager = Microsoft.AspNetCore.Components.NavigationManager;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The sidebar's current-page marker: NavLink renders .active on the
/// link matching the current route and nothing else, so the existing
/// accent-soft CSS paints exactly one entry. The dashboard link must
/// only light up on an exact "/" match, not on every page.
/// </summary>
[TestFixture]
[Category("UI")]
public class NavMenuActiveStateTests
{
    private Bunit.TestContext _ctx = null!;
    private readonly List<string> _dbs = new();

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        var dbPath = Path.Combine(Path.GetTempPath(), $"mb-nav-{Guid.NewGuid():N}.db");
        _dbs.Add(dbPath);
        var factory = new TestSqliteFactory(dbPath);
        using (var db = factory.CreateDbContext())
            db.Database.EnsureCreated();
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);
        _ctx.Services.AddSingleton(new DevPanelService());
    }

    [TearDown]
    public void TearDown()
    {
        _ctx.Dispose();
        foreach (var dbPath in _dbs)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try { File.Delete(dbPath + suffix); } catch { /* best effort */ }
            }
        }
        _dbs.Clear();
    }

    private static string[] ActiveHrefs(IRenderedComponent<NavMenu> nav)
        => nav.FindAll("a.nav-link.active")
            .Select(a => a.GetAttribute("href"))
            .ToArray()!;

    private IRenderedComponent<NavMenu> RenderNavAt(string uri)
    {
        var nav = _ctx.Render<NavMenu>();
        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(uri);
        return nav;
    }

    [Test]
    public void PlaylistsRoute_MarksOnlyThePlaylistsLink()
    {
        var nav = RenderNavAt("/playlists");

        Assert.That(ActiveHrefs(nav), Is.EqualTo(new[] { "/playlists" }),
            "exactly the playlists entry carries the active class");
    }

    [Test]
    public void SettingsRoute_MarksOnlyTheSettingsLink()
    {
        var nav = RenderNavAt("/settings");

        Assert.That(ActiveHrefs(nav), Is.EqualTo(new[] { "/settings" }));
    }

    [Test]
    public void LogsRoute_MarksOnlyTheLogsLink()
    {
        var nav = RenderNavAt("/logs");

        Assert.That(ActiveHrefs(nav), Is.EqualTo(new[] { "/logs" }));
    }

    [Test]
    public void RootRoute_MarksOnlyTheDashboardLink()
    {
        var nav = RenderNavAt("/");

        Assert.That(ActiveHrefs(nav), Is.EqualTo(new[] { "" }),
            "the dashboard entry (href empty, Match All) is the one active link");
    }

    [Test]
    public void DeepRoute_MarksItsSectionLink()
    {
        // A playlist details page sits under the Playlists section:
        // NavLink's prefix match keeps the section entry lit, so the
        // sidebar still answers "you are in Your music".
        var nav = RenderNavAt("/playlists/pl-123");

        Assert.That(ActiveHrefs(nav), Is.EqualTo(new[] { "/playlists" }),
            "the section link stays marked on its child pages");
    }

    [Test]
    public void Navigating_MovesTheActiveLink()
    {
        var nav = RenderNavAt("/playlists");

        _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo("/library");

        Assert.That(ActiveHrefs(nav), Is.EqualTo(new[] { "/library" }),
            "the marker follows the route, no reload needed");
    }

    [Test]
    public void ActiveLink_KeepsTheIconInsideIt()
    {
        // The .active CSS colors the icon through the link; the svg must
        // stay a child of the marked anchor.
        var nav = RenderNavAt("/plugins");

        var active = nav.FindAll("a.nav-link.active").Single();
        Assert.That(active.QuerySelector(".nav-icon"), Is.Not.Null,
            "the entry icon travels with the active link");
        Assert.That(active.TextContent, Does.Contain("Plugins"));
    }
}
