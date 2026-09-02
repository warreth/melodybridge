using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// FileIntegrity against REAL audio bytes and the corrupt-download handling
/// in DownloadMissingAsync: a valid file passes, garbage and truncated files
/// fail, and a plugin that delivers garbage bytes gets its file deleted and
/// the track marked failed. Files are generated with ffmpeg at test time;
/// tests skip honestly when ffmpeg is absent (like the yt-dlp tests).
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class CorruptionCheckTests
{
    private string _dir = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mb-corrupt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
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

    /// <summary>Runs ffmpeg; empty string on success, an error description otherwise.</summary>
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

    private static string NewDbPath([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), $"mb-corrupt-{test}-{Guid.NewGuid():N}.db");

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

    [Test]
    public void Check_ValidFile_IsOk()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");

        var path = Path.Combine(_dir, "valid.mp3");
        Assert.That(RunFfmpeg(
            $"-y -f lavfi -i anullsrc=r=44100:cl=stereo -t 10 -c:a libmp3lame -b:a 128k \"{path}\""), Is.Empty);

        var result = FileIntegrity.Check(path, TimeSpan.FromSeconds(10));

        Assert.That(result.Ok, Is.True, $"a genuine file must pass; reason: {result.Reason}");
    }

    [Test]
    public void Check_GarbageBytes_IsNotOk()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");

        var path = Path.Combine(_dir, "garbage.mp3");
        System.IO.File.WriteAllBytes(path, RandomBytes(4096));

        var result = FileIntegrity.Check(path);

        Assert.That(result.Ok, Is.False, "random bytes must never pass");
        Assert.That(result.Reason, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public void Check_TruncatedFile_FailsDurationMismatchOrParse()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");

        // No Xing header: ffprobe then measures the frames actually present,
        // so a half-truncated file reports half the expected duration.
        var full = Path.Combine(_dir, "full.mp3");
        Assert.That(RunFfmpeg(
            $"-y -f lavfi -i anullsrc=r=44100:cl=stereo -t 300 -c:a libmp3lame -b:a 128k -write_xing 0 \"{full}\""), Is.Empty);

        var truncated = Path.Combine(_dir, "truncated.mp3");
        var bytes = System.IO.File.ReadAllBytes(full);
        System.IO.File.WriteAllBytes(truncated, bytes[..(bytes.Length / 2)]);

        var result = FileIntegrity.Check(truncated, TimeSpan.FromSeconds(300));

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.False, "a half-file must not pass the duration check");
            Assert.That(result.Reason, Does.Contain("duration mismatch").Or.Contain("no duration").Or.Contain("ffprobe failed"),
                $"unexpected reason: {result.Reason}");
        });
    }

    [Test]
    public void Check_ValidFile_WrongExpectedDuration_Fails()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");

        var path = Path.Combine(_dir, "short.mp3");
        Assert.That(RunFfmpeg(
            $"-y -f lavfi -i anullsrc=r=44100:cl=stereo -t 10 -c:a libmp3lame -b:a 128k \"{path}\""), Is.Empty);

        // 10s file against a 300s expectation: far outside max(30s, 10%).
        var result = FileIntegrity.Check(path, TimeSpan.FromSeconds(300));

        Assert.That(result.Ok, Is.False);
        Assert.That(result.Reason, Does.Contain("duration mismatch"));
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(42).NextBytes(bytes);
        return bytes;
    }

    /// <summary>
    /// Plugin that downloads a REAL file but delivers garbage bytes with an
    /// .mp3 extension: the plugin "succeeded", the file is garbage.
    /// </summary>
    private sealed class GarbageWritingDownloader : IDownloader
    {
        public string Id => "garbage-writer";
        public string Name => "Garbage Writer (test)";

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(string artist, string title, DownloadQuality quality, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(
                new DownloaderSearchHit(title, artist, "https://example.com/x", TimeSpan.FromSeconds(180)));

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId, DownloadQuality? quality = null, CancellationToken ct = default)
        {
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"{melodyId}.mp3");
            System.IO.File.WriteAllBytes(path, RandomBytes(4096));
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }

    private static PlaylistStore NewStore(
        IDbContextFactory<MelodyBridgeDbContext> factory, params IDownloader[] downloaders)
        => new(factory,
            Array.Empty<ISourceProvider>(),
            new Application.Services.DownloadManager(
                new StubRegistry(downloaders),
                NullLogger<Application.Services.DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance);

    private sealed class StubRegistry : IDownloaderRegistry
    {
        private readonly IDownloader[] _downloaders;
        public StubRegistry(IDownloader[] downloaders) => _downloaders = downloaders;
        public IReadOnlyList<IDownloader> GetAll() => _downloaders;
        public IDownloader? Get(string id) => _downloaders.FirstOrDefault(d => d.Id == id);
        public IReadOnlyList<IDownloader> GetEnabled() => _downloaders;
        public Task SetEnabledAsync(string id, bool enabled) => Task.CompletedTask;
        public bool IsEnabled(string id) => true;
        public Task<int> GetPriorityAsync(string id, CancellationToken ct = default) => Task.FromResult(0);
        public Task SetPriorityAsync(string id, int priority, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> GetConfigAsync(string id, string key, CancellationToken ct = default) => Task.FromResult("");
        public Task SetConfigAsync(string id, string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Test]
    public async Task DownloadMissing_CorruptFile_IsDeletedAndTrackFailed()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");

        var dbPath = NewDbPath();
        try
        {
            var factory = await NewDbFactory(dbPath);
            var store = NewStore(factory, new GarbageWritingDownloader());

            await using (var db = await factory.CreateDbContextAsync())
            {
                db.Playlists.Add(new PlaylistEntity
                {
                    Id = "pl-corrupt",
                    Name = "Corrupt Me",
                    SourceUrl = "stub:pl",
                    TargetDirectory = _dir,
                    SyncMode = "Additive",
                    Tracks = new List<TrackEntity>
                    {
                        new()
                        {
                            MelodyId = "mel-corrupt-1",
                            Title = "Corrupt Song",
                            Artist = "Artist A",
                            DurationMs = 180_000,
                            DownloadStatus = "pending",
                            Position = 0,
                        },
                    },
                });
                await db.SaveChangesAsync();
            }

            var (downloaded, failed) = await store.DownloadMissingAsync("pl-corrupt");

            Assert.That((downloaded, failed), Is.EqualTo((0, 1)),
                "a garbage file must count as failed, not downloaded");

            await using var checkDb = await factory.CreateDbContextAsync();
            var track = await checkDb.Tracks.AsNoTracking().SingleAsync(t => t.MelodyId == "mel-corrupt-1");
            Assert.That(track.DownloadStatus, Is.EqualTo("failed"));
            Assert.That(track.DownloadError, Does.Contain("corrupt"),
                $"the error must name the corruption; got: {track.DownloadError}");
            Assert.That(track.CurrentPath, Is.Null, "a corrupt download must not keep its path");
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { System.IO.File.Delete(dbPath + suffix); } catch { }
        }
    }
}
