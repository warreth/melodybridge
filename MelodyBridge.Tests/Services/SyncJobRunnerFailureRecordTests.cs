using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Playlists;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// A sync job that fails before its normal recording block ran (a missing
/// source playlist, any pre-publish exception) used to leave nothing
/// behind: no run row, no LastRunAt, so the scheduler treated the job as
/// never-run and fired it again on every poll forever. The failure paths
/// record like every other run now.
/// </summary>
[TestFixture]
public class SyncJobRunnerFailureRecordTests
{
    private string _root = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-jobfail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _factory = new TestSqliteFactory(Path.Combine(_root, "jobs.db"));
        await using var db = _factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private SyncJobRunner NewRunner()
        => new(_factory, new M3uGenerator(NullLogger<M3uGenerator>.Instance),
            Array.Empty<IMediaServerSync>(), NullLogger<SyncJobRunner>.Instance);

    private async Task<SyncJob> SeedJobAsync(string id, string? sourceId, bool createSource = true)
    {
        await using (var db = _factory.CreateDbContext())
        {
            if (sourceId is not null && createSource)
            {
                db.Playlists.Add(new PlaylistEntity
                {
                    Id = sourceId,
                    Name = "Source",
                    SourceUrl = "stub:source",
                    TargetDirectory = _root,
                });
            }
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = id,
                Name = "Job",
                SourceId = sourceId,
                
            });
            await db.SaveChangesAsync();
        }
        return new SyncJob
        {
            Id = id,
            Name = "Job",
            SourceId = sourceId,
            OutputTarget = OutputTargetType.JellyfinApi,
        };
    }

    [Test]
    public async Task MissingSourcePlaylist_RecordsARunRowAndStampsTheJob()
    {
        var job = await SeedJobAsync("job-missing", "pl-vanished", createSource: false);

        var log = await NewRunner().RunJobAsync(job);

        Assert.That(log.Status, Is.EqualTo(SyncStatus.Failed));

        // Read back through a fresh context, never from the runner's state.
        using var db = _factory.CreateDbContext();
        var run = await db.SyncJobRuns.AsNoTracking()
            .SingleOrDefaultAsync(r => r.SyncJobId == job.Id);
        Assert.Multiple(() =>
        {
            Assert.That(run, Is.Not.Null, "the failed run gets its row");
            Assert.That(run!.Status, Is.EqualTo("Failed"));
            Assert.That(run.Message, Does.Contain("not found"));
        });
        var jobRow = await db.SyncJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        Assert.Multiple(() =>
        {
            Assert.That(jobRow.LastRunAt, Is.Not.Null, "the scheduler needs the stamp");
            Assert.That(jobRow.LastRunStatus, Is.EqualTo("Failed"));
            Assert.That(jobRow.LastRunSummary, Does.Contain("not found"));
        });
    }

    [Test]
    public async Task RunRowIsUsableByTheSchedulerAnchor_SoTheJobStopsHamming()
    {
        var job = await SeedJobAsync("job-anchor", "pl-vanished", createSource: false);

        var runner = NewRunner();
        var first = await runner.RunJobAsync(job);
        var firstStamp = await JobStampAsync();

        Assert.That(first.Status, Is.EqualTo(SyncStatus.Failed));
        Assert.That(firstStamp, Is.Not.EqualTo(DateTime.MinValue), "the first failure stamps the job");

        // The scheduler's anchor moves with the stamp: the job is not due
        // again a minute later once its schedule window was honored in the
        // first stamp.
        using var db = _factory.CreateDbContext();
        var entity = await db.SyncJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        Assert.That(entity.LastRunStatus, Is.EqualTo("Failed"));
    }

    private async Task<DateTime> JobStampAsync()
    {
        using var db = _factory.CreateDbContext();
        var entity = await db.SyncJobs.AsNoTracking().SingleAsync(j => j.Id == "job-anchor");
        return entity.LastRunAt ?? DateTime.MinValue;
    }
}
