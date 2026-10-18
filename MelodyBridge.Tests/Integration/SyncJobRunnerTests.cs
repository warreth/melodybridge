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
        services.AddDbContextFactory<MelodyBridgeDbContext>(o => o.UseSqlite($"Data Source={dbPath};Pooling=False"));
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
    public async Task RunJob_MultipleTargets_WritesTheM3uAndPushesEveryServer()
    {
        var dbPath = NewDbPath();
        var dir = Path.Combine(Path.GetTempPath(), $"mb-job-{Guid.NewGuid():N}");
        var m3uPath = Path.Combine(dir, "multi.m3u");
        try
        {
            var factory = await NewDbFactory(dbPath);
            await SeedPlaylistAsync(factory, dir);
            var jellyfin = new CapturingServerSync("Jellyfin");
            var plex = new CapturingServerSync("Plex");
            var runner = new SyncJobRunner(factory,
                new M3uGenerator(NullLogger<M3uGenerator>.Instance),
                new IMediaServerSync[] { jellyfin, plex },
                NullLogger<SyncJobRunner>.Instance);

            var job = new SyncJob
            {
                Id = "job-multi",
                Name = "Job Playlist",
                SourceId = "pl-job",
                Targets = new List<SyncTarget>
                {
                    new(OutputTargetType.M3uFile, M3uPath: m3uPath),
                    new(OutputTargetType.JellyfinApi),
                    new(OutputTargetType.PlexApi),
                },
            };

            var log = await runner.RunJobAsync(job);

            Assert.Multiple(() =>
            {
                Assert.That(log.Status, Is.EqualTo(SyncStatus.Completed));
                Assert.That(log.Message, Does.Contain("2/3"), "the summary keeps the track counts");
                Assert.That(log.Message, Does.Contain("to 3 targets"));
                Assert.That(log.Message, Does.Contain("M3U file"));
                Assert.That(log.Message, Does.Contain("Jellyfin"));
                Assert.That(log.Message, Does.Contain("Plex"));
                Assert.That(File.Exists(m3uPath), Is.True, "the M3U target ran too");
                Assert.That(jellyfin.Calls, Is.EqualTo(1));
                Assert.That(plex.Calls, Is.EqualTo(1));
            });
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task RunJob_OneTargetFails_TheRestStillRun_AndTheJobFails()
    {
        var dbPath = NewDbPath();
        var dir = Path.Combine(Path.GetTempPath(), $"mb-job-{Guid.NewGuid():N}");
        var m3uPath = Path.Combine(dir, "partial.m3u");
        try
        {
            var factory = await NewDbFactory(dbPath);
            await SeedPlaylistAsync(factory, dir);
            var failing = new FailingServerSync();
            var plex = new CapturingServerSync("Plex");
            var runner = new SyncJobRunner(factory,
                new M3uGenerator(NullLogger<M3uGenerator>.Instance),
                new IMediaServerSync[] { failing, plex },
                NullLogger<SyncJobRunner>.Instance);

            var job = new SyncJob
            {
                Id = "job-partial",
                Name = "Job Playlist",
                SourceId = "pl-job",
                Targets = new List<SyncTarget>
                {
                    new(OutputTargetType.M3uFile, M3uPath: m3uPath),
                    new(OutputTargetType.JellyfinApi),
                    new(OutputTargetType.PlexApi),
                },
            };

            var log = await runner.RunJobAsync(job);

            Assert.Multiple(() =>
            {
                Assert.That(log.Status, Is.EqualTo(SyncStatus.Failed),
                    "one refused target fails the run, it does not hide");
                Assert.That(log.Message, Does.Contain("Create failed"));
                Assert.That(File.Exists(m3uPath), Is.True,
                    "the other targets still ran");
                Assert.That(plex.Calls, Is.EqualTo(1), "a failing target stops nothing else");
            });
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

    [Test]
    public async Task RunJob_FailedServerReport_FailsTheJob()
    {
        // A media server that rejected the create must fail the run: the
        // old behaviour reported Completed while nothing was written.
        var dbPath = NewDbPath();
        var dir = Path.Combine(Path.GetTempPath(), $"mb-job-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            await SeedPlaylistAsync(factory, dir);
            var server = new FailingServerSync();
            var runner = new SyncJobRunner(factory,
                new M3uGenerator(NullLogger<M3uGenerator>.Instance),
                new IMediaServerSync[] { server },
                NullLogger<SyncJobRunner>.Instance);
            var job = new SyncJob
            {
                Id = "job-failed-server",
                Name = "Job Playlist",
                SourceId = "pl-job",
                OutputTarget = OutputTargetType.JellyfinApi,
                JellyfinServerUrl = "http://jellyfin:8096",
                JellyfinApiKey = "key",
            };

            var log = await runner.RunJobAsync(job);

            Assert.Multiple(() =>
            {
                Assert.That(log.Status, Is.EqualTo(SyncStatus.Failed),
                    "a failed server write must fail the job, not report Completed");
                Assert.That(log.Message, Does.Contain("Create failed"));
            });

            await using var db = await factory.CreateDbContextAsync();
            var run = await db.SyncJobRuns.AsNoTracking()
                .SingleAsync(r => r.SyncJobId == "job-failed-server");
            Assert.That(run.Status, Is.EqualTo("Failed"), "the stored run says Failed too");
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task RunJob_WithProfile_ResolvesTheConnectionFreshFromTheProfile()
    {
        var dbPath = NewDbPath();
        var dir = Path.Combine(Path.GetTempPath(), $"mb-job-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            await SeedPlaylistAsync(factory, dir);
            var profiles = new MediaServerProfileStore(new SettingsStore(factory));
            await profiles.SaveAsync(new MediaServerProfile
            {
                Id = "prof-1",
                Name = "Homelab",
                Kind = "Jellyfin",
                BaseUrl = "http://jellyfin:8096",
                ApiKey = "token-1",
            });

            var server = new CapturingServerSync("Jellyfin");
            var runner = new SyncJobRunner(factory,
                new M3uGenerator(NullLogger<M3uGenerator>.Instance),
                new IMediaServerSync[] { server },
                NullLogger<SyncJobRunner>.Instance,
                profiles);
            var job = new SyncJob
            {
                Id = "job-profile",
                Name = "Job Playlist",
                SourceId = "pl-job",
                OutputTarget = OutputTargetType.JellyfinApi,
                MediaServerProfileId = "prof-1",
                JellyfinUserId = "user-7",
            };

            var log = await runner.RunJobAsync(job);

            Assert.That(log.Status, Is.EqualTo(SyncStatus.Completed));
            Assert.That(server.Connection, Is.Not.Null);
            Assert.That(server.Connection!.BaseUrl, Is.EqualTo("http://jellyfin:8096"),
                "the profile's URL is the connection, not a copy stored on the job");
            Assert.That(server.Connection.ApiKey, Is.EqualTo("token-1"));
            Assert.That(server.Connection.UserId, Is.EqualTo("user-7"),
                "the job's Jellyfin user still travels along for favorites");
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task RunJob_WithSubsonicProfile_CarriesTheProfilesUsername()
    {
        var dbPath = NewDbPath();
        var dir = Path.Combine(Path.GetTempPath(), $"mb-job-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            await SeedPlaylistAsync(factory, dir);
            var profiles = new MediaServerProfileStore(new SettingsStore(factory));
            await profiles.SaveAsync(new MediaServerProfile
            {
                Id = "prof-sv",
                Name = "Navidrome",
                Kind = "Navidrome",
                BaseUrl = "http://navidrome:4533",
                ApiKey = "secret",
                Username = "admin",
            });

            var server = new CapturingServerSync("Subsonic");
            var runner = new SyncJobRunner(factory,
                new M3uGenerator(NullLogger<M3uGenerator>.Instance),
                new IMediaServerSync[] { server },
                NullLogger<SyncJobRunner>.Instance,
                profiles);
            var job = new SyncJob
            {
                Id = "job-subsonic",
                Name = "Job Playlist",
                SourceId = "pl-job",
                OutputTarget = OutputTargetType.SubsonicApi,
                MediaServerProfileId = "prof-sv",
                // A legacy Subsonic job carried the username in this field;
                // the profile's username and password are the credential now.
                JellyfinUserId = "old-username",
            };

            var log = await runner.RunJobAsync(job);

            Assert.That(log.Status, Is.EqualTo(SyncStatus.Completed));
            Assert.That(server.Connection!.UserId, Is.EqualTo("admin"),
                "the profile's username pairs with the profile's password");
            Assert.That(server.Connection.ApiKey, Is.EqualTo("secret"));
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task RunJob_ProfileDeleted_FailsWithAPickAnotherMessage()
    {
        var dbPath = NewDbPath();
        var dir = Path.Combine(Path.GetTempPath(), $"mb-job-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            await SeedPlaylistAsync(factory, dir);
            var profiles = new MediaServerProfileStore(new SettingsStore(factory));
            var server = new CapturingServerSync("Jellyfin");
            var runner = new SyncJobRunner(factory,
                new M3uGenerator(NullLogger<M3uGenerator>.Instance),
                new IMediaServerSync[] { server },
                NullLogger<SyncJobRunner>.Instance,
                profiles);
            var job = new SyncJob
            {
                Id = "job-gone",
                Name = "Job Playlist",
                SourceId = "pl-job",
                OutputTarget = OutputTargetType.JellyfinApi,
                MediaServerProfileId = "prof-gone",
            };

            var log = await runner.RunJobAsync(job);

            Assert.That(log.Status, Is.EqualTo(SyncStatus.Failed));
            Assert.That(log.Message, Does.Contain("no longer exists"),
                "the run says exactly what to fix");
            Assert.That(server.Connection, Is.Null, "nothing was synced with a guessed connection");
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task RunJob_ProfileKindMismatch_FailsWithClearMessage()
    {
        var dbPath = NewDbPath();
        var dir = Path.Combine(Path.GetTempPath(), $"mb-job-{Guid.NewGuid():N}");
        try
        {
            var factory = await NewDbFactory(dbPath);
            await SeedPlaylistAsync(factory, dir);
            var profiles = new MediaServerProfileStore(new SettingsStore(factory));
            await profiles.SaveAsync(new MediaServerProfile
            {
                Id = "prof-plex",
                Name = "Plexy",
                Kind = "Plex",
                BaseUrl = "http://plex:32400",
                ApiKey = "plex-token",
            });

            var server = new CapturingServerSync("Jellyfin");
            var runner = new SyncJobRunner(factory,
                new M3uGenerator(NullLogger<M3uGenerator>.Instance),
                new IMediaServerSync[] { server },
                NullLogger<SyncJobRunner>.Instance,
                profiles);
            var job = new SyncJob
            {
                Id = "job-mismatch",
                Name = "Job Playlist",
                SourceId = "pl-job",
                OutputTarget = OutputTargetType.JellyfinApi,
                MediaServerProfileId = "prof-plex",
            };

            var log = await runner.RunJobAsync(job);

            Assert.That(log.Status, Is.EqualTo(SyncStatus.Failed));
            Assert.That(log.Message, Does.Contain("is a Plex server"),
                "editing a profile's kind must not silently feed the wrong client");
            Assert.That(server.Connection, Is.Null);
        }
        finally
        {
            TryDelete(dbPath);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>A server sync that records the connection it was handed.</summary>
    private sealed class CapturingServerSync(string name) : IMediaServerSync
    {
        public string Name { get; } = name;
        public MediaServerConnection? Connection { get; private set; }
        public MediaServerSyncReport? LastReport { get; private set; }
        public int Calls { get; private set; }

        public Task SyncPlaylistAsync(Playlist playlist, PlaylistOutputOptions options, CancellationToken ct = default)
        {
            Calls++;
            Connection = options.MediaServerConnection;
            LastReport = new MediaServerSyncReport(playlist.Tracks?.Count ?? 0, Array.Empty<string>(), null, "Synced");
            return Task.CompletedTask;
        }
    }

    /// <summary>A server sync that always reports a rejected create.</summary>
    private sealed class FailingServerSync : IMediaServerSync
    {
        public string Name => "Jellyfin";
        public MediaServerSyncReport? LastReport { get; private set; }

        public Task SyncPlaylistAsync(Playlist playlist, PlaylistOutputOptions options, CancellationToken ct = default)
        {
            LastReport = new MediaServerSyncReport(0, Array.Empty<string>(), null,
                "Create failed with BadRequest", Succeeded: false);
            return Task.CompletedTask;
        }
    }

    private static void TryDelete(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(dbPath + suffix); } catch { /* best effort */ }
        }
    }
}
