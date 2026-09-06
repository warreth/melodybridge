using Bunit;
using MelodyBridge.Server.Components.Shared;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The shared TabStrip on its own: active marking, aria state, click
/// routing. Uses no services, just renders the component.
/// </summary>
[TestFixture]
[Category("UI")]
public class TabStripTests
{
    private Bunit.TestContext _ctx = null!;

    private static readonly TabStrip.Tab[] Tabs =
    [
        new("accounts", "Accounts"),
        new("about", "About"),
    ];

    [SetUp]
    public void Setup() => _ctx = new Bunit.TestContext();

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    [Test]
    public void Marks_Active_Tab_With_Class_And_Aria()
    {
        var cut = _ctx.Render<TabStrip>(p => p
            .Add(t => t.Tabs, Tabs)
            .Add(t => t.ActiveId, "accounts"));

        var active = cut.FindAll("button.tab-link.active");
        Assert.That(active.Count, Is.EqualTo(1));
        Assert.That(active[0].TextContent.Trim(), Is.EqualTo("Accounts"));
        Assert.That(active[0].GetAttribute("aria-current"), Is.EqualTo("tab"));
        Assert.That(active[0].GetAttribute("aria-selected"), Is.EqualTo("true"));

        var inactive = cut.FindAll("button.tab-link").Single(b => b.TextContent.Trim() == "About");
        Assert.That(inactive.GetAttribute("aria-selected"), Is.EqualTo("false"));
        Assert.That(inactive.GetAttribute("aria-current"), Is.Null);
    }

    [Test]
    public void Reports_Tab_Clicks()
    {
        string? clicked = null;
        var cut = _ctx.Render<TabStrip>(p => p
            .Add(t => t.Tabs, Tabs)
            .Add(t => t.ActiveId, "accounts")
            .Add(t => t.OnSelect, id => clicked = id));

        cut.FindAll("button.tab-link").Single(b => b.TextContent.Trim() == "About").Click();

        Assert.That(clicked, Is.EqualTo("about"));
    }
}
