using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The user-facing update flow: a baseline on first install, release notes
/// after an upgrade, one notification per newer release, and the automatic
/// check preference. Real SQLite for the settings, a scripted GitHub.
/// </summary>
[TestFixture]
public class UpdateServiceTests
{
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;
    private SettingsStore _settings = null!;
    private NotificationService _notifications = null!;

    [SetUp]
    public async Task Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-update-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        _settings = new SettingsStore(_factory);
        _notifications = new NotificationService();
    }

    [TearDown]
    public void TearDown()
    {
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private UpdateService Build(HttpMessageHandler handler, bool databaseExisted = true)
        => new(new UpdateCheckService(new HttpClient(handler)), _settings,
            _notifications, new InstallState(databaseExisted),
            NullLogger<UpdateService>.Instance);

    private async Task SeedSeenAsync(string version)
        => await _settings.SetAsync(UpdateService.SeenVersionKey, version);

    [Test]
    public async Task FirstInstall_RecordsABaseline_AndShowsNoNotes()
    {
        var service = Build(new ThrowingHandler(), databaseExisted: false);

        await service.EnsureReleaseNotesAsync();

        Assert.That(service.PendingReleaseNotes, Is.Null,
            "a fresh install has no previous version to announce");
        Assert.That(await _settings.GetAsync(UpdateService.SeenVersionKey, ""),
            Is.EqualTo(AppInfo.Version));
    }

    [Test]
    public async Task Upgrade_ShowsTheRunningVersionsReleaseNotes()
    {
        await SeedSeenAsync("0.0.1");
        var service = Build(new ReleaseListHandler(AppInfo.Version, "- feat(ui): a new dashboard"));

        await service.EnsureReleaseNotesAsync();

        Assert.That(service.PendingReleaseNotes, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(service.PendingReleaseNotes!.Version, Is.EqualTo(AppInfo.Version));
            Assert.That(service.PendingReleaseNotes.Body, Does.Contain("a new dashboard"));
            Assert.That(service.PendingReleaseNotes.Url, Does.Contain("releases/tag/v"));
        });
    }

    [Test]
    public async Task ExistingInstall_WithoutAMarker_ShowsTheCurrentReleaseNotes()
    {
        // The first launch of the version that introduced this dialog has
        // no stored version yet, but an existing database means upgrade.
        var service = Build(new ReleaseListHandler(AppInfo.Version, "- feat: first notes"));

        await service.EnsureReleaseNotesAsync();

        Assert.That(service.PendingReleaseNotes, Is.Not.Null);
        Assert.That(service.PendingReleaseNotes!.Body, Does.Contain("first notes"));
    }

    [Test]
    public async Task SameVersion_ShowsNoNotes()
    {
        await SeedSeenAsync(AppInfo.Version);
        var service = Build(new ThrowingHandler());

        await service.EnsureReleaseNotesAsync();

        Assert.That(service.PendingReleaseNotes, Is.Null,
            "only an upgrade opens the what's-new dialog");
    }

    [Test]
    public async Task Upgrade_Offline_StillOpensTheDialogWithTheReleasePage()
    {
        await SeedSeenAsync("0.0.1");
        var service = Build(new ThrowingHandler());

        await service.EnsureReleaseNotesAsync();

        Assert.Multiple(() =>
        {
            Assert.That(service.PendingReleaseNotes, Is.Not.Null);
            Assert.That(service.PendingReleaseNotes!.Body, Is.Null);
            Assert.That(service.PendingReleaseNotes.Url, Is.EqualTo(AppInfo.ReleasesPage));
        });
    }

    [Test]
    public async Task Dismiss_RecordsTheRunningVersion()
    {
        await SeedSeenAsync("0.0.1");
        var service = Build(new ReleaseListHandler(AppInfo.Version, "- fix: something"));
        await service.EnsureReleaseNotesAsync();

        await service.DismissReleaseNotesAsync();

        Assert.That(service.PendingReleaseNotes, Is.Null);
        Assert.That(await _settings.GetAsync(UpdateService.SeenVersionKey, ""),
            Is.EqualTo(AppInfo.Version));
    }

    [Test]
    public async Task NewVersion_NotifiesOncePerVersion()
    {
        var service = Build(new LatestReleaseHandler("9.9.9"));

        await service.CheckAndNotifyAsync();
        await service.CheckAndNotifyAsync();

        var toasts = _notifications.Snapshot();
        Assert.That(toasts, Has.Count.EqualTo(1), "a restart or a second sweep stays quiet");
        Assert.That(toasts[0].Message, Does.Contain("9.9.9"));
        Assert.That(toasts[0].ActionLabel, Is.EqualTo("Release notes"));
        Assert.That(await _settings.GetAsync(UpdateService.NotifiedVersionKey, ""),
            Is.EqualTo("9.9.9"));
    }

    [Test]
    public async Task RunningTheLatest_DoesNotNotify()
    {
        var service = Build(new LatestReleaseHandler(AppInfo.Version));

        await service.CheckAndNotifyAsync();

        Assert.That(_notifications.Snapshot(), Is.Empty);
    }

    [Test]
    public async Task AutomaticChecksDisabled_SkipsNotesAndNotifications()
    {
        await _settings.SetAsync(UpdateService.EnabledKey, "false");
        await SeedSeenAsync("0.0.1");
        var service = Build(new LatestReleaseHandler("9.9.9"));

        await service.EnsureReleaseNotesAsync();
        await service.CheckAndNotifyAsync();

        Assert.Multiple(() =>
        {
            Assert.That(service.PendingReleaseNotes, Is.Null);
            Assert.That(_notifications.Snapshot(), Is.Empty);
        });
    }

    [Test]
    public async Task EnablingLater_StillFindsTheUpgradeNotes()
    {
        await _settings.SetAsync(UpdateService.EnabledKey, "false");
        await SeedSeenAsync("0.0.1");
        var service = Build(new ReleaseListHandler(AppInfo.Version, "- feat: notes"));
        await service.EnsureReleaseNotesAsync();
        Assert.That(service.PendingReleaseNotes, Is.Null);

        await _settings.SetAsync(UpdateService.EnabledKey, "true");
        await service.EnsureReleaseNotesAsync();

        Assert.That(service.PendingReleaseNotes, Is.Not.Null,
            "the disabled startup must not mark the notes as handled");
    }

    private sealed class LatestReleaseHandler(string version) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    {"tag_name":"v{{version}}","name":"v{{version}}","body":"- notes","html_url":"https://github.com/warreth/melodybridge/releases/tag/v{{version}}","draft":false}
                    """, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private sealed class ReleaseListHandler(string version, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    [{"tag_name":"v{{version}}","name":"v{{version}}","body":"{{body}}","html_url":"https://github.com/warreth/melodybridge/releases/tag/v{{version}}","draft":false}]
                    """, System.Text.Encoding.UTF8, "application/json"),
            });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("offline");
    }
}
