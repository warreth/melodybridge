using System.Runtime.CompilerServices;
using Bunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// RecomputeMissingFactsAsync against REAL files and a REAL SQLite database:
/// stale downloaded tracks get their facts from the actual bytes on disk,
/// vanished files flip to pending with the reconciler wording, and tracks
/// that already have facts (or are not downloaded) stay untouched. The
/// Advanced page test clicks the real button on the real store.
/// ffmpeg generates the audio; tests skip honestly without it.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class RecomputeFactsTests
{
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;
    private string _dir = null!;

    [SetUp]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-recompute-{Guid.NewGuid():N}.db");
        _dir = Path.Combine(Path.GetTempPath(), $"mb-recompute-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _factory = new TestSqliteFactory(_dbPath);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch { }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { System.IO.File.Delete(_dbPath + suffix); } catch { }
    }

    private static bool FfmpegAvailable()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            return p is not null && p.WaitForExit(5000);
        }
        catch { return false; }
    }

    /// <summary>Generates a real audio file; empty string on success.</summary>
    private static string RunFfmpeg(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = args,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit(60000);
        return p.ExitCode == 0 ? string.Empty : $"ffmpeg exited {p.ExitCode}";
    }

    private async Task<PlaylistStore> SeedAndCreateStoreAsync(
        params (string melodyId, string? path, string status, int? bitrate, int? sampleRate, long? size)[] tracks)
    {
        await using var db = _factory.CreateDbContext();
        var playlist = new PlaylistEntity
        {
            Name = "Recompute",
            SourceUrl = "https://example.com/pl",
            SourcePlatform = Platform.Spotify,
            TargetDirectory = _dir,
            Tracks = tracks.Select((t, i) => new TrackEntity
            {
                MelodyId = t.melodyId,
                Title = t.melodyId,
                Artist = "Artist",
                Position = i,
                DownloadStatus = t.status,
                CurrentPath = t.path,
                Bitrate = t.bitrate,
                SampleRateHz = t.sampleRate,
                FileSizeBytes = t.size,
            }).ToList(),
        };
        db.Playlists.Add(playlist);
        await db.SaveChangesAsync();

        return new PlaylistStore(
            _factory,
            Array.Empty<ISourceProvider>(),
            new Application.Services.DownloadManager(
                new EmptyRegistryStub(),
                NullLogger<Application.Services.DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance);
    }

    private sealed class EmptyRegistryStub : IDownloaderRegistry
    {
        public IReadOnlyList<IDownloader> GetAll() => Array.Empty<IDownloader>();
        public IDownloader? Get(string id) => null;
        public IReadOnlyList<IDownloader> GetEnabled() => Array.Empty<IDownloader>();
        public Task SetEnabledAsync(string id, bool enabled) => Task.CompletedTask;
        public bool IsEnabled(string id) => false;
        public Task<int> GetPriorityAsync(string id, CancellationToken ct = default) => Task.FromResult(0);
        public Task SetPriorityAsync(string id, int priority, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetConfigAsync(string id, string key, CancellationToken ct = default) => Task.FromResult("");
        public Task SetConfigAsync(string id, string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Test]
    public async Task Recompute_FillsFacts_FlagsMissing_SkipsCompleteAndPending()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");

        var file1 = Path.Combine(_dir, "a.mp3");
        var file2 = Path.Combine(_dir, "b.flac");
        Assert.That(RunFfmpeg(
            $"-y -f lavfi -i anullsrc=r=44100:cl=stereo -t 3 -c:a libmp3lame -b:a 128k \"{file1}\""), Is.Empty);
        Assert.That(RunFfmpeg(
            $"-y -f lavfi -i anullsrc=r=44100:cl=stereo -t 3 -c:a flac \"{file2}\""), Is.Empty);

        var store = await SeedAndCreateStoreAsync(
            ("mel-fill-1", file1, "downloaded", null, null, null),   // stale: facts must be filled
            ("mel-fill-2", file2, "downloaded", null, null, null),   // stale: facts must be filled
            ("mel-missing", Path.Combine(_dir, "gone.mp3"), "downloaded", null, null, null), // vanished file
            ("mel-complete", file1, "downloaded", 320, 44100, 12345), // facts already set: must stay untouched
            ("mel-pending", null, "pending", null, null, null));     // not downloaded: must stay untouched

        var (recomputed, missing) = await store.RecomputeMissingFactsAsync();

        Assert.That((recomputed, missing), Is.EqualTo((2, 1)));

        await using var db = _factory.CreateDbContext();
        var tracks = db.Tracks.AsNoTracking().ToDictionary(t => t.MelodyId);

        var filled1 = tracks["mel-fill-1"];
        Assert.Multiple(() =>
        {
            Assert.That(filled1.Bitrate, Is.EqualTo(128), "bitrate must come from the real file");
            Assert.That(filled1.SampleRateHz, Is.EqualTo(44100), "sample rate must come from the real file");
            Assert.That(filled1.FileSizeBytes, Is.GreaterThan(0));
            Assert.That(filled1.DownloadStatus, Is.EqualTo("downloaded"));
        });

        var filled2 = tracks["mel-fill-2"];
        Assert.Multiple(() =>
        {
            Assert.That(filled2.SampleRateHz, Is.EqualTo(44100));
            Assert.That(filled2.FileSizeBytes, Is.GreaterThan(0));
        });

        var lost = tracks["mel-missing"];
        Assert.Multiple(() =>
        {
            Assert.That(lost.DownloadStatus, Is.EqualTo("pending"));
            Assert.That(lost.Warning, Is.EqualTo("file missing on disk, will re-download"));
        });

        var complete = tracks["mel-complete"];
        Assert.Multiple(() =>
        {
            Assert.That(complete.Bitrate, Is.EqualTo(320),
                "a track with facts set must not be re-probed (128 was on disk, 320 was stored)");
            Assert.That(complete.DownloadStatus, Is.EqualTo("downloaded"));
        });

        var pending = tracks["mel-pending"];
        Assert.Multiple(() =>
        {
            Assert.That(pending.DownloadStatus, Is.EqualTo("pending"));
            Assert.That(pending.Bitrate, Is.Null);
        });
    }

    [Test]
    public async Task Recompute_AllFactsPresent_ChangesNothing()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");

        var file = Path.Combine(_dir, "c.mp3");
        Assert.That(RunFfmpeg(
            $"-y -f lavfi -i anullsrc=r=44100:cl=stereo -t 3 -c:a libmp3lame -b:a 128k \"{file}\""), Is.Empty);

        var store = await SeedAndCreateStoreAsync(
            ("mel-complete", file, "downloaded", 320, 44100, 12345));

        var (recomputed, missing) = await store.RecomputeMissingFactsAsync();

        Assert.That((recomputed, missing), Is.EqualTo((0, 0)),
            "a track with all facts set must be skipped entirely");
    }

    [Test]
    public void AdvancedPage_RecomputeButton_RunsStoreAndToasts()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");

        // Arrange: real store on a real (empty) sqlite database.
        using var ctx = new Bunit.TestContext();
        ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(_factory);
        ctx.Services.AddSingleton(new SettingsStore(_factory));
        ctx.Services.AddSingleton(new MelodyBridge.Server.Services.NotificationService());
        var store = new PlaylistStore(
            _factory,
            Array.Empty<ISourceProvider>(),
            new Application.Services.DownloadManager(
                new EmptyRegistryStub(),
                NullLogger<Application.Services.DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance);
        ctx.Services.AddSingleton(store);

        // Act: the page must render the button and clicking it must invoke
        // the real store (0 stale tracks here -> the "0 track(s)" toast).
        var cut = ctx.Render<MelodyBridge.Server.Components.Pages.Advanced>();
        Assert.That(cut.Markup, Does.Contain("Recompute audio facts"), "the button must be on the page");
        Assert.That(cut.Markup, Does.Contain("Library maintenance"));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Recompute audio facts")).Click();

        var notifications = ctx.Services.GetRequiredService<MelodyBridge.Server.Services.NotificationService>().Snapshot();
        Assert.That(notifications, Is.Not.Empty, "clicking must fire a toast");
        Assert.That(notifications[0].Message, Does.Contain("Recomputed facts for 0 track(s)"),
            $"unexpected toast: {notifications[0].Message}");
    }
}
