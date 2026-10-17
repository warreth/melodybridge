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
/// The full post-download pipeline against a REAL audio file: write-through
/// tagging (title/artist/album/track), MELODY_ID, and the quality verdicts
/// that fill the track's review and warning columns.
/// The audio is a real ffmpeg-generated file; spectrum assertions skip
/// honestly when ffmpeg is missing.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class DownloadWriteThroughTests
{
    private static string NewDbPath([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), $"mb-wt-{test}-{Guid.NewGuid():N}.db");

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

    /// <summary>Generates a real FLAC via ffmpeg (tag-friendly, fast).</summary>
    private static string GenerateFlac(string path, string duration = "3")
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-y -f lavfi -i \"anoisesrc=color=white:duration={duration}\" \"{path}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.StandardError.ReadToEnd();
        p.WaitForExit(60000);
        return p.ExitCode == 0 ? string.Empty : $"ffmpeg exited {p.ExitCode}";
    }

    /// <summary>Writes REAL pre-generated FLAC files and tags MELODY_ID itself.</summary>
    private sealed class RealFileDownloader : IDownloader
    {
        public string Id => "real-file";
        public string Name => "Real File (test)";
        private readonly string _sourceFlac;
        public string? LastSourceUrl;
        public PluginCapabilities Capabilities => PluginCapabilities.Any;

        public RealFileDownloader(string sourceFlac) => _sourceFlac = sourceFlac;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(
                new DownloaderSearchHit(query.Title, query.Artist, "flac://real", TimeSpan.FromSeconds(3),
                    MatchConfidence: MatchConfidence.High));

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId, DownloadQuality? quality = null, CancellationToken ct = default)
        {
            LastSourceUrl = sourceUrl;
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"{melodyId}.flac");
            File.Copy(_sourceFlac, path, overwrite: true);
            MelodyBridge.Infrastructure.Tagging.TaglibHelper.WriteMelodyId(path, melodyId);
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }

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

    private static PlaylistStore NewStoreWithSource(
        IDbContextFactory<MelodyBridgeDbContext> factory, string sourceFlac, string _) =>
        new(
            factory,
            Array.Empty<ISourceProvider>(),
            new Application.Services.DownloadManager(
                new StubRegistry(new IDownloader[] { new RealFileDownloader(sourceFlac) }),
                NullLogger<Application.Services.DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance);

    private static async Task<(PlaylistStore store, IDbContextFactory<MelodyBridgeDbContext> factory, PlaylistEntity playlist, string dir)>
        SetupAsync(string test)
    {
        var dbPath = NewDbPath(test);
        var dir = Path.Combine(Path.GetTempPath(), $"mb-wt-dir-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        var services = new ServiceCollection();
        services.AddDbContextFactory<MelodyBridgeDbContext>(o => o.UseSqlite($"Data Source={dbPath};Pooling=False"));
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.Database.EnsureCreatedAsync();
            var playlist = new PlaylistEntity
            {
                Id = "pl-wt",
                Name = "Write-through test",
                SourceUrl = "stub:playlist",
                TargetDirectory = dir,
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "mel-wt-1",
                        Title = "Write Through",
                        Artist = "Test Artist",
                        Album = "Test Album",
                        Position = 1,
                        DurationMs = 3000,
                    },
                },
            };
            db.Playlists.Add(playlist);
            await db.SaveChangesAsync();
        }

        var sourceFlac = Path.Combine(dir, "source.flac");
        var ffmpegError = GenerateFlac(sourceFlac);
        if (ffmpegError.Length > 0) Assert.Ignore($"ffmpeg unavailable: {ffmpegError}");

        var store = NewStoreWithSource(factory, sourceFlac, dir);

        await using (var db = await factory.CreateDbContextAsync())
        {
            var playlist = await db.Playlists.Include(p => p.Tracks).FirstAsync();
            return (store, factory, playlist, dir);
        }
    }

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
    public async Task Download_WritesFullTags_AndMelodyId()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");

        var (store, factory, playlist, dir) = await SetupAsync(nameof(Download_WritesFullTags_AndMelodyId));

        await store.DownloadMissingAsync(playlist.Id);

        await using var db = await factory.CreateDbContextAsync();
        var track = await db.Tracks.FirstAsync(t => t.MelodyId == "mel-wt-1");

        Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"));
        Assert.That(track.CurrentPath, Is.Not.Null);

        // Full write-through: every tag from the playlist snapshot lands in the file.
        var tags = TagLib.File.Create(track.CurrentPath!);
        Assert.Multiple(() =>
        {
            Assert.That(tags.Tag.Title, Is.EqualTo("Write Through"), "title must be written");
            Assert.That(tags.Tag.Performers, Is.EqualTo(new[] { "Test Artist" }), "artist must be written");
            Assert.That(tags.Tag.Album, Is.EqualTo("Test Album"), "album must be written");
            Assert.That(tags.Tag.Track, Is.EqualTo(2),
                "snapshot position 1 is the second track, tagged 1-based");
        });

        // MELODY_ID survives the write-through.
        Assert.That(
            MelodyBridge.Infrastructure.Tagging.TaglibHelper.ReadMelodyId(track.CurrentPath!),
            Is.EqualTo("mel-wt-1"));

        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task Download_FillsRealQualityColumns_FromTheFile()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");

        var (store, factory, playlist, dir) = await SetupAsync(
            nameof(Download_FillsRealQualityColumns_FromTheFile));

        await store.DownloadMissingAsync(playlist.Id);

        await using var db = await factory.CreateDbContextAsync();
        var track = await db.Tracks.FirstAsync(t => t.MelodyId == "mel-wt-1");

        Assert.That(track.FileSizeBytes, Is.GreaterThan(0),
            "the file size must come from the real file");
        Assert.That(track.SampleRateHz, Is.GreaterThan(0),
            "the sample rate must come from the real file");
        Assert.That(track.MediaType, Is.EqualTo("flac"), "container must come from the extension");

        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task Download_WhiteNoise_GetsNoInflationWarning()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");

        var (store, factory, playlist, dir) = await SetupAsync(
            nameof(Download_WhiteNoise_GetsNoInflationWarning));

        await WithVerification(new VerificationOptions(SpectrumMode.Thorough), async () =>
        {
            await store.DownloadMissingAsync(playlist.Id);
        });

        await using var db = await factory.CreateDbContextAsync();
        var track = await db.Tracks.FirstAsync(t => t.MelodyId == "mel-wt-1");

        Assert.That(track.ReviewKind, Is.Null,
            "a genuine full-spectrum file must not be flagged as inflated");
        Assert.That(track.Warning, Is.Null,
            "and it must not carry a phantom quality hint either; got: " + track.Warning);

        Directory.Delete(dir, recursive: true);
    }

    [Test]
    public async Task Download_InflatedFile_GetsFlaggedForReview()
    {
        if (!FfmpegAvailable()) Assert.Ignore("ffmpeg not installed");

        // 64 kbps MP3 base re-encoded to 320: the classic blow-up.
        var (store, factory, playlist, dir) = await SetupAsync(
            nameof(Download_InflatedFile_GetsFlaggedForReview));

        // Replace the source with the inflated variant: a 64 kbps MP3
        // re-encoded to 320, the classic blow-up file. It must stay an
        // MP3: a lossless container of the same content is not inflated,
        // only a high-bitrate header on a source that stopped low is.
        var fake = Path.Combine(dir, "fake-source.mp3");
        var mid = Path.Combine(dir, "base64.mp3");
        Assert.That(RunFfmpeg(
            $"-y -f lavfi -i \"anoisesrc=color=white:duration=3\" -b:a 64k \"{mid}\""), Is.Empty);
        Assert.That(RunFfmpeg($"-y -i \"{mid}\" -b:a 320k \"{fake}\""), Is.Empty);
        // NOTE: the store's downloader was built with the genuine source;
        // regenerate the store pointing at the fake source.
        store = NewStoreWithSource(factory, fake, dir);

        await WithVerification(new VerificationOptions(SpectrumMode.Thorough), async () =>
        {
            await store.DownloadMissingAsync(playlist.Id);
        });

        await using var db = await factory.CreateDbContextAsync();
        var track = await db.Tracks.FirstAsync(t => t.MelodyId == "mel-wt-1");

        // The 64 kbps source's lowpass under a 320 kbps header: the
        // analyzer must flag it, and the file must stay on disk for the
        // user's Keep or Retry decision.
        Assert.That(track.DownloadStatus, Is.EqualTo("downloaded"),
            "a quality flag keeps the file, it does not fail the track");
        Assert.That(track.ReviewKind, Does.Contain("spectrum"),
            "the flag must record the spectrum kind");
        Assert.That(track.ReviewText, Does.Contain("inflated"),
            $"unexpected review text: {track.ReviewText}");
        Assert.That(File.Exists(track.CurrentPath), Is.True,
            "the flagged file stays in the library until the user decides");
        Assert.That(track.SourcePluginId, Is.EqualTo("real-file"),
            "the producing plugin is remembered for a provider-avoiding retry");

        Directory.Delete(dir, recursive: true);
    }

    /// <summary>Runs the body with the app-wide verification replaced, restoring it after.</summary>
    private static async Task WithVerification(VerificationOptions options, Func<Task> body)
    {
        var saved = PlaylistStore.Verification;
        PlaylistStore.Verification = () => options;
        try { await body(); }
        finally { PlaylistStore.Verification = saved; }
    }
}
