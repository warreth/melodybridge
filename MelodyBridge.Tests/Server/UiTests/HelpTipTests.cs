using Bunit;
using MelodyBridge.Server.Components.Shared;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The shared docs help button: a question mark next to a feature that
/// explains it on hover or focus and opens the matching page on
/// docs.melodybridge.app when clicked. Rendered for real; every
/// assertion reads the produced markup.
/// </summary>
[TestFixture]
[Category("UI")]
public class HelpTipTests
{
    private Bunit.TestContext _ctx = null!;

    [SetUp]
    public void Setup() => _ctx = new Bunit.TestContext();

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    [Test]
    public void HelpTip_LinksToTheDocsAnchorInANewTab()
    {
        var cut = _ctx.Render<HelpTip>(p => p
            .Add(h => h.Path, "concepts#the-waterfall")
            .Add(h => h.Text, "Routing explained."));

        var link = cut.Find("a.help-tip");
        Assert.That(link.GetAttribute("href"),
            Is.EqualTo("https://docs.melodybridge.app/concepts#the-waterfall"),
            "the deep link points at the exact docs section");
        Assert.That(link.GetAttribute("target"), Is.EqualTo("_blank"),
            "the docs open in a new tab");
        Assert.That(link.GetAttribute("rel"), Is.EqualTo("noopener"));
    }

    [Test]
    public void HelpTip_ShowsTheExplanationInAPopup()
    {
        var cut = _ctx.Render<HelpTip>(p => p
            .Add(h => h.Path, "publishing")
            .Add(h => h.Text, "Sync jobs publish your playlists."));

        Assert.That(cut.Find(".help-tip-popup").TextContent,
            Does.Contain("Sync jobs publish your playlists."),
            "hover and focus show the short explanation");
        Assert.That(cut.Markup, Does.Contain("Open the docs"),
            "the popup says where a click goes");
        Assert.That(cut.FindAll("svg"), Has.Count.EqualTo(2),
            "the question mark and the external-link mark render as icons");
    }

    [Test]
    public void HelpTip_CarriesAnAccessibleLabel()
    {
        var cut = _ctx.Render<HelpTip>(p => p
            .Add(h => h.Path, "accounts")
            .Add(h => h.Text, "Read-only logins."));

        var label = cut.Find("a.help-tip").GetAttribute("aria-label");
        Assert.That(label, Does.Contain("Help: Read-only logins."),
            "screen readers get the explanation");
        Assert.That(label, Does.Contain("Opens the docs in a new tab."),
            "and the click behaviour");
    }

    [Test]
    public void HelpTip_LeadingSlashPath_DoesNotDoubleTheSlash()
    {
        var cut = _ctx.Render<HelpTip>(p => p
            .Add(h => h.Path, "/lucida")
            .Add(h => h.Text, "Solver setup."));

        Assert.That(cut.Find("a.help-tip").GetAttribute("href"),
            Is.EqualTo("https://docs.melodybridge.app/lucida"));
    }
}
