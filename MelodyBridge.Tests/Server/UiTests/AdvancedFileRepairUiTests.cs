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
/// The File links maintenance card: the repair reports its counts, the
/// duplicate check lists the keeper and the orphan, and removing the
/// orphan calls the store with exactly the reported path. Shared styles
/// only, no inline styling.
/// </summary>
[TestFixture]
[Category("UI")]
public class AdvancedFileRepairUiTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;
    private StubStore _store = null!;

    private sealed class StubStore : PlaylistStore
    {
        public StubStore(IDbContextFactory<MelodyBridgeDbContext> factory)
            : base(factory, Array.Empty<ISourceProvider>(),
                new DownloadManager(new EmptyDownloaderRegistry(), NullLogger<DownloadManager>.Instance),
                NullLogger<PlaylistStore>.Instance)
        {
        }

        public int RepairCalls { get; private set; }
        public int CheckCalls { get; private set; }
        public List<string> Removed { get; } = new();

        public override Task<FileLinkRepairReport> RepairFileLinksAsync(CancellationToken ct = default)
        {
            RepairCalls++;
            return Task.FromResult(new FileLinkRepairReport(3, 1, 2, 4));
        }

        public override Task<DuplicateFileReport> FindDuplicateFilesAsync(CancellationToken ct = default)
        {
            CheckCalls++;
            return Task.FromResult(Removed.Count > 0
                ? new DuplicateFileReport(Array.Empty<DuplicateFileGroup>())
                : new DuplicateFileReport(new[]
                {
                    new DuplicateFileGroup(
                        "pl-chill", "Chill", "Kids", "spotify:kids",
                        "/music/Chill/MGMT - Kids (2).flac",
                        new[] { "/music/Chill/MGMT - Kids.flac" }),
                }));
        }

        public override Task<DuplicateCleanupReport> RemoveDuplicateFilesAsync(
            IReadOnlyCollection<string> paths, CancellationToken ct = default)
        {
            Removed.AddRange(paths);
            return Task.FromResult(new DuplicateCleanupReport(paths.Count, 1));
        }
    }

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-file-repair-ui-{Guid.NewGuid():N}.db");
        var factory = new TestSqliteFactory(_dbPath);
        using (var db = factory.CreateDbContext())
            db.Database.EnsureCreated();

        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);
        _ctx.Services.AddDownloadPages(factory);
        _ctx.Services.AddSingleton(new NotificationService(autoDismissAfter: TimeSpan.FromHours(1)));
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

    [Test]
    public void FileLinksCard_RepairsAndReportsTheCounts()
    {
        var cut = _ctx.Render<Advanced>();
        var repair = cut.FindAll("button").First(b => b.TextContent.Contains("Repair file links"));

        repair.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(_store.RepairCalls, Is.EqualTo(1));
            Assert.That(cut.Markup, Does.Contain("Adopted 3 track(s)"));
            Assert.That(cut.Markup, Does.Contain("rewrote 1 legacy tag(s)"));
            Assert.That(cut.Markup, Does.Contain("2 ambiguous file(s)"));
        });
        Assert.That(repair.ClassList, Does.Contain("btn-modern"));
        Assert.That(repair.GetAttribute("style"), Is.Null, "shared styles only");
    }

    [Test]
    public void DuplicateReport_ListsTheKeeperAndRemovesOnlyTheOrphan()
    {
        var cut = _ctx.Render<Advanced>();
        var check = cut.FindAll("button").First(b => b.TextContent.Contains("Check duplicate files"));

        check.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Kids"));
            Assert.That(cut.Markup, Does.Contain("MGMT - Kids (2).flac"));
            Assert.That(cut.Markup, Does.Contain("MGMT - Kids.flac"));
            Assert.That(cut.Markup, Does.Contain("Clean up 1 duplicate file"));
        });

        cut.FindAll("button").First(b => b.TextContent.Contains("Clean up 1 duplicate file")).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(_store.Removed, Is.EqualTo(new[] { "/music/Chill/MGMT - Kids.flac" }),
                "only the orphan path is sent, never the keeper");
            Assert.That(cut.Markup, Does.Not.Contain("MGMT - Kids.flac"));
        });
    }
}
