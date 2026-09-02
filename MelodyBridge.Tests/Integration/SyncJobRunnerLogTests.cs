using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.MediaServers;
using MelodyBridge.Infrastructure.Playlists;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// Runner behaviour against a real SQLite database: local-folder source
/// filtering (C5), per-job Jellyfin connection override (C2) and the
/// per-track warning breakdown stored on the run row (C6).
/// </summary>
[TestFixture]
public class SyncJobRunnerLogTests
{
    private string _dbPath = null!;
    private TestSqliteFactory _dbFactory = null!;

    [SetUp]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-runner-{Guid.NewGuid()}.db");
        _dbFactory = new TestSqliteFactory(_dbPath);
        using var db = _dbFactory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
        }
    }

    private static SyncJob NewJob(string output = "M3uFile", string? m3uPath = null) => new()
    {
        Id = "job-" + Guid.NewGuid().ToString("N"),
        Name = "Test job",
        OutputTarget = Enum.TryParse<OutputTargetType>(output, out var ot)
            ? ot : OutputTargetType.M3uFile,
        M3uOutputPath = m3uPath,
        Schedule = SyncJobSchedule.Manual,
    };

    private async Task<SyncJobEntity> InsertJobAsync(SyncJob job)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = new SyncJobEntity
        {
            Id = job.Id,
            Name = job.Name,
            SourceId = job.SourceId,
            SearchLocationPaths = JsonSerializer.Serialize(job.SearchLocationPaths),
            OutputTarget = job.OutputTarget.ToString(),
            M3uOutputPath = job.M3uOutputPath,
            JellyfinServerUrl = job.JellyfinServerUrl,
            JellyfinApiKey = job.JellyfinApiKey,
            JellyfinUserId = job.JellyfinUserId,
            PathRemapRules = "{}",
            ExtensionRemapRules = "{}",
            Schedule = job.Schedule.ToString(),
        };
        db.SyncJobs.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    private async Task AddTrackAsync(string title, string path,
        string status = "downloaded", string? playlistId = null, bool isLiked = false)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var t = new TrackEntity
        {
            MelodyId = "mid-" + Guid.NewGuid().ToString("N"),
            Title = title,
            Artist = "Someone",
            DownloadStatus = status,
            CurrentPath = path,
            IsLiked = isLiked,
        };
        if (playlistId is { } pid)
        {
            var pl = await db.Playlists.Include(p => p.Tracks).FirstOrDefaultAsync(p => p.Id == pid);
            if (pl != null) pl.Tracks.Add(t);
        }
        db.Tracks.Add(t);
        await db.SaveChangesAsync();
    }

    // ── C5: local folder as source ─────────────────────────────────

    [Test]
    public async Task LocalFolderSource_IncludesOnlyTracksUnderThatFolder()
    {
        var m3u = Path.Combine(Path.GetTempPath(), $"mb-out-{Guid.NewGuid():N}.m3u");
        await AddTrackAsync("Track A", "/music/a/a.flac");
        await AddTrackAsync("Track B", "/music/b/b.flac");
        await AddTrackAsync("Track C", "/other/c.flac");

        var job = NewJob(m3uPath: m3u);
        job.SourceId = null;
        job.SearchLocationPaths = new List<string> { "/music/a" };
        await InsertJobAsync(job);

        var runner = new SyncJobRunner(_dbFactory,
            new M3uGenerator(NullLogger<M3uGenerator>.Instance),
            Array.Empty<IMediaServerSync>(),
            NullLogger<SyncJobRunner>.Instance);

        var log = await runner.RunJobAsync(job);

        Assert.That(log.Status, Is.EqualTo(SyncStatus.Completed));
        var lines = await File.ReadAllLinesAsync(m3u);
        var paths = lines.Where(l => !l.StartsWith("#")).ToList();
        Assert.That(paths, Has.Count.EqualTo(1), "only the /music/a track is in the M3U");
        Assert.That(paths[0], Does.StartWith("/music/a"));

        await using var db = await _dbFactory.CreateDbContextAsync();
        var run = await db.SyncJobRuns.AsNoTracking()
            .SingleAsync(r => r.SyncJobId == job.Id);
        Assert.That(run.TotalTracks, Is.EqualTo(1),
            "the folder's tracks are the total, nothing else");
    }

    // ── C6: per-track warnings on the run row ────────────────────────

    [Test]
    public async Task M3uRun_StoresPerTrackWarningsForMissingFiles()
    {
        var m3u = Path.Combine(Path.GetTempPath(), $"mb-out-{Guid.NewGuid():N}.m3u");
        const string playlistId = "9001";
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = playlistId,
                Name = "With a gap",
                SourceUrl = "stub://p",
            });
            await db.SaveChangesAsync();
        }

        await AddTrackAsync("Present one", "/music/one.flac", playlistId: playlistId);
        await AddTrackAsync("Missing one", null, status: "pending", playlistId: playlistId);
        await AddTrackAsync("Present two", "/music/two.flac", playlistId: playlistId);

        var job = NewJob(m3uPath: m3u);
        job.SourceId = playlistId.ToString();
        await InsertJobAsync(job);

        var runner = new SyncJobRunner(_dbFactory,
            new M3uGenerator(NullLogger<M3uGenerator>.Instance),
            Array.Empty<IMediaServerSync>(),
            NullLogger<SyncJobRunner>.Instance);

        await runner.RunJobAsync(job);

        await using var db2 = await _dbFactory.CreateDbContextAsync();
        var run = await db2.SyncJobRuns.AsNoTracking()
            .SingleAsync(r => r.SyncJobId == job.Id);
        Assert.That(run.Status, Is.EqualTo("Completed"),
            "missing files are a warning breakdown, not a failure");
        var warnings = JsonSerializer.Deserialize<List<string>>(run.WarningDetails!);
        Assert.That(warnings, Is.Not.Null);
        Assert.That(warnings!, Has.Count.EqualTo(1),
            "exactly the track without a local file is listed");
        Assert.That(warnings![0], Does.Contain("Missing one"),
            "the warning names the track");
    }

    // ── C2: per-job Jellyfin connection wins ─────────────────────────

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Url, string? Token)> Requests { get; } = new();
        public Func<string, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(),
                request.Headers.TryGetValues("X-Emby-Token", out var t) ? t.FirstOrDefault() : null));
            return Task.FromResult(Respond(request.RequestUri!.PathAndQuery));
        }
    }

    private sealed class FixedSettings : IJellyfinSettings
    {
        public Task<string> GetBaseUrlAsync(CancellationToken ct = default)
            => Task.FromResult("http://global:8096");
        public Task<string> GetApiKeyAsync(CancellationToken ct = default)
            => Task.FromResult("global-key");
        public Task<string?> GetUserIdAsync(CancellationToken ct = default)
            => Task.FromResult<string?>(null);
    }

    [Test]
    public async Task JellyfinRun_UsesJobUrlKeyAndUser_OverGlobalSettings()
    {
        var handler = new RecordingHandler();
        handler.Respond = url => url switch
        {
            "/Users" => Json("""[{"Id": "u1", "Name": "Alice"}, {"Id": "u2", "Name": "Bob"}]"""),
            var items when items.StartsWith("/Items?") => Json(
                """{"Items": [{"Id": "item-1", "Name": "Song"}]}"""),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK),
        };
        var jellyfin = new JellyfinSync(
            new HttpClient(handler),
            NullLogger<JellyfinSync>.Instance,
            new FixedSettings());

        const string playlistId = "9002";
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = playlistId,
                Name = "JF test",
                SourceUrl = "stub://p",
            });
            await db.SaveChangesAsync();
        }
        await AddTrackAsync("Song", "/music/song.flac", playlistId: playlistId, isLiked: true);

        var job = NewJob(output: "JellyfinApi");
        job.SourceId = playlistId.ToString();
        job.JellyfinServerUrl = "http://perjob:8096";
        job.JellyfinApiKey = "per-job-key";
        job.JellyfinUserId = "u2";
        await InsertJobAsync(job);

        var runner = new SyncJobRunner(_dbFactory,
            new M3uGenerator(NullLogger<M3uGenerator>.Instance),
            new[] { jellyfin },
            NullLogger<SyncJobRunner>.Instance);

        var log = await runner.RunJobAsync(job);

        Assert.That(log.Status, Is.EqualTo(SyncStatus.Completed));
        Assert.That(handler.Requests, Is.Not.Empty, "the sync must have called the server");
        Assert.That(handler.Requests.All(r => r.Url.StartsWith("http://perjob:8096/")),
            Is.True, "every request goes to the job's URL, not the global one");
        Assert.That(handler.Requests.All(r => r.Token == "per-job-key"),
            Is.True, "every request carries the job's API key");
        Assert.That(handler.Requests.Any(r => r.Url.Contains("/Users/u2/")),
            Is.True, "the job's chosen user id is used");
    }

    private static HttpResponseMessage Json(string body)
        => new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
}
