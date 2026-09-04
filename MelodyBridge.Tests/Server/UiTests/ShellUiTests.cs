using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Server.Components;
using MelodyBridge.Server.Components.Layout;
using Microsoft.Extensions.DependencyInjection;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// Global shell contracts: the sidebar renders exactly once, the
/// version badge evaluates the real version instead of the raw
/// template, every nav link resolves to a real route, and the host
/// page ships the framework error and reconnect markup.
/// </summary>
[TestFixture]
[Category("UI")]
public class ShellUiTests
{
    private Bunit.TestContext _ctx = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _ctx.Services.AddSingleton(new MelodyBridge.Server.Services.DevPanelService());
        _ctx.Services.AddSingleton(new MelodyBridge.Server.Services.NotificationService());
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    [Test]
    public void VersionBadge_EvaluatesInsteadOfRenderingTemplate()
    {
        var cut = _ctx.Render<MainLayout>();

        var badge = cut.Find(".version-badge");
        Assert.That(badge.TextContent, Does.StartWith("v" + AppInfo.Version),
            "the sidebar footer shows the evaluated version, never the raw template");
        Assert.That(badge.TextContent, Does.Not.Contain("@AppInfo"),
            "no Razor template syntax may leak into the rendered shell");
    }

    [Test]
    public void MainLayout_RendersExactlyOneNavigation()
    {
        var cut = _ctx.Render<MainLayout>();

        Assert.That(cut.FindAll("nav.app-nav").Count(), Is.EqualTo(1),
            "the shell owns the single navigation; no page may nest another");
        Assert.That(cut.FindAll("aside.app-sidebar").Count(), Is.EqualTo(1),
            "one sidebar, one shell");
    }

    [Test]
    public void NavMenu_EveryLinkResolvesToARoute()
    {
        var nav = _ctx.Render<NavMenu>();
        // Route table of the app component assembly: every @page
        // directive becomes one entry here.
        var routes = typeof(App).Assembly
            .GetTypes()
            .SelectMany(t => t.GetCustomAttributes(false))
            .OfType<Microsoft.AspNetCore.Components.RouteAttribute>()
            .Select(r => r.Template)
            .ToList();
        Assert.That(routes, Is.Not.Empty, "the route table must be discoverable");

        foreach (var link in nav.FindAll(".nav-link"))
        {
            var href = link.GetAttribute("href");
            var target = string.IsNullOrEmpty(href) ? "/" : href;
            var isTemplate = target.Contains('{');
            Assert.That(isTemplate || routes.Contains(target), Is.True,
                $"nav target {target} must exist in the route table");
        }
    }

    [Test]
    public void NavMenu_DevPanelLink_FollowsToggle()
    {
        var devPanel = _ctx.Services.GetRequiredService<MelodyBridge.Server.Services.DevPanelService>();
        var nav = _ctx.Render<NavMenu>();

        Assert.That(nav.FindAll(".nav-link").Any(l => l.GetAttribute("href") == "/dev"),
            Is.False, "the dev panel stays hidden while disabled");

        devPanel.Enabled = true;
        var navWithDev = _ctx.Render<NavMenu>();
        Assert.That(navWithDev.FindAll(".nav-link").Any(l => l.GetAttribute("href") == "/dev"),
            Is.True, "the dev panel appears when the service is enabled");
        devPanel.Enabled = false;
    }

    [Test]
    public void HostPage_ShipsFrameworkErrorAndReconnectMarkup()
    {
        var host = File.ReadAllText(Path.Combine(
            RepoRoot.Find(), "MelodyBridge.Server", "Pages", "_Host.cshtml"));

        Assert.That(host, Does.Contain("id=\"blazor-error-ui\""),
            "the framework error banner div lives in the host page");
        Assert.That(host, Does.Contain("class=\"reload\""),
            "the error banner keeps its reload link");
        Assert.That(host, Does.Contain("class=\"dismiss\""),
            "the error banner keeps a dismiss control");
        Assert.That(host, Does.Contain("id=\"components-reconnect-modal\""),
            "the reconnect dialog markup ships statically so it survives circuit loss");
        Assert.That(host, Does.Contain("components-reconnect-button"),
            "the reconnect retry button ships in the host page");
    }

    private static class RepoRoot
    {
        public static string Find()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
                dir = dir.Parent!;
            return dir!.FullName;
        }
    }
}
