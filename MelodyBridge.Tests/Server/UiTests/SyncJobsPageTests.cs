using TestContext = Bunit.TestContext;
using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

[TestFixture]
[Category("UI")]
public class SyncJobsPageTests
{
    private TestContext _ctx = null!;
    private Mock<ISyncJobRunner> _jobRunner = null!;
    private InMemFactory _dbFactory = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _jobRunner = new Mock<ISyncJobRunner>();

        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"SyncJobsTest_{Guid.NewGuid()}")
            .Options;
        var dbFactory = new InMemFactory(options);
        _dbFactory = dbFactory;

        using (var db = dbFactory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "src-1",
                Name = "Weekly Source",
                SourceUrl = "stub:src-1",
            });
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "j1",
                Name = "Weekly Sync",
                SourceId = "src-1",
                SearchLocationPaths = "[]",
                Schedule = "Daily",
                OutputTarget = "M3uFile",
                M3uOutputPath = "/app/playlists/weekly.m3u",
                LastRunAt = DateTime.UtcNow.AddDays(-1),
                LastRunStatus = "Completed",
            });
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "j2",
                Name = "Failed Job",
                Schedule = "Manual",
                OutputTarget = "Jellyfin",
                LastRunStatus = "Failed",
            });
            db.SaveChanges();
        }

        _ctx.Services.AddSingleton<ISyncJobRunner>(_jobRunner.Object);
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(dbFactory);
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    [Test]
    public void SyncJobs_Renders_Title()
    {
        var cut = _ctx.Render<SyncJobs>();
        Assert.That(cut.Markup, Does.Contain("Sync jobs"));
    }

    [Test]
    public void SyncJobs_ShowsJobList()
    {
        var cut = _ctx.Render<SyncJobs>();
        Assert.That(cut.Markup, Does.Contain("Weekly Sync"));
        Assert.That(cut.Markup, Does.Contain("Failed Job"));
    }

    [Test]
    public void SyncJobs_HasCreateNewButton()
    {
        var cut = _ctx.Render<SyncJobs>();
        var btns = cut.FindAll("button");
        Assert.That(btns.Any(b => b.TextContent.Trim().Contains("New sync job")), Is.True);
    }

    [Test]
    public void SyncJobs_ShowsRunButton()
    {
        var cut = _ctx.Render<SyncJobs>();
        var btns = cut.FindAll("button");
        Assert.That(btns.Any(b => b.TextContent.Trim().Contains("Run")), Is.True);
    }

    [Test]
    public void SyncJobs_OpensWizard_OnNewJob()
    {
        var cut = _ctx.Render<SyncJobs>();
        var btn = cut.FindAll("button").First(b => b.TextContent.Trim().Contains("New sync job"));
        btn.Click();
        Assert.That(cut.Markup, Does.Contain("New sync job"));
    }

    [Test]
    public async Task RunJobNow_ReallyRunsTheJob_RecordsHistoryAndWritesM3u()
    {
        // Real SQLite, real files on disk, the production SyncJobRunner:
        // the click must leave a completed run row and a real M3U file.
        var dbPath = Path.Combine(Path.GetTempPath(), $"mb-syncjob-{Guid.NewGuid():N}.db");
        var dir = Path.Combine(Path.GetTempPath(), $"mb-syncjob-{Guid.NewGuid():N}");
        var m3uPath = Path.Combine(dir, "weekly.m3u");
        Directory.CreateDirectory(dir);
        try
        {
            var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
                .UseSqlite($"Data Source={dbPath}").Options;
            var sqliteFactory = new InMemFactory(options);
            using (var db = sqliteFactory.CreateDbContext())
            {
                db.Database.EnsureCreated();

                var a = Path.Combine(dir, "a.mp3");
                await System.IO.File.WriteAllTextAsync(a, "x");
                db.Playlists.Add(new PlaylistEntity
                {
                    Id = "src-1", Name = "Weekly Source", SourceUrl = "stub:src-1",
                    Tracks = new List<TrackEntity>
                    {
                        new()
                        {
                            MelodyId = "mel-a", Title = "Alpha", Artist = "A",
                            DownloadStatus = "downloaded", CurrentPath = a, Position = 0,
                        },
                    },
                });
                db.SyncJobs.Add(new SyncJobEntity
                {
                    Id = "j1", Name = "Weekly Sync", SourceId = "src-1",
                    SearchLocationPaths = "[]", Schedule = "Manual",
                    OutputTarget = "M3uFile", M3uOutputPath = m3uPath,
                });
                db.SaveChanges();
            }

            // The production runner over the real DB, no media servers.
            _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(sqliteFactory);
            _ctx.Services.AddSingleton<ISyncJobRunner>(new MelodyBridge.Infrastructure.Services.SyncJobRunner(
                sqliteFactory,
                new MelodyBridge.Infrastructure.Playlists.M3uGenerator(
                    NullLogger<MelodyBridge.Infrastructure.Playlists.M3uGenerator>.Instance),
                Array.Empty<IMediaServerSync>(),
                NullLogger<MelodyBridge.Infrastructure.Services.SyncJobRunner>.Instance));

            var cut = _ctx.Render<SyncJobs>();
            cut.WaitForAssertion(() =>
                Assert.That(cut.Markup, Does.Contain("Weekly Sync")), TimeSpan.FromSeconds(3));

            cut.FindAll("button").Single(b => b.TextContent.Trim() == "Run now").Click();

            // The card's status pill flips to Completed after a real run.
            cut.WaitForAssertion(() =>
                Assert.That(cut.Markup, Does.Contain("Completed")), TimeSpan.FromSeconds(5));

            using (var db = sqliteFactory.CreateDbContext())
            {
                var runs = db.SyncJobRuns.AsNoTracking().Where(r => r.SyncJobId == "j1").ToList();
                Assert.That(runs, Has.Exactly(1).Items, "the click recorded a run-history row");
                Assert.That(runs[0].Status, Is.EqualTo("Completed"));
            }

            var lines = await System.IO.File.ReadAllLinesAsync(m3uPath);
            Assert.That(lines[0], Is.EqualTo("#EXTM3U"), "the M3U file was really written");
            Assert.That(lines.Any(l => l.EndsWith("a.mp3")), Is.True, "the downloaded track is in it");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(dbPath + suffix); } catch { /* best effort */ }
        }
    }

    [Test]
    public void EditButton_OpensWizard_HydratedWithJobValues()
    {
        var cut = _ctx.Render<SyncJobs>();
        var editBtn = cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit");
        editBtn.Click();

        Assert.That(cut.Markup, Does.Contain("Pick your playlist"));
        Assert.That(cut.Markup, Does.Contain("Editing \"Weekly Sync\""));
        var nameInput = cut.Find("input[placeholder='e.g. My Summer Hits']");
        Assert.That(nameInput.GetAttribute("value"), Is.EqualTo("Weekly Sync"));
    }

    [Test]
    public void EditJob_SaveUpdates_ExistingEntity_InDb()
    {
        var cut = _ctx.Render<SyncJobs>();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();

        // walk to the review step and save (step 1 needs a source, and
        // the M3U path must stay filled to pass the new validation)
        for (int i = 0; i < 4; i++)
        {
            var next = cut.FindAll("button")
                .FirstOrDefault(b => b.TextContent.Trim() == "Next");
            if (next != null) next.Click();
        }

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save changes").Click();

        using var db = _dbFactory.CreateDbContext();
        var all = db.SyncJobs.IgnoreQueryFilters().ToList();
        Assert.That(all.Count, Is.EqualTo(2), "edit must update, not duplicate");
        var updated = all.First(j => j.Id == "j1");
        Assert.That(updated.Name, Is.EqualTo("Weekly Sync"));
        Assert.That(updated.M3uOutputPath, Is.EqualTo("/app/playlists/weekly.m3u"));
        Assert.That(updated.LastRunStatus, Is.EqualTo("Completed"), "edit must not reset run status");
    }

    private class InMemFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InMemFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }

}
