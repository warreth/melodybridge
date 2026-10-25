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
        Assert.That(labels.Count, Is.EqualTo(4),
            "Advanced mode + dedup + display + database logs use the wide switch; the toast and ntfy switches moved to Settings - Notifications");

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

        var showFile = cut.FindAll("label.toggle-switch.wide")
            .Single(l => l.TextContent.Contains("Show the file column"))
            .QuerySelector("input");
        showFile.Change(true);

        await cut.InvokeAsync(() => cut.Find("section.page-title button.btn-modern").Click());

        var factory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.That(await db.DownloaderSettings.AsNoTracking()
            .AnyAsync(s => s.Key == "show_filename" && s.Value == "true"), Is.True,
            "the display toggle survives Save");
    }

    [Test]
    public async Task Advanced_DatabaseLogToggle_OffByDefault_SavesAndFlipsTheSwitch()
    {
        // The default matters: SQL chatter must not crowd the Logs page
        // unless someone turned it on for a debugging session.
        MelodyBridge.Server.Services.DatabaseLogSwitch.Set(false);
        var cut = _ctx.Render<Advanced>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Show database activity in the logs")),
            TimeSpan.FromSeconds(3));

        var dbToggle = cut.FindAll("label.toggle-switch.wide")
            .Single(l => l.TextContent.Contains("Show database activity in the logs"))
            .QuerySelector("input");
        Assert.That(dbToggle.HasAttribute("checked"), Is.False,
            "database activity logging starts off for a fresh install");

        dbToggle.Change(true);
        await cut.InvokeAsync(() => cut.Find("section.page-title button.btn-modern").Click());

        cut.WaitForAssertion(() =>
            Assert.That(MelodyBridge.Server.Services.DatabaseLogSwitch.Enabled, Is.True,
                "saving the toggle applies it immediately, no restart needed"),
            TimeSpan.FromSeconds(3));

        var factory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.That(await db.DownloaderSettings.AsNoTracking()
            .AnyAsync(s => s.Key == "log_database_activity" && s.Value == "true"), Is.True,
            "the database logs toggle survives Save");

        MelodyBridge.Server.Services.DatabaseLogSwitch.Set(false);
    }

    [Test]
    public async Task Advanced_AdvancedMode_PersistsAndOpensTheDevPanel()
    {
        var factory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity
            {
                Key = MelodyBridge.Server.Services.DevPanelService.AdvancedSettingKey,
                Value = "true",
            });
            await db.SaveChangesAsync();
        }
        var panel = _ctx.Services.GetRequiredService<MelodyBridge.Server.Services.DevPanelService>();
        panel.Enabled = true;

        var cut = _ctx.Render<Advanced>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Show developer tools")), TimeSpan.FromSeconds(3));

        var toggle = cut.FindAll("label.toggle-switch.wide")
            .Single(l => l.TextContent.Contains("Show developer tools"))
            .QuerySelector("input")!;
        Assert.Multiple(() =>
        {
            Assert.That(toggle.HasAttribute("checked"), Is.True,
                "the stored preference loads as checked");
            Assert.That(cut.Markup, Does.Contain("Open the Dev panel"),
                "with advanced mode on, the panel is one click away");
        });

        // Off: the link goes away at once and the row follows.
        toggle.Change(false);
        await cut.InvokeAsync(() => cut.Find("section.page-title button.btn-modern").Click());
        Assert.That(panel.Enabled, Is.False, "turning advanced mode off closes the panel without a restart");
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Not.Contain("Open the Dev panel")), TimeSpan.FromSeconds(3));
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.That(await db.DownloaderSettings.AsNoTracking().AnyAsync(s =>
                s.Key == MelodyBridge.Server.Services.DevPanelService.AdvancedSettingKey && s.Value == "false"),
                Is.True, "the off choice survives a restart");
        }

        // On again: the same switch, both ways.
        cut.FindAll("label.toggle-switch.wide")
            .Single(l => l.TextContent.Contains("Show developer tools"))
            .QuerySelector("input")!
            .Change(true);
        await cut.InvokeAsync(() => cut.Find("section.page-title button.btn-modern").Click());
        Assert.That(panel.Enabled, Is.True);
        await using (var db = await factory.CreateDbContextAsync())
        {
            Assert.That(await db.DownloaderSettings.AsNoTracking().AnyAsync(s =>
                s.Key == MelodyBridge.Server.Services.DevPanelService.AdvancedSettingKey && s.Value == "true"),
                Is.True);
        }
    }

    [Test]
    public async Task Advanced_Save_ReportsThroughTheSharedToastStack()
    {
        var cut = _ctx.Render<Advanced>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Show the file column")), TimeSpan.FromSeconds(3));

        var notifications = _ctx.Services
            .GetRequiredService<MelodyBridge.Server.Services.NotificationService>();

        await cut.InvokeAsync(() =>
            cut.Find("section.page-title button.btn-modern").Click());

        cut.WaitForAssertion(() =>
            Assert.That(notifications.Snapshot().Any(n => n.Message == "Settings saved."), Is.True,
                "the save confirmation rides the shared toast stack, which dismisses itself"),
            TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task Advanced_TimeBudgets_LoadDefaults_PersistThroughSave()
    {
        var cut = _ctx.Render<Advanced>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Plugin time budget")), TimeSpan.FromSeconds(3));

        // The documented defaults render as-is.
        var pluginInput = cut.FindAll("input[type=number]")
            .Single(i => i.ParentElement!.TextContent.Contains("Plugin time budget"));
        var trackInput = cut.FindAll("input[type=number]")
            .Single(i => i.ParentElement!.TextContent.Contains("Track time budget"));
        Assert.Multiple(() =>
        {
            Assert.That(pluginInput.GetAttribute("value"), Is.EqualTo("240"));
            Assert.That(trackInput.GetAttribute("value"), Is.EqualTo("1200"));
        });

        pluginInput.Change("120");
        await cut.InvokeAsync(() =>
            cut.FindAll("input[type=number]")
                .Single(i => i.ParentElement!.TextContent.Contains("Track time budget"))
                .Change("600"));
        await cut.InvokeAsync(() => cut.Find("section.page-title button.btn-modern").Click());

        var factory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var pluginSaved = await db.DownloaderSettings.AsNoTracking()
            .AnyAsync(s => s.Key == "download_plugin_budget_seconds" && s.Value == "120");
        var trackSaved = await db.DownloaderSettings.AsNoTracking()
            .AnyAsync(s => s.Key == "download_track_budget_seconds" && s.Value == "600");
        Assert.Multiple(() =>
        {
            Assert.That(pluginSaved, Is.True, "the plugin budget survives Save");
            Assert.That(trackSaved, Is.True, "the track budget survives Save");
        });
    }

    [Test]
    public async Task Advanced_TimeBudgets_OutOfRange_ClampedOnSave()
    {
        var cut = _ctx.Render<Advanced>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Plugin time budget")), TimeSpan.FromSeconds(3));

        var pluginInput = cut.FindAll("input[type=number]")
            .Single(i => i.ParentElement!.TextContent.Contains("Plugin time budget"));
        pluginInput.Change("5"); // below the 30 second floor
        await cut.InvokeAsync(() => cut.Find("section.page-title button.btn-modern").Click());

        var factory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.That(await db.DownloaderSettings.AsNoTracking()
            .AnyAsync(s => s.Key == "download_plugin_budget_seconds" && s.Value == "30"), Is.True,
            "out-of-range input is clamped, never stored as typed");
    }
}
