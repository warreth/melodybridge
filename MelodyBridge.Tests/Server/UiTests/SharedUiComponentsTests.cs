using TestContext = Bunit.TestContext;
using Bunit;
using Microsoft.AspNetCore.Components;
using MelodyBridge.Server.Components.Shared;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// Rendering tests for the shared design-system components: icons,
/// brand marks, status pills, empty states, skeletons and the card
/// action menu. Every assertion runs against real rendered markup.
/// </summary>
[TestFixture]
[Category("UI")]
public class SharedUiComponentsTests
{
    private TestContext _ctx = null!;

    [SetUp]
    public void Setup() => _ctx = new TestContext();

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    // ── Icon ────────────────────────────────────────────────────

    [Test]
    public void Icon_RendersSvgElement()
    {
        var cut = _ctx.Render<Icon>(p => p
            .Add(i => i.Name, "music")
            .Add(i => i.Size, 20));

        Assert.That(cut.Markup, Does.StartWith("<svg"), "an inline svg renders");
        Assert.That(cut.Find("svg").GetAttribute("width"), Is.EqualTo("20"), "the svg carries the size");
        Assert.That(cut.Markup, Does.Contain("<path"), "the music icon draws its paths");
        Assert.That(cut.Find("svg").GetAttribute("aria-hidden"), Is.EqualTo("true"), "decorative icons are hidden from screen readers");
    }

    [Test]
    public void Icon_UnknownName_FallsBackWithoutThrowing()
    {
        var cut = _ctx.Render<Icon>(p => p.Add(i => i.Name, "definitely-not-an-icon"));

        Assert.That(cut.Markup, Does.Contain("<path"), "the fallback check mark renders");
    }

    [Test]
    public void Icon_CustomClass_IsApplied()
    {
        var cut = _ctx.Render<Icon>(p => p
            .Add(i => i.Name, "download")
            .Add(i => i.Class, "stat-icon"));

        Assert.That(cut.Find("svg").ClassList, Does.Contain("stat-icon"), "the class reaches the svg");
    }

    // ── BrandIcon ───────────────────────────────────────────────

    [Test]
    public void BrandIcon_Spotify_RendersBrandPath()
    {
        var cut = _ctx.Render<BrandIcon>(p => p
            .Add(b => b.Name, "spotify")
            .Add(b => b.Size, 24));

        Assert.That(cut.Find("svg").GetAttribute("width"), Is.EqualTo("24"));
        Assert.That(cut.Find("path"), Is.Not.Null, "the spotify mark is a real path");
        Assert.That(cut.Find("svg").GetAttribute("style"), Does.Contain("1DB954"), "brand color travels with the mark");
    }

    [Test]
    public void BrandIcon_MonogramBrands_RenderTileAndLetter()
    {
        foreach (var (name, letter) in new[]
                 {
                     ("jellyfin", "J"), ("plex", "P"), ("navidrome", "N"), ("flaresolverr", "F"),
                 })
        {
            var cut = _ctx.Render<BrandIcon>(p => p.Add(b => b.Name, name));

            Assert.That(cut.Find("rect"), Is.Not.Null, $"{name} draws a tile");
            var text = cut.Find("text");
            Assert.That(text.TextContent.Trim(), Is.EqualTo(letter), $"{name} carries its initial");
        }
    }

    // ── StatusPill ──────────────────────────────────────────────

    [Test]
    public void StatusPill_OkVariant_RendersOkClass()
    {
        var cut = _ctx.Render<StatusPill>(p => p
            .Add(s => s.Variant, "ok")
            .Add(s => s.ChildContent, "connected"));

        var span = cut.Find("span.pill");
        Assert.That(span.ClassList, Does.Contain("ok"), "the ok variant maps to the ok class");
        Assert.That(span.TextContent, Does.Contain("connected"), "the label text renders");
    }

    [Test]
    public void StatusPill_FriendlyAliases_MapToClasses()
    {
        foreach (var (variant, expected) in new[]
                 {
                     ("success", "ok"), ("ready", "ok"), ("connected", "ok"),
                     ("in_progress", "warn"), ("warning", "warn"),
                     ("failed", "err"), ("unavailable", "err"), ("error", "err"),
                     ("guest", "neutral"), ("pending", "neutral"),
                     ("running", "info"),
                 })
        {
            var cut = _ctx.Render<StatusPill>(p => p
                .Add(s => s.Variant, variant)
                .Add(s => s.ChildContent, "x"));

            Assert.That(cut.Find("span.pill").ClassList, Does.Contain(expected),
                $"{variant} must map to {expected}");
        }
    }

    [Test]
    public void StatusPill_UnknownVariant_FallsBackToNeutral()
    {
        var cut = _ctx.Render<StatusPill>(p => p
            .Add(s => s.Variant, "something-new")
            .Add(s => s.ChildContent, "x"));

        Assert.That(cut.Find("span.pill").ClassList, Does.Contain("neutral"));
        Assert.That(cut.Find("span.pill").ClassList, Has.None.EqualTo("ok"), "no accidental status color");
    }

    [Test]
    public void StatusPill_Pulse_RendersAnimatedDot()
    {
        var cut = _ctx.Render<StatusPill>(p => p
            .Add(s => s.Variant, "warn")
            .Add(s => s.Pulse, true)
            .Add(s => s.ChildContent, "in progress"));

        Assert.That(cut.Find("span.pill").ClassList, Does.Contain("pulse"), "the pulse class marks the pill");
        var dot = cut.Find("span.dot");
        Assert.That(dot.GetAttribute("aria-hidden"), Is.EqualTo("true"), "the dot is decorative");
    }

    [Test]
    public void StatusPill_PulseOff_HasNoDot()
    {
        var cut = _ctx.Render<StatusPill>(p => p
            .Add(s => s.Variant, "ok")
            .Add(s => s.ChildContent, "ready"));

        Assert.That(cut.FindAll("span.dot"), Is.Empty, "no dot without pulse");
    }

    [Test]
    public void StatusPill_Title_BecomesHoverHint()
    {
        var cut = _ctx.Render<StatusPill>(p => p
            .Add(s => s.Variant, "err")
            .Add(s => s.Title, "The download failed twice")
            .Add(s => s.ChildContent, "failed"));

        Assert.That(cut.Find("span.pill").GetAttribute("title"),
            Is.EqualTo("The download failed twice"));
    }

    // ── EmptyState ─────────────────────────────────────────────

    [Test]
    public void EmptyState_ShowsIconTitleAndDescription()
    {
        var cut = _ctx.Render<EmptyState>(p => p
            .Add(e => e.IconName, "check-circle")
            .Add(e => e.Title, "No errors")
            .Add(e => e.ChildContent, "<p>Everything ran clean.</p>"));

        Assert.That(cut.Find(".empty-state"), Is.Not.Null);
        Assert.That(cut.Find("svg"), Is.Not.Null, "the empty state carries an icon");
        Assert.That(cut.Find(".empty-title").TextContent, Is.EqualTo("No errors"));
        Assert.That(cut.Find(".empty-desc").TextContent.Trim(), Does.Contain("Everything ran clean."));
    }

    [Test]
    public void EmptyState_ActionsRender()
    {
        var cut = _ctx.Render<EmptyState>(p => p
            .Add(e => e.Title, "No playlists yet")
            .Add(e => e.Actions, "<a href='/playlists'>Add one</a>"));

        Assert.That(cut.Find(".empty-actions a").GetAttribute("href"), Is.EqualTo("/playlists"));
    }

    // ── Skeleton ───────────────────────────────────────────────

    [Test]
    public void Skeleton_Rows_RendersRequestedLineCount()
    {
        var cut = _ctx.Render<Skeleton>(p => p
            .Add(s => s.Kind, "rows")
            .Add(s => s.Rows, 4));

        Assert.That(cut.FindAll(".skeleton-line").Count(), Is.EqualTo(4), "one line per row");
        Assert.That(cut.FindAll(".skeleton-line.w-60").Count(), Is.EqualTo(2), "alternate lines are shorter");
    }

    [Test]
    public void Skeleton_Cards_RendersCardGrid()
    {
        var cut = _ctx.Render<Skeleton>(p => p
            .Add(s => s.Kind, "cards")
            .Add(s => s.Rows, 2));

        Assert.That(cut.FindAll(".skeleton-card").Count(), Is.EqualTo(2));
        Assert.That(cut.Find(".skeleton-grid"), Is.Not.Null, "cards live in the grid wrapper");
    }

    [Test]
    public void Skeleton_Table_RendersRowCells()
    {
        var cut = _ctx.Render<Skeleton>(p => p
            .Add(s => s.Kind, "table")
            .Add(s => s.Rows, 3));

        Assert.That(cut.FindAll(".skeleton-row").Count(), Is.EqualTo(3));
        Assert.That(cut.FindAll(".skeleton-row").First().QuerySelectorAll(".skeleton-cell").Length,
            Is.EqualTo(5), "each skeleton row has five cells");
    }

    [Test]
    public void Skeleton_UnknownKind_FallsBackToRows()
    {
        var cut = _ctx.Render<Skeleton>(p => p
            .Add(s => s.Kind, "nonsense")
            .Add(s => s.Rows, 2));

        Assert.That(cut.FindAll(".skeleton-line").Count(), Is.EqualTo(2), "unknown kinds render rows");
    }

    // ── CardMenu ───────────────────────────────────────────────

    [Test]
    public void CardMenu_RendersTriggerWithLabel()
    {
        var cut = _ctx.Render<CardMenu>(p => p
            .Add(m => m.Label, "Playlist actions")
            .Add(m => m.ChildContent, "<button>Remove</button>"));

        var details = cut.Find("details.card-menu");
        Assert.That(details, Is.Not.Null, "the menu is a details element");
        var summary = cut.Find("summary.card-menu-trigger");
        Assert.That(summary.GetAttribute("aria-label"), Is.EqualTo("Playlist actions"));
    }

    [Test]
    public void CardMenu_ItemClick_FiresItsOwnHandler()
    {
        var clicked = false;
        RenderFragment items = builder =>
        {
            builder.OpenElement(0, "button");
            builder.AddAttribute(1, "class", "danger");
            builder.AddAttribute(2, "onclick",
                EventCallback.Factory.Create(this, () => clicked = true));
            builder.AddContent(3, "Remove");
            builder.CloseElement();
        };
        var cut = _ctx.Render<CardMenu>(p => p.Add(m => m.ChildContent, items));

        cut.Find("button.danger").Click();

        Assert.That(clicked, Is.True, "the menu item click runs the real handler");
        Assert.That(cut.FindAll("button").Count(), Is.EqualTo(1), "exactly the item button renders");
    }
}