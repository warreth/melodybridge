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
        Assert.That(labels.Count, Is.EqualTo(10),
            "Dedup + display + database logs + two toast toggles + five ntfy toggles use the wide switch");

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
    public async Task Advanced_NtfySettings_PersistThroughSave()
    {
        var cut = _ctx.Render<Advanced>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Send notifications to ntfy")), TimeSpan.FromSeconds(3));

        var enable = cut.FindAll("label.toggle-switch.wide")
            .Single(l => l.TextContent.Contains("Send notifications to ntfy"))
            .QuerySelector("input");
        enable.Change(true);
        cut.Find("input[placeholder='https://ntfy.sh/melodybridge']").Change("https://ntfy.example/music");

        // One category off proves the switches are independent.
        var jobFailures = cut.FindAll("label.toggle-switch.wide")
            .Single(l => l.TextContent.Contains("A sync job fails"))
            .QuerySelector("input");
        jobFailures.Change(false);

        await cut.InvokeAsync(() => cut.Find("section.page-title button.btn-modern").Click());

        var factory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var settings = await db.DownloaderSettings.AsNoTracking()
            .ToDictionaryAsync(s => s.Key, s => s.Value);
        Assert.Multiple(() =>
        {
            Assert.That(settings.GetValueOrDefault("ntfy_enabled"), Is.EqualTo("true"));
            Assert.That(settings.GetValueOrDefault("ntfy_url"), Is.EqualTo("https://ntfy.example/music"));
            Assert.That(settings.GetValueOrDefault("ntfy_job_failures"), Is.EqualTo("false"));
            Assert.That(settings.GetValueOrDefault("ntfy_downloads"), Is.EqualTo("true"),
                "untouched categories keep their default");
        });
    }

    [Test]
    public async Task Advanced_NtfyTestButton_SendsWithTheTypedValues()
    {
        var fake = new RecordingNtfy();
        var descriptor = _ctx.Services.FirstOrDefault(d =>
            d.ServiceType == typeof(MelodyBridge.Server.Services.NtfyNotifier));
        if (descriptor is not null) _ctx.Services.Remove(descriptor);
        _ctx.Services.AddSingleton<MelodyBridge.Server.Services.NtfyNotifier>(fake);

        var cut = _ctx.Render<Advanced>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Send notifications to ntfy")), TimeSpan.FromSeconds(3));

        cut.Find("input[placeholder='https://ntfy.sh/melodybridge']").Change("https://ntfy.example/music");
        await cut.InvokeAsync(() => cut.FindAll("button.btn-modern")
            .Single(b => b.TextContent.Contains("Send test notification")).Click());

        cut.WaitForAssertion(() =>
            Assert.That(fake.Tests, Has.Count.EqualTo(1), "the test button sends once"),
            TimeSpan.FromSeconds(3));
        Assert.That(fake.Tests[0].Url, Is.EqualTo("https://ntfy.example/music"));
    }

    /// <summary>Records test sends instead of hitting a topic.</summary>
    private sealed class RecordingNtfy : MelodyBridge.Server.Services.NtfyNotifier
    {
        public List<(string Url, string? Token)> Tests { get; } = new();

        public RecordingNtfy() : base(null!, null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MelodyBridge.Server.Services.NtfyNotifier>.Instance)
        {
        }

        public override Task<string?> TestAsync(string url, string? token, CancellationToken ct = default)
        {
            Tests.Add((url, token));
            return Task.FromResult<string?>(null);
        }
    }
}
