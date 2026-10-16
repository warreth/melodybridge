using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The Notifications tab on Settings: the toast gates and the ntfy push
/// block live here (moved off the Advanced page), save through the page's
/// Save button, and the test button sends with the typed values without
/// persisting them.
/// </summary>
[TestFixture]
[Category("UI")]
public class SettingsNotificationsTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        // A real SQLite file on disk: the save test asserts persisted
        // rows, so the EF InMemory provider would not prove anything.
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-notif-{Guid.NewGuid():N}.db");
        var factory = new TestSqliteFactory(_dbPath);
        using (var db = factory.CreateDbContext())
            db.Database.EnsureCreated();
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);
        _ctx.Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        _ctx.Services.AddDownloadPages(factory);
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(factory,
            NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
        _ctx.Services.AddSingleton(tokenStore);
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider>.Instance));
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider>.Instance));
        var collector = new MelodyBridge.Server.Services.LogCollector();
        _ctx.Services.AddSingleton<MelodyBridge.Core.Logging.ILogCollector>(collector);
        _ctx.Services.AddSingleton(new MelodyBridge.Server.Services.LogExporter(collector));
        _ctx.Services.AddSingleton(new Moq.Mock<IMediaServerDirectory>().Object);
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

    /// <summary>Renders Settings and switches to the Notifications tab.</summary>
    private IRenderedComponent<Settings> OpenNotifications()
    {
        var cut = _ctx.Render<Settings>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("button.tab-link").Any(b => b.TextContent.Trim() == "Notifications"), Is.True),
            TimeSpan.FromSeconds(3));
        cut.FindAll("button.tab-link").Single(b => b.TextContent.Trim() == "Notifications").Click();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Send notifications to ntfy")), TimeSpan.FromSeconds(3));
        return cut;
    }

    [Test]
    public void NotificationsTab_ShowsTheToastAndNtfyBlocks()
    {
        var cut = OpenNotifications();

        Assert.That(cut.Markup, Does.Contain("Toast when a playlist download finishes"),
            "the toast gates live on the tab too");
        Assert.That(cut.Markup, Does.Contain("Send notifications to ntfy"));
        Assert.That(cut.Markup, Does.Contain("Send test notification"));
        Assert.That(cut.Markup, Does.Contain("A background task fails"),
            "all four ntfy categories render");
    }

    [Test]
    public void Advanced_NoLongerCarriesTheNotificationBlocks()
    {
        var advanced = _ctx.Render<Advanced>();
        advanced.WaitForAssertion(() =>
            Assert.That(advanced.Markup, Does.Contain("Show the file column")), TimeSpan.FromSeconds(3));

        Assert.That(advanced.Markup, Does.Not.Contain("Send notifications to ntfy"),
            "ntfy moved to Settings - Notifications");
        Assert.That(advanced.Markup, Does.Not.Contain("Toast when a playlist download finishes"),
            "the toast gates moved with it");
    }

    [Test]
    public async Task Notifications_SavePersistsToastAndNtfySwitches()
    {
        var cut = OpenNotifications();

        // The toast gate: one off proves the switches are independent.
        cut.FindAll("label.toggle-switch.wide")
            .Single(l => l.TextContent.Contains("Toast when a playlist download finishes"))
            .QuerySelector("input")!.Change(false);

        cut.FindAll("label.toggle-switch.wide")
            .Single(l => l.TextContent.Contains("Send notifications to ntfy"))
            .QuerySelector("input")!.Change(true);
        cut.Find("input[placeholder='https://ntfy.sh/melodybridge']").Change("https://ntfy.example/music");
        cut.FindAll("label.toggle-switch.wide")
            .Single(l => l.TextContent.Contains("A sync job fails"))
            .QuerySelector("input")!.Change(false);

        await cut.InvokeAsync(() => cut.Find("section.page-title button.btn-modern").Click());

        var factory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var settings = await db.DownloaderSettings.AsNoTracking()
            .ToDictionaryAsync(s => s.Key, s => s.Value);
        Assert.Multiple(() =>
        {
            Assert.That(settings.GetValueOrDefault("notify_downloads"), Is.EqualTo("false"));
            Assert.That(settings.GetValueOrDefault("ntfy_enabled"), Is.EqualTo("true"));
            Assert.That(settings.GetValueOrDefault("ntfy_url"), Is.EqualTo("https://ntfy.example/music"));
            Assert.That(settings.GetValueOrDefault("ntfy_job_failures"), Is.EqualTo("false"));
            Assert.That(settings.GetValueOrDefault("ntfy_downloads"), Is.EqualTo("true"),
                "untouched categories keep their default");
        });
    }

    [Test]
    public async Task Notifications_TestButton_SendsWithTheTypedValues()
    {
        var fake = new RecordingNtfy();
        var descriptor = _ctx.Services.FirstOrDefault(d =>
            d.ServiceType == typeof(MelodyBridge.Server.Services.NtfyNotifier));
        if (descriptor is not null) _ctx.Services.Remove(descriptor);
        _ctx.Services.AddSingleton<MelodyBridge.Server.Services.NtfyNotifier>(fake);

        var cut = OpenNotifications();
        cut.Find("input[placeholder='https://ntfy.sh/melodybridge']").Change("https://ntfy.example/music");
        await cut.InvokeAsync(() => cut.FindAll("button.btn-modern")
            .Single(b => b.TextContent.Contains("Send test notification")).Click());

        cut.WaitForAssertion(() =>
            Assert.That(fake.Tests, Has.Count.EqualTo(1), "the test button sends once"),
            TimeSpan.FromSeconds(3));
        Assert.That(fake.Tests[0].Url, Is.EqualTo("https://ntfy.example/music"));
        Assert.That(fake.Tests[0].Token, Is.EqualTo(""),
            "an empty token stays empty, nothing invented");
    }

    /// <summary>Records test sends instead of hitting a topic.</summary>
    private sealed class RecordingNtfy : MelodyBridge.Server.Services.NtfyNotifier
    {
        public List<(string Url, string? Token)> Tests { get; } = new();

        public RecordingNtfy() : base(null!, null!,
            NullLogger<MelodyBridge.Server.Services.NtfyNotifier>.Instance)
        {
        }

        public override Task<string?> TestAsync(string url, string? token, CancellationToken ct = default)
        {
            Tests.Add((url, token));
            return Task.FromResult<string?>(null);
        }
    }
}
