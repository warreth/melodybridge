using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Playlists;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// SyncJobRunner end-to-end with real SQLite and a real .m3u output file.
/// Every assertion reads the produced file or a fresh DbContext.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class SyncJobRunnerTests
{
    private static string NewDbPath([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), $"mb-job-{test}-{Guid.NewGuid():N}.db");

    private static async Task<IDbContextFactory<MelodyBridgeDbContext>> NewDbFactory(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<MelodyBridgeDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        return factory;
    }

    private static SyncJobRunner NewRunner(IDbContextFactory<MelodyBridgeDbContext> factory)
        => new(factory, new M3uGenerator(NullLogger<M3uGenerator>.Instance),
            Array.Empty<IMediaServerSync>(), NullLogger<SyncJobRunner>.Instance);

    /// <summary>Seed a playlist with downloaded tracks (files must exist) + one pending track.</summary>
    private static async Task SeedPlaylistAsync(
        IDbContextFactory<MelodyBridgeDbContext> factory, string dir)
    {
        Directory.CreateDirectory(dir);
        var a = Path.Combine(dir, "a.mp3"); var b = Path.Combine(dir, "b.mp3");
        await System.IO.File.WriteAllTextAsync(a, "x");
        await System.IO.File.WriteAllTextAsync(b, "x");

        await using var db = await factory.CreateDbContextAsync();
        var playlist = new PlaylistEntity
        {
            Id = "pl-job",
            Name = "Job Playlist",
            SourceUrl = "stub:pl",
            TargetDirectory = dir,
            Tracks = new List<TrackEntity>
            {
                new()
                {
                    MelodyId = "mel-a", Title = "Alpha", Artist = "A", DownloadStatus = "downloaded",
                    CurrentPath = a, Position = 0,
                },
                new()
                {
                    MelodyId = "mel-b", Title = "Beta", Artist = "B", DownloadStatus = "downloaded",
                    CurrentPath = b, Position = 1, DurationMs = 120_000,
                },
                new()
                {
                    MelodyId = "mel-c", Title = "Gamma", Artist = "C", DownloadStatus = "pending",
                    Position = 2,
                },
            },
        };
        db.Playlists.Add(playlist);
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task RunJob_M3uPlaylist_WritesFileWithDownloadedTracksOnly()
    {
        var dbPath = NewDbPath();
        var dir = Path.Combine(Path.GetTempPath(), $"mb-job-{Guid.NewGuid():N}");
        var m3uPath = Path.Combine(dir, "out.m3u");
        try
        {
            var factory = await NewDbFactory(dbPath);
            await SeedPlaylistAsync(factory, dir);

            var runner = NewRunner(factory);
            var job = new SyncJob
            {
                Id = "job-1",
                Name = "Job Playlist",
                SourceId = "pl-job",
                OutputTarget = OutputTargetType.M3uFile,
                M3uOutputPath = m3uPath,
            };

            var log = await runner.RunJobAsync(job);

            Assert.That(log.Status, Is.EqualTo(SyncStatus.Completed));
            Assert.That(log.TotalTracks, Is.EqualTo(3),
                "the whole playlist counts, not just what has a file");
            Assert.That(log.ResolvedTracks, Is.EqualTo(2));
            Assert.That(log.Message, Does.Contain("2/3"),
                "the run summary must show resolved over total");
            Assert.That(log.Message, Does.Contain("1 without a local file"),
                "the pending track must be mentioned as missing");

            // Read the actual file back.
            var lines = await System.IO.File.ReadAllLinesAsync(m3uPath);
            Assert.That(lines[0], Is.EqualTo("#EXTM3U"));
            Assert.That(lines, Has.Length.EqualTo(5), "header + 2×(EXTINF + path)");
            Assert.That(lines[1], Is.EqualTo("#EXTINF:-1,A - Alpha"), "no duration stored -> -1");
            Assert.That(lines[2].EndsWith("a.mp3"), Is.True);
            Assert.That(lines[3], Is.EqualTo("#EXTINF:120,B - Beta"));
            Assert.That(lines[4].EndsWith("b.mp3"), Is.True);

            // Gamma (pending) must not appear.
            Assert.That(lines.Any(l => l.Contains("Gamma")), Is.False);
            Assert.That(lines.Any(l => l.EndsWith("mel-c")), Is.False);

            // Run history must be recorded in the DB (fresh context).
            await using var db = await factory.CreateDbContextAsync();
            var runs = await db.SyncJobRuns.AsNoTracking().Where(r => r.SyncJobId == "job-1").ToListAsync();
            Assert.That(runs, Has.Exactly(1).Items);
            Assert.That(runs[0].Status, Is.EqualTo("Completed"));
            Assert.That(runs[0].ResolvedTracks, Is.EqualTo(2));
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task RunJob_PathRemap_AppliesToOutputFile()
    {
        var dbPath = NewDbPath();
        var dir = Path.Combine(Path.GetTempPath(), $"mb-job-{Guid.NewGuid():N}");
        var m3uPath = Path.Combine(dir, "remapped.m3u");
        try
        {
            var factory = await NewDbFactory(dbPath);
            await SeedPlaylistAsync(factory, dir);

            var runner = NewRunner(factory);
            var job = new SyncJob
            {
                Id = "job-2",
                Name = "Remap Job",
                SourceId = "pl-job",
                OutputTarget = OutputTargetType.M3uFile,
                M3uOutputPath = m3uPath,
                PathRemapRules = new Dictionary<string, string> { [dir] = "/media/music" },
            };

            var log = await runner.RunJobAsync(job);
            Assert.That(log.Status, Is.EqualTo(SyncStatus.Completed));

            var lines = await System.IO.File.ReadAllLinesAsync(m3uPath);
            Assert.That(lines.Count(l => l.StartsWith("/media/music")), Is.EqualTo(2),
                "both downloaded tracks must use the remapped prefix");
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task RunJob_UnknownPlaylist_FailsWithMessage()
    {
        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var runner = NewRunner(factory);
            var job = new SyncJob
            {
                Id = "job-3",
                Name = "Nothing",
                SourceId = "does-not-exist",
                OutputTarget = OutputTargetType.M3uFile,
                M3uOutputPath = "/tmp/nope.m3u",
            };

            var log = await runner.RunJobAsync(job);
            Assert.That(log.Status, Is.EqualTo(SyncStatus.Failed));
            Assert.That(log.Message, Does.Contain("not found"));
        }
        finally { TryDelete(dbPath); }
    }

    private static void TryDelete(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(dbPath + suffix); } catch { /* best effort */ }
        }
    }
}
