using TestContext = Bunit.TestContext;
using Bunit;
using MelodyBridge.Server.Components.Shared;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// Rendering tests for ConnectionRow, the one data-driven row used by
/// every dashboard connection entry. Every assertion runs against the
/// real rendered markup.
/// </summary>
[TestFixture]
[Category("UI")]
public class ConnectionRowTests
{
    private TestContext _ctx = null!;

    [SetUp]
    public void Setup() => _ctx = new TestContext();

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    [Test]
    public void Renders_Title_Detail_And_PillText()
    {
        var cut = _ctx.Render<ConnectionRow>(p => p
            .Add(r => r.IconName, "jellyfin")
            .Add(r => r.Title, "Jellyfin")
            .Add(r => r.Detail, "http://192.168.1.20:8096")
            .Add(r => r.PillVariant, "ready")
            .Add(r => r.PillText, "ready")
            .Add(r => r.Enabled, true));

        var info = cut.Find(".connection-info");
        Assert.That(info.QuerySelector("strong")!.TextContent, Is.EqualTo("Jellyfin"), "the title renders in bold");
        Assert.That(info.QuerySelector("small")!.TextContent, Is.EqualTo("http://192.168.1.20:8096"), "the detail line renders the URL");
        Assert.That(cut.Find(".pill").TextContent, Is.EqualTo("ready"), "the pill shows its text");
    }

    [Test]
    public void Enabled_True_AddsEnabledClass_False_DoesNot()
    {
        var on = _ctx.Render<ConnectionRow>(p => p
            .Add(r => r.IconName, "plex")
            .Add(r => r.Title, "Plex")
            .Add(r => r.Detail, "not configured")
            .Add(r => r.PillVariant, "neutral")
            .Add(r => r.PillText, "off")
            .Add(r => r.Enabled, true));

        Assert.That(on.Find(".connection-row").ClassList, Does.Contain("enabled"), "enabled=true paints the accent class");

        var off = _ctx.Render<ConnectionRow>(p => p
            .Add(r => r.IconName, "plex")
            .Add(r => r.Title, "Plex")
            .Add(r => r.Detail, "not configured")
            .Add(r => r.PillVariant, "neutral")
            .Add(r => r.PillText, "off")
            .Add(r => r.Enabled, false));

        Assert.That(off.Find(".connection-row").ClassList, Does.Not.Contain("enabled"), "enabled=false leaves the class off");
    }

    [TestCase("connected", "ok")]
    [TestCase("guest", "neutral")]
    [TestCase("ok", "ok")]
    [TestCase("ready", "ok")]
    [TestCase("neutral", "neutral")]
    public void Pill_Variant_MapsToFriendlyPillClass(string variant, string expected)
    {
        var cut = _ctx.Render<ConnectionRow>(p => p
            .Add(r => r.IconName, "navidrome")
            .Add(r => r.Title, "Navidrome")
            .Add(r => r.Detail, "not configured")
            .Add(r => r.PillVariant, variant)
            .Add(r => r.PillText, variant));

        Assert.That(cut.Find(".pill").ClassList, Does.Contain(expected),
            $"variant {variant} maps to the pill class {expected}");
    }

    [Test]
    public void Row_HasThreeChildren_Icon_Info_Pill()
    {
        var cut = _ctx.Render<ConnectionRow>(p => p
            .Add(r => r.IconName, "flaresolverr")
            .Add(r => r.Title, "FlareSolverr")
            .Add(r => r.Detail, "Cloudflare bypass off")
            .Add(r => r.PillVariant, "neutral")
            .Add(r => r.PillText, "off"));

        var row = cut.Find(".connection-row");
        Assert.That(row.Children.Length, Is.EqualTo(3), "icon, info and pill are the only three children");
        Assert.That(cut.FindAll("svg.brand-icon.connection-icon").Count, Is.EqualTo(1), "the brand icon svg renders");
        Assert.That(cut.FindAll(".connection-info").Count, Is.EqualTo(1), "the info block renders");
        Assert.That(cut.FindAll(".pill").Count, Is.EqualTo(1), "the status pill renders");
    }
}
