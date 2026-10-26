using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Layout;
using MelodyBridge.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The what's-new dialog in the shell: an upgrade opens it once, the
/// release body renders as readable bullets, and Got it records the
/// running version so the same screen never returns.
/// </summary>
[TestFixture]
[Category("UI")]
public class ReleaseNotesUiTests
{
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public async Task Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-notes-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public void TearDown()
    {
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    [Test]
    public async Task MainLayout_Upgrade_ShowsReleaseNotes_AndGotItPersists()
    {
        var settings = new SettingsStore(_factory);
        await settings.SetAsync(UpdateService.SeenVersionKey, "0.0.1");
        var notifications = new NotificationService();
        var updates = new UpdateService(
            new UpdateCheckService(new HttpClient(new ReleaseListHandler(
                AppInfo.Version,
                "- feat(sync): one job editor\n- fix(ui): clearer plugins"))),
            settings, notifications, new InstallState(DatabaseExistedAtStartup: true),
            NullLogger<UpdateService>.Instance);
        await updates.EnsureReleaseNotesAsync();
        Assert.That(updates.PendingReleaseNotes, Is.Not.Null);

        using var ctx = new Bunit.TestContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddPluginAuthMonitor();
        ctx.Services.AddSingleton(new DevPanelService());
        ctx.Services.AddSingleton(notifications);
        ctx.Services.AddSingleton(settings);
        ctx.Services.AddSingleton(updates);

        var cut = ctx.Render<MainLayout>();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Find(".release-notes-modal h2").TextContent,
                Is.EqualTo($"What's new in {AppInfo.Version}"));
            var items = cut.FindAll(".release-notes-body li").Select(li => li.TextContent.Trim()).ToList();
            Assert.That(items, Is.EqualTo(new[]
            {
                "feat(sync): one job editor",
                "fix(ui): clearer plugins",
            }), "the release body renders as a readable list");
            Assert.That(cut.Find(".release-notes-footer, .wizard-footer a").TextContent,
                Does.Contain("Open on GitHub"));
        }, TimeSpan.FromSeconds(3));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Got it").Click();

        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".release-notes-modal"), Is.Empty,
                "Got it closes the dialog"), TimeSpan.FromSeconds(3));
        Assert.That(await settings.GetAsync(UpdateService.SeenVersionKey, ""),
            Is.EqualTo(AppInfo.Version),
            "the running version is recorded so the dialog stays closed");
    }

    private sealed class ReleaseListHandler(string version, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    $$"""
                    [{"tag_name":"v{{version}}","name":"v{{version}}","body":{{System.Text.Json.JsonSerializer.Serialize(body)}},"html_url":"https://github.com/warreth/melodybridge/releases/tag/v{{version}}","draft":false}]
                    """, System.Text.Encoding.UTF8, "application/json"),
            });
    }
}
