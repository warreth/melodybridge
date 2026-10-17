using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// The full flagged-download flow against REAL files and REAL SQLite: a
/// wrong-length download is kept and flagged (not deleted, not failed),
/// Keep clears the flag and leaves the file, Retry deletes the flagged
/// file and downloads again while avoiding the provider that delivered
/// it, and the avoid hint is one-shot so a failed retry never traps the
/// track. The audio comes from ffmpeg at test time; the tests skip
/// honestly when ffmpeg is missing.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class QualityReviewFlowTests
{
    private string _dir = null!;
    private Func<VerificationOptions> _savedVerification = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mb-review-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        // Length behavior under test; the spectrum check has its own
        // coverage and would only slow these files down.
        _savedVerification = PlaylistStore.Verification;
        PlaylistStore.Verification = () => new VerificationOptions(SpectrumMode.Off);
    }

    [TearDown]
    public void TearDown()
    {
        PlaylistStore.Verification = _savedVerification;
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
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

    /// <summary>A real silent MP3 of the given length, generated with ffmpeg.</summary>
    private string MakeMp3(string name, int seconds)
    {
        var path = Path.Combine(_dir, name);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-y -hide_banner -loglevel error -f lavfi -i anullsrc=r=44100:cl=stereo -t {seconds} -c:a libmp3lame -b:a 128k \"{path}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit(60000);
        Assert.That(p.ExitCode, Is.EqualTo(0), "ffmpeg must produce the test file");
        return path;
    }

    /// <summary>Runs ffmpeg with free-form arguments and returns the output path.</summary>
    private string RunFfmpeg(string args, string name)
    {
        var path = Path.Combine(_dir, name);
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-y -hide_banner -loglevel error {args} \"{path}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(60000);
        Assert.That(p.ExitCode, Is.EqualTo(0), $"ffmpeg must produce {name}: {stderr}");
        return path;
    }

    /// <summary>Serves one real file for every search; records its calls.</summary>
    private sealed class FileServingDownloader : IDownloader
    {
        private readonly string _file;

        public FileServingDownloader(string id, string file)
        {
            Id = id;
            _file = file;
        }

        public string Id { get; }
        public string Name => $"File Server ({Id})";
        public string Description => string.Empty;
        public PluginCapabilities Capabilities { get; } = PluginCapabilities.Lossy(AudioFormat.Mp3);
        public int SearchCalls { get; private set; }
        public int DownloadCalls { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
        {
            SearchCalls++;
            // The hit advertises the playlist's own expectation, so the
            // pre-download duration filter lets it through; the real file
            // behind it is where the surprise lives.
            return Task.FromResult<DownloaderSearchHit?>(
                new DownloaderSearchHit(query.Title, query.Artist, $"test://{Id}",
                    query.ExpectedDuration, MatchConfidence: MatchConfidence.High));
        }

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            DownloadCalls++;
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"{Id}.mp3");
            File.Copy(_file, path, overwrite: true);
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }

    private async Task<(PlaylistStore store, IDbContextFactory<MelodyBridgeDbContext> factory, int trackId)>
        SetupAsync(params IDownloader[] downloaders) => await SetupAsync(10_500, downloaders);

    private async Task<(PlaylistStore store, IDbContextFactory<MelodyBridgeDbContext> factory, int trackId)>
        SetupAsync(int expectedDurationMs, params IDownloader[] downloaders)
    {
        var dbPath = Path.Combine(_dir, $"review-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddDbContextFactory<MelodyBridgeDbContext>(o => o.UseSqlite($"Data Source={dbPath};Pooling=False"));
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();

        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-review",
                Name = "Review flow test",
                SourceUrl = "stub:pl",
                TargetDirectory = _dir,
                SyncMode = "Additive",
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "mel-review-1",
                        Title = "Wrong Length Song",
                        Artist = "Artist",
                        Position = 0,
                        // 10.5 seconds by default: a 60s file is far outside
                        // the default max(30s, 10%) tolerance, a 10s file
                        // is comfortably inside it.
                        DurationMs = expectedDurationMs,
                        DownloadStatus = "pending",
                    },
                },
            });
            await db.SaveChangesAsync();
        }

        var manager = new Application.Services.DownloadManager(
            new ListDownloaderRegistry(downloaders),
            NullLogger<Application.Services.DownloadManager>.Instance);
        var store = new PlaylistStore(
            factory, Array.Empty<ISourceProvider>(), manager,
            NullLogger<PlaylistStore>.Instance);

        await using var lookup = await factory.CreateDbContextAsync();
        var trackId = await lookup.Tracks.Select(t => t.Id).SingleAsync();
        return (store, factory, trackId);
    }

    [Test]
    public async Task WrongLengthDownload_IsKeptAndFlagged_NotDeleted()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");

        var bad = new FileServingDownloader("bad", MakeMp3("bad-60s.mp3", 60));
        var (store, factory, trackId) = await SetupAsync(bad);

        var (downloaded, failed) = await store.DownloadMissingAsync("pl-review");

        Assert.That((downloaded, failed), Is.EqualTo((1, 0)),
            "a wrong-length file is a review, not a failure");
        await using var db = await factory.CreateDbContextAsync();
        var track = await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
        Assert.Multiple(() =>
        {
            Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
            Assert.That(track.CurrentPath, Is.Not.Null, "the file must stay on disk");
            Assert.That(File.Exists(track.CurrentPath!), Is.True);
            Assert.That(track.ReviewKind, Is.EqualTo("length"));
            Assert.That(track.ReviewText, Does.Contain("duration mismatch"));
            Assert.That(track.ReviewText, Does.Contain("10.5"),
                "the text names the expected length");
            Assert.That(track.SourcePluginId, Is.EqualTo("bad"),
                "the delivering provider is remembered for Retry");
            Assert.That(track.FileSizeBytes, Is.GreaterThan(0), "the facts are still measured");
        });
    }

    [Test]
    public async Task Keep_ClearsTheFlag_AndTheFileStays()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");

        var bad = new FileServingDownloader("bad", MakeMp3("bad-60s.mp3", 60));
        var (store, factory, trackId) = await SetupAsync(bad);
        await store.DownloadMissingAsync("pl-review");

        var kept = await store.ResolveQualityReviewAsync(trackId, QualityReviewAction.Keep);

        Assert.That(kept, Is.True);
        await using var db = await factory.CreateDbContextAsync();
        var track = await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
        Assert.Multiple(() =>
        {
            Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
            Assert.That(track.ReviewKind, Is.Null, "the flag is gone");
            Assert.That(track.ReviewText, Is.Null);
            Assert.That(track.CurrentPath, Is.Not.Null);
            Assert.That(File.Exists(track.CurrentPath!), Is.True, "Keep never deletes the file");
            Assert.That(track.SourcePluginId, Is.EqualTo("bad"), "the row keeps its history");
        });
    }

    [Test]
    public async Task Retry_DeletesTheFlaggedFile_ThenDownloadsFromAnotherProvider()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");

        var bad = new FileServingDownloader("bad", MakeMp3("bad-60s.mp3", 60));
        var good = new FileServingDownloader("good", MakeMp3("good-10s.mp3", 10));
        var (store, factory, trackId) = await SetupAsync(bad, good);
        await store.DownloadMissingAsync("pl-review");

        string flaggedPath;
        await using (var before = await factory.CreateDbContextAsync())
            flaggedPath = (await before.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId)).CurrentPath!;
        Assert.That(File.Exists(flaggedPath), Is.True);
        var searchesBeforeRetry = bad.SearchCalls;

        var queued = await store.ResolveQualityReviewAsync(trackId, QualityReviewAction.Retry);

        Assert.That(queued, Is.True);
        await using (var after = await factory.CreateDbContextAsync())
        {
            var track = await after.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
            Assert.Multiple(() =>
            {
                Assert.That(track.DownloadStatus, Is.EqualTo("pending"));
                Assert.That(track.ReviewKind, Is.Null, "the review is settled by the decision");
                Assert.That(track.CurrentPath, Is.Null);
                Assert.That(track.AvoidPluginId, Is.EqualTo("bad"),
                    "the retry hint names the provider that delivered the flagged file");
            });
        }
        Assert.That(File.Exists(flaggedPath), Is.False, "the flagged file is replaced, not kept");

        var result = await store.DownloadTrackAsync(trackId);

        Assert.That(result, Is.EqualTo("downloaded"));
        Assert.That(bad.SearchCalls, Is.EqualTo(searchesBeforeRetry),
            "the avoided provider is not searched again during the retry");
        Assert.That(bad.DownloadCalls, Is.EqualTo(1), "only the first, flagged download");
        Assert.That(good.DownloadCalls, Is.EqualTo(1), "the retry lands at the other provider");
        await using var final = await factory.CreateDbContextAsync();
        var done = await final.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
        Assert.Multiple(() =>
        {
            Assert.That(done.DownloadStatus, Is.EqualTo("downloaded"));
            Assert.That(done.ReviewKind, Is.Null, "the 10s file is within tolerance");
            Assert.That(done.SourcePluginId, Is.EqualTo("good"));
            Assert.That(done.CurrentPath, Does.Contain("good"));
            Assert.That(File.Exists(done.CurrentPath!), Is.True);
            Assert.That(done.AvoidPluginId, Is.Null, "the one-shot hint is consumed");
        });
    }

    [Test]
    public async Task Retry_WithNoOtherProvider_FailsHonestly_AndTheAvoidDoesNotStick()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");

        var bad = new FileServingDownloader("bad", MakeMp3("bad-60s.mp3", 60));
        var (store, factory, trackId) = await SetupAsync(bad);
        await store.DownloadMissingAsync("pl-review");

        await store.ResolveQualityReviewAsync(trackId, QualityReviewAction.Retry);
        var result = await store.DownloadTrackAsync(trackId);

        Assert.That(result, Is.EqualTo("failed"));
        await using (var db = await factory.CreateDbContextAsync())
        {
            var track = await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
            Assert.Multiple(() =>
            {
                Assert.That(track.DownloadStatus, Is.EqualTo("failed"));
                Assert.That(track.DownloadError, Does.Contain("no other provider"),
                    $"the reason names the skipped provider: {track.DownloadError}");
                Assert.That(track.AvoidPluginId, Is.Null,
                    "the hint was one-shot: a later attempt may take any provider");
            });
        }

        // The next plain attempt takes the only provider again and flags
        // again: the user keeps the last word on every file.
        var again = await store.DownloadTrackAsync(trackId);
        Assert.That(again, Is.EqualTo("downloaded"));
        await using var final = await factory.CreateDbContextAsync();
        var refetched = await final.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
        Assert.That(refetched.ReviewKind, Is.EqualTo("length"));
        Assert.That(refetched.DownloadStatus, Is.EqualTo("downloaded"));
    }

    [Test]
    public async Task WrongLengthAndInflatedFile_BecomesOneReviewWithBothKinds()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");

        // A 3 second 64 kbps noise source re-encoded at 320: the classic
        // blow-up. Expected length 60s, so the file is also far too short.
        var baseFile = RunFfmpeg(
            "-f lavfi -i \"anoisesrc=color=white:duration=3\" -b:a 64k", "base64.mp3");
        var inflated = RunFfmpeg($"-i \"{baseFile}\" -b:a 320k", "inflated.mp3");
        var (store, factory, trackId) = await SetupAsync(60_000, new FileServingDownloader("bad", inflated));

        var saved = PlaylistStore.Verification;
        PlaylistStore.Verification = () => new VerificationOptions(SpectrumMode.Thorough);
        try
        {
            await store.DownloadMissingAsync("pl-review");
        }
        finally
        {
            PlaylistStore.Verification = saved;
        }

        await using var db = await factory.CreateDbContextAsync();
        var track = await db.Tracks.AsNoTracking().SingleAsync(t => t.Id == trackId);
        Assert.Multiple(() =>
        {
            Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
            Assert.That(TrackReview.Has(track.ReviewKind, TrackReview.Length), Is.True,
                $"the length doubt must be recorded ({track.ReviewKind})");
            Assert.That(TrackReview.Has(track.ReviewKind, TrackReview.Spectrum), Is.True,
                $"the spectrum doubt must be recorded too ({track.ReviewKind})");
            Assert.That(track.ReviewText, Does.Contain("duration mismatch"));
            Assert.That(track.ReviewText, Does.Contain("inflated"));
            Assert.That(File.Exists(track.CurrentPath!), Is.True,
                "one decision settles both doubts; the file waits for it");
        });
    }

    [Test]
    public async Task ResolveQualityReview_OnAHealthyTrack_IsRefused()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");

        var good = new FileServingDownloader("good", MakeMp3("good-10s.mp3", 10));
        var (store, _, trackId) = await SetupAsync(good);
        await store.DownloadMissingAsync("pl-review");

        Assert.That(await store.ResolveQualityReviewAsync(trackId, QualityReviewAction.Keep), Is.False,
            "a track without an open review cannot be settled");
        Assert.That(await store.ResolveQualityReviewAsync(999_999, QualityReviewAction.Retry), Is.False,
            "an unknown track id is refused, not thrown");
    }
}
