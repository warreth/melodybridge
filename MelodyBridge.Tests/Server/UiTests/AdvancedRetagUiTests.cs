using Bunit;
using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Pages;
using MelodyBridge.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The Re-tag library action on the Advanced page: the button uses the
/// shared styles (no inline styling), the run shows the counts and a
/// toast, a failure shows the error toast, and the button is disabled
/// while the work is running.
/// </summary>
[TestFixture]
[Category("UI")]
public class AdvancedRetagUiTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;
    private NotificationService _notifications = null!;
    private StubStore _store = null!;

    /// <summary>PlaylistStore with a controllable Re-tag result.</summary>
    private sealed class StubStore : PlaylistStore
    {
        public StubStore(IDbContextFactory<MelodyBridgeDbContext> factory)
            : base(factory, Array.Empty<ISourceProvider>(),
                new DownloadManager(new EmptyDownloaderRegistry(), NullLogger<DownloadManager>.Instance),
                NullLogger<PlaylistStore>.Instance)
        {
        }

        public int Calls { get; private set; }
        public bool Throw { get; set; }
        public TaskCompletionSource? Gate { get; set; }
        public (int updated, int skipped, int failed) Result { get; set; } = (2, 1, 3);

        public override async Task<(int updated, int skipped, int failed)> RetagLibraryAsync(
            CancellationToken ct = default)
        {
            Calls++;
            if (Gate is not null) await Gate.Task;
            if (Throw) throw new InvalidOperationException("the database is locked");
            return Result;
        }
    }

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-retag-ui-{Guid.NewGuid():N}.db");
        var factory = new TestSqliteFactory(_dbPath);
        using (var db = factory.CreateDbContext())
            db.Database.EnsureCreated();

        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);
        _ctx.Services.AddDownloadPages(factory);
        _notifications = new NotificationService(autoDismissAfter: TimeSpan.FromHours(1));
        _ctx.Services.AddSingleton(_notifications);
        // Last registration wins: the page gets the controllable store.
        _store = new StubStore(factory);
        _ctx.Services.AddSingleton<PlaylistStore>(_store);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [TearDown]
    public void TearDown()
    {
        _ctx.Dispose();
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    private IRenderedComponent<Advanced> RenderAdvanced()
    {
        var cut = _ctx.Render<Advanced>();
        // Jump straight to the maintenance card; the page is long.
        return cut;
    }

    [Test]
    public void MaintenancePanel_ShowsTheRetagActionWithSharedStyles()
    {
        var cut = RenderAdvanced();

        var retag = cut.FindAll("button").First(b => b.TextContent.Contains("Re-tag library"));
        var recompute = cut.FindAll("button").First(b => b.TextContent.Contains("Recompute audio facts"));

        Assert.Multiple(() =>
        {
            Assert.That(retag.ClassList, Does.Contain("btn-modern"),
                "the action uses the shared button style");
            Assert.That(retag.GetAttribute("style"), Is.Null,
                "no inline styling on a shared-style button");
            Assert.That(recompute.ClassList, Does.Contain("primary"),
                "Recompute stays the primary action");
            Assert.That(retag.ClassList, Does.Not.Contain("primary"));
        });
    }

    [Test]
    public void Retag_Run_ShowsTheCountsAndASuccessToast()
    {
        _store.Result = (4, 2, 1);
        var cut = RenderAdvanced();

        cut.FindAll("button").First(b => b.TextContent.Contains("Re-tag library")).Click();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Re-tagged 4 file(s); 2 already complete, 1 could not be read."),
                "the counts are shown next to the actions"), TimeSpan.FromSeconds(3));

        Assert.Multiple(() =>
        {
            Assert.That(_store.Calls, Is.EqualTo(1));
            Assert.That(_notifications.Snapshot().Any(n => n.Level == "success" && n.Message.Contains("Re-tagged 4 file(s)")),
                Is.True, "the toast confirms the run");
        });
    }

    [Test]
    public void Retag_Failure_ShowsTheErrorToast()
    {
        _store.Throw = true;
        var cut = RenderAdvanced();

        cut.FindAll("button").First(b => b.TextContent.Contains("Re-tag library")).Click();

        cut.WaitForAssertion(() =>
            Assert.That(_notifications.Snapshot().Any(n => n.Level == "error" && n.Message.Contains("Re-tag failed")),
                Is.True), TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Retag_DisablesTheButtonWhileRunning()
    {
        _store.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = RenderAdvanced();

        var button = cut.FindAll("button").First(b => b.TextContent.Contains("Re-tag library"));
        button.Click();

        cut.WaitForAssertion(() =>
        {
            var running = cut.FindAll("button").First(b => b.TextContent.Contains("Re-tagging"));
            Assert.That(running.HasAttribute("disabled"), Is.True,
                "a second click must not start a second run");
        }, TimeSpan.FromSeconds(3));

        _store.Gate.SetResult();
        cut.WaitForAssertion(() =>
        {
            var idle = cut.FindAll("button").First(b => b.TextContent.Contains("Re-tag library"));
            Assert.That(idle.HasAttribute("disabled"), Is.False);
        }, TimeSpan.FromSeconds(3));
    }
}
