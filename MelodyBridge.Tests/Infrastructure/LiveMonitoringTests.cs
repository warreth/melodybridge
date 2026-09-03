using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Scanning;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The live-monitoring pipeline on real files and a real SQLite database:
/// a change inside a monitored folder (FileSystemWatcher, no fake) leads to
/// an actual library scan, so the new file with a MELODY_ID lands in the
/// Tracks table and the location's LastScannedAt is stamped. The debounce
/// window and watcher startup make this the slowest test in the suite;
/// that is the cost of not faking the pipeline.
/// </summary>
[TestFixture]
[Category("Integration")]
public class LiveMonitoringTests
{
    private string _dir = null!;
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mb-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-live-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        using (var db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.ScanLocations.Add(new ScanLocationEntity
            {
                Path = _dir,
                LiveMonitoring = true,
                ScheduleCron = "", // manual schedule: live monitoring is the only trigger
            });
            db.SaveChanges();
        }
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
        try { File.Delete(_dbPath); } catch { /* best effort */ }
    }

    /// <summary>Real mp3 via ffmpeg's internal encoder: no network.</summary>
    private string MakeTaggedMp3(string name, string melodyId)
    {
        var path = Path.Combine(_dir, name);
        var ok = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-y -v error -f lavfi -i anullsrc=r=44100:cl=mono -t 1 -c:a libmp3lame -b:a 128k {path}",
            UseShellExecute = false,
        })!.WaitForExit(15000);
        Assert.That(ok && File.Exists(path), Is.True, "ffmpeg must produce the probe file");
        TaglibHelper.WriteMelodyId(path, melodyId);
        return path;
    }

    [Test]
    public async Task FileSystemChange_TriggersScan_AndStampsLocation()
    {
        // The app wiring, in miniature: monitor singleton + a handler that
        // runs the same rescan the FileSystemMonitoringBackgroundService does.
        var monitor = new FileSystemMonitor(
            NullLogger<FileSystemMonitor>.Instance);

        var scanned = new TaskCompletionSource<int>();
        monitor.ChangeDetected += (_, e) =>
        {
            if (e.ScanLocationId <= 0) return;
            _ = Task.Run(async () =>
            {
                await RescanAsync(e.ScanLocationId);
                scanned.TrySetResult(e.ScanLocationId);
            });
        };

        using (var db = _factory.CreateDbContext())
        {
            var loc = db.ScanLocations.Single(l => l.Path == _dir);
            monitor.StartMonitoring(_dir, loc.Id);
        }

        MakeTaggedMp3("fresh.mp3", "live-1");

        // The watcher event fires within seconds; the handler completes the
        // task source when the scan is done.
        var finished = await Task.WhenAny(scanned.Task, Task.Delay(TimeSpan.FromSeconds(20)));
        monitor.StopAll();
        Assert.That(finished == scanned.Task, Is.True,
            "a real file creation in the monitored folder must trigger the rescan");

        using var verify = _factory.CreateDbContext();
        Assert.That(verify.Tracks.Any(t => t.MelodyId == "live-1"),
            Is.True, "the scanned file with its MELODY_ID must be in the library");
        Assert.That(verify.ScanLocations.Single().LastScannedAt, Is.Not.Null,
            "the live rescan stamps the location's last-scan time");
    }

    private async Task RescanAsync(int scanLocationId)
    {
        using var db = _factory.CreateDbContext();
        var loc = await db.ScanLocations.FindAsync(scanLocationId);
        if (loc is null || !loc.LiveMonitoring) return;

        var scanner = new LibraryScanner(db, NullLogger<LibraryScanner>.Instance);
        await scanner.ScanAsync(new[] { new ScanLocation(loc.Path) });

        loc.LastScannedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task Scheduler_DueLocation_GetsScannedAndStamped()
    {
        // A location whose interval elapsed: the scheduler's due check
        // (LocationSchedule.Of + IsDue) must call the scanner and stamp
        // LastScannedAt, exactly like the background service loop body.
        MakeTaggedMp3("seed.mp3", "sched-1");

        using (var db = _factory.CreateDbContext())
        {
            var loc = db.ScanLocations.Single();
            loc.ScheduleCron = "interval:10";
            loc.LastScannedAt = DateTime.UtcNow.AddHours(-2); // long overdue
            db.SaveChanges();
        }

        using (var db = _factory.CreateDbContext())
        {
            var loc = db.ScanLocations.Single();
            var lastScan = loc.LastScannedAt!.Value;
            var schedule = LocationSchedule.Of(loc);
            Assert.That(schedule.IsDue(new DateTimeOffset(lastScan, TimeSpan.Zero), DateTimeOffset.UtcNow),
                Is.True, "an interval location two hours overdue is due");

            var scanner = new LibraryScanner(db, NullLogger<LibraryScanner>.Instance);
            await scanner.ScanAsync(new[] { new ScanLocation(loc.Path) });

            loc.LastScannedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        using var verify = _factory.CreateDbContext();
        Assert.That(verify.Tracks.Any(t => t.MelodyId == "sched-1"), Is.True);
        Assert.That(verify.ScanLocations.Single().LastScannedAt!.Value, Is.GreaterThan(DateTime.UtcNow.AddMinutes(-1)));
    }

    [Test]
    public void Scheduler_ManualLocation_NeverDue()
    {
        using var db = _factory.CreateDbContext();
        var loc = db.ScanLocations.Single();
        loc.ScheduleCron = "";
        loc.LastScannedAt = DateTime.UtcNow.AddYears(-2);
        db.SaveChanges();

        var schedule = LocationSchedule.Of(db.ScanLocations.AsNoTracking().Single());
        Assert.That(schedule.IsDue(DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow), Is.False,
            "manual locations only scan through the Run scan button");
    }

    [Test]
    public void Scheduler_LegacyHoursColumn_StillSchedules()
    {
        using var db = _factory.CreateDbContext();
        var loc = db.ScanLocations.Single();
        loc.ScheduleCron = null;
        loc.ScanIntervalHours = 24; // pre-ScheduleCron row
        db.SaveChanges();

        var schedule = LocationSchedule.Of(db.ScanLocations.AsNoTracking().Single());
        Assert.That(schedule.Mode, Is.EqualTo(ScanScheduleMode.Interval));
        Assert.That(schedule.IntervalMinutes, Is.EqualTo(24 * 60),
            "old rows configured in hours keep scanning on the same cadence");
    }
}
