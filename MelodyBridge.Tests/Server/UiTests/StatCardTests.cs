using Bunit;
using TestContext = Bunit.TestContext;
using MelodyBridge.Server.Components.Shared;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// Rendering tests for the StatCard dashboard tile: icon, label, value
/// and the optional caption all appear in real markup, and the card
/// keeps exactly one of each element of the dashboard contract.
/// </summary>
[TestFixture]
[Category("UI")]
public class StatCardTests
{
    private TestContext _ctx = null!;

    [SetUp]
    public void Setup() => _ctx = new TestContext();

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    [Test]
    public void Renders_Icon_Label_Value_And_Caption()
    {
        var cut = _ctx.Render<StatCard>(p => p
            .Add(c => c.IconName, "list-music")
            .Add(c => c.Value, "12")
            .Add(c => c.Label, "Playlists")
            .Add(c => c.Caption, "Saved from Spotify or YouTube"));

        var card = cut.Find(".stat-card");
        Assert.That(card.QuerySelector("svg"), Is.Not.Null,
            "the icon renders inside the card");
        Assert.That(cut.Find("svg").ClassList, Does.Contain("stat-icon"),
            "the icon carries the stat-icon class");
        Assert.That(cut.Find("label").TextContent, Is.EqualTo("Playlists"),
            "the label text is visible");
        Assert.That(cut.Find("strong").TextContent, Is.EqualTo("12"),
            "the value text is visible");
        Assert.That(cut.Find("small").TextContent, Is.EqualTo("Saved from Spotify or YouTube"),
            "the caption text is visible");
    }

    [Test]
    public void Caption_Is_Optional_NoSmallWithoutIt()
    {
        var cut = _ctx.Render<StatCard>(p => p
            .Add(c => c.IconName, "download")
            .Add(c => c.Value, "7")
            .Add(c => c.Label, "Downloaded tracks"));

        Assert.That(cut.FindAll("small"), Is.Empty,
            "no caption means no small element in the markup");
        Assert.That(cut.Markup, Does.Not.Contain("<small"),
            "the raw markup holds no small tag at all");
    }

    [Test]
    public void Markup_Matches_Dashboard_Contract()
    {
        var cut = _ctx.Render<StatCard>(p => p
            .Add(c => c.IconName, "package")
            .Add(c => c.Value, "3 / 6")
            .Add(c => c.Label, "Plugins")
            .Add(c => c.Caption, "Enabled in the waterfall"));

        Assert.That(cut.FindAll(".stat-card").Count, Is.EqualTo(1),
            "exactly one stat-card root renders");
        Assert.That(cut.FindAll("label").Count, Is.EqualTo(1),
            "exactly one label renders");
        Assert.That(cut.FindAll("strong").Count, Is.EqualTo(1),
            "exactly one strong value renders");
        Assert.That(cut.FindAll("small").Count, Is.EqualTo(1),
            "exactly one caption renders");
        Assert.That(cut.Find("svg").ClassList, Does.Contain("icon"),
            "the icon svg keeps its base icon class");
    }
}
