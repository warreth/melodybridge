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

        var (recomputed, missing, flagged) = await store.RecomputeMissingFactsAsync();

        Assert.That((recomputed, missing, flagged), Is.EqualTo((2, 1, 0)),
            "silent files get facts filled, the vanished file is queued, nothing is flagged");

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

        var (recomputed, missing, flagged) = await store.RecomputeMissingFactsAsync();

        Assert.That((recomputed, missing, flagged), Is.EqualTo((0, 0, 0)),
            "a track with all facts set must not be re-probed and a silent file has no verdict");
    }

    [Test]
    public async Task Recompute_ClearsAStaleInflatedClaim_WhenTheCurrentCheckFindsNothing()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");
        if (MelodyBridge.Infrastructure.Audio.SpectrumAnalyzer.FindFfprobe() is null)
            Assert.Ignore("ffprobe not installed");

        // A genuine full-band file with an old flagged note on the row:
        // the recheck must drop the stale claim, not keep telling the
        // story of a checker that has since been fixed.
        var file = Path.Combine(_dir, "genuine320.mp3");
        Assert.That(RunFfmpeg(
            $"-y -f lavfi -i \"anoisesrc=color=pink:duration=4:seed=4\" -b:a 320k \"{file}\""), Is.Empty);

        var store = await SeedAndCreateStoreAsync(
            ("mel-stale", file, "downloaded", 320, 44100, 12345));
        await using (var db = _factory.CreateDbContext())
        {
            await db.Tracks.Where(t => t.MelodyId == "mel-stale")
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Warning,
                    "bitrate looks inflated: spectral ceiling 9.7 kHz: matches a 128 kbps source"));
        }

        var previous = PlaylistStore.Verification;
        PlaylistStore.Verification = () =>
            new MelodyBridge.Infrastructure.Audio.VerificationOptions(
                MelodyBridge.Infrastructure.Audio.SpectrumMode.Fast);
        int flagged;
        try
        {
            (_, _, flagged) = await store.RecomputeMissingFactsAsync();
        }
        finally
        {
            PlaylistStore.Verification = previous;
        }

        await using var check = _factory.CreateDbContext();
        var track = check.Tracks.AsNoTracking().Single(t => t.MelodyId == "mel-stale");
        Assert.Multiple(() =>
        {
            Assert.That(flagged, Is.EqualTo(0));
            Assert.That(track.Warning, Is.Null,
                "a stale inflated claim is cleared by the fresh check");
            Assert.That(track.ReviewKind, Is.Null);
        });
    }

    [Test]
    public async Task Recompute_FlagsALossySourceBoxedAsFlac()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");
        if (MelodyBridge.Infrastructure.Audio.SpectrumAnalyzer.FindFfprobe() is null)
            Assert.Ignore("ffprobe not installed");

        // The user-reported shape: a lossy source re-encoded into FLAC
        // (the manual picker used to do this for yt-dlp-backed plugins).
        var source = Path.Combine(_dir, "src64.mp3");
        var fakeFlac = Path.Combine(_dir, "fake.flac");
        Assert.That(RunFfmpeg(
            $"-y -f lavfi -i \"anoisesrc=color=pink:duration=4:seed=4\" -b:a 64k \"{source}\""), Is.Empty);
        Assert.That(RunFfmpeg($"-y -i \"{source}\" -c:a flac \"{fakeFlac}\""), Is.Empty);

        var store = await SeedAndCreateStoreAsync(
            ("mel-fake-flac", fakeFlac, "downloaded", null, null, null));

        // Other fixtures toggle the static options; pin the check here and
        // restore it so this test never depends on execution order.
        var previous = PlaylistStore.Verification;
        PlaylistStore.Verification = () =>
            new MelodyBridge.Infrastructure.Audio.VerificationOptions(
                MelodyBridge.Infrastructure.Audio.SpectrumMode.Fast);
        int recomputed, missing, flagged;
        try
        {
            (recomputed, missing, flagged) = await store.RecomputeMissingFactsAsync();
        }
        finally
        {
            PlaylistStore.Verification = previous;
        }

        Assert.Multiple(() =>
        {
            Assert.That(recomputed, Is.EqualTo(1), "the facts were backfilled");
            Assert.That(missing, Is.EqualTo(0));
            Assert.That(flagged, Is.EqualTo(1), "the fake lossless file must be flagged");
        });

        await using var db = _factory.CreateDbContext();
        var track = db.Tracks.AsNoTracking().Single(t => t.MelodyId == "mel-fake-flac");
        Assert.Multiple(() =>
        {
            Assert.That(track.ReviewKind, Does.Contain("spectrum"));
            Assert.That(track.ReviewText, Does.Contain("looks inflated"));
            Assert.That(track.ReviewText, Does.Contain("lossless"),
                "the review says the container is lossless, not the content");
        });
    }

    [Test]
    public async Task Recompute_DoesNotReopenASpectrumReviewSettledWithKeep()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");
        if (MelodyBridge.Infrastructure.Audio.SpectrumAnalyzer.FindFfprobe() is null)
            Assert.Ignore("ffprobe not installed");

        var source = Path.Combine(_dir, "src-kept.mp3");
        var fakeFlac = Path.Combine(_dir, "kept.flac");
        Assert.That(RunFfmpeg(
            $"-y -f lavfi -i \"anoisesrc=color=pink:duration=4:seed=4\" -b:a 64k \"{source}\""), Is.Empty);
        Assert.That(RunFfmpeg($"-y -i \"{source}\" -c:a flac \"{fakeFlac}\""), Is.Empty);

        var store = await SeedAndCreateStoreAsync(
            ("mel-kept", fakeFlac, "downloaded", null, null, null));
        var trackId = await TrackIdAsync("mel-kept");

        var previous = PlaylistStore.Verification;
        PlaylistStore.Verification = () =>
            new MelodyBridge.Infrastructure.Audio.VerificationOptions(
                MelodyBridge.Infrastructure.Audio.SpectrumMode.Fast);
        try
        {
            var (_, _, first) = await store.RecomputeMissingFactsAsync();
            Assert.That(first, Is.EqualTo(1), "the fake FLAC is flagged first");

            Assert.That(await store.ResolveQualityReviewAsync(trackId, QualityReviewAction.Keep),
                Is.True);
            await using (var afterKeep = _factory.CreateDbContext())
            {
                var kept = afterKeep.Tracks.AsNoTracking().Single(t => t.Id == trackId);
                Assert.Multiple(() =>
                {
                    Assert.That(kept.SpectrumAccepted, Is.True,
                        "the Keep decision is remembered for this file");
                    Assert.That(kept.ReviewKind, Is.Null, "Keep clears the open review");
                });
            }

            var (_, _, second) = await store.RecomputeMissingFactsAsync();
            await using var afterRecheck = _factory.CreateDbContext();
            var track = afterRecheck.Tracks.AsNoTracking().Single(t => t.Id == trackId);
            Assert.Multiple(() =>
            {
                Assert.That(second, Is.EqualTo(0),
                    "a recheck must not reopen a review the user settled");
                Assert.That(track.ReviewKind, Is.Null);
                Assert.That(track.SpectrumAccepted, Is.True);
            });
        }
        finally
        {
            PlaylistStore.Verification = previous;
        }
    }

    private async Task<int> TrackIdAsync(string melodyId)
    {
        await using var db = _factory.CreateDbContext();
        return db.Tracks.AsNoTracking().Single(t => t.MelodyId == melodyId).Id;
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
        ctx.Services.AddSingleton(new MelodyBridge.Server.Services.DevPanelService());
        // The page injects the ntfy notifier now; a plain client is enough
        // because this test never sends.
        ctx.Services.AddHttpClient();
        ctx.Services.AddSingleton(sp => new MelodyBridge.Server.Services.NtfyNotifier(
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>(),
            NullLogger<MelodyBridge.Server.Services.NtfyNotifier>.Instance));
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
