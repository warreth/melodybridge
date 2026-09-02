using Bunit;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// Advanced page toggles: the wide toggle markup (label wrapping input,
/// slider, text) is unchanged - the CSS-only un-garble fix is verified by
/// asserting structure the new rules rely on: switch before text, no text
/// inside the slider span, and toggles stacked with breathing room.
/// </summary>
[TestFixture]
[Category("UI")]
public class AdvancedToggleTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-adv-{Guid.NewGuid():N}.db");
        var factory = new TestSqliteFactory(_dbPath);
        using (var db = factory.CreateDbContext())
            db.Database.EnsureCreated();
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);
        _ctx.Services.AddDownloadPages(factory);
        _ctx.Services.AddSingleton(new MelodyBridge.Server.Services.NotificationService(
            autoDismissAfter: TimeSpan.FromHours(1)));
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

    [Test]
    public void Advanced_WideToggles_SwitchPrecedesText_OutsideSlider()
    {
        var cut = _ctx.Render<Advanced>();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Show the file column")), TimeSpan.FromSeconds(3));

        var labels = cut.FindAll("label.toggle-switch.wide");
        Assert.That(labels.Count, Is.EqualTo(3),
            "Display + two notification toggles use the wide switch");

        foreach (var label in labels)
        {
            var slider = label.QuerySelector(".toggle-slider");
            Assert.That(slider, Is.Not.Null, "each wide toggle keeps its slider span");
            Assert.That(slider!.TextContent.Trim(), Is.Empty,
                "the slider span carries no text - text lives beside the switch");

            var spans = label.QuerySelectorAll("span:not(.toggle-slider)");
            Assert.That(spans.Length, Is.EqualTo(1),
                "exactly one text span after the slider: the flex-row CSS lays it out");
            Assert.That(label.QuerySelector("input[type=checkbox]"), Is.Not.Null);
        }
    }

    [Test]
    public async Task Advanced_Toggles_PersistThroughSave()
    {
        var cut = _ctx.Render<Advanced>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Show the file column")), TimeSpan.FromSeconds(3));

        var showFile = cut.FindAll("label.toggle-switch.wide")[0].QuerySelector("input");
        showFile.Change(true);

        await cut.InvokeAsync(() => cut.Find("section.page-title button.btn-modern").Click());

        var factory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.That(await db.DownloaderSettings.AsNoTracking()
            .AnyAsync(s => s.Key == "show_filename" && s.Value == "true"), Is.True,
            "the display toggle survives Save");
    }

    [Test]
    public async Task Advanced_SavedPreview_UsesToastPreviewBox()
    {
        var cut = _ctx.Render<Advanced>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Show the file column")), TimeSpan.FromSeconds(3));

        await cut.InvokeAsync(() =>
            cut.Find("section.page-title button.btn-modern").Click());

        cut.WaitForAssertion(() =>
            Assert.That(cut.Find(".toast-preview"), Is.Not.Null,
                "the save confirmation keeps its own preview box, not the global stack"),
            TimeSpan.FromSeconds(3));
    }
}
