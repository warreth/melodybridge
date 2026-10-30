using System.Diagnostics;
using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// A real download whose plain file name is already occupied by an
/// orphaned copy of the same track: the new bytes replace that file
/// instead of landing as " (2)". A file of another song keeps its name.
/// The whole pipeline runs: a real downloader, ffprobe integrity, tagging
/// and the name pass.
/// </summary>
[TestFixture]
[Category("Dedup")]
public class DownloadPipelineDuplicateNameTests
{
    private string _dbPath = null!;
    private string _root = null!;
    private string _folder = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public void SetUp()
    {
        var id = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-dupname-{id}.db");
        _root = Path.Combine(Path.GetTempPath(), $"mb-dupname-{id}");
        _folder = Path.Combine(_root, "Chill");
        Directory.CreateDirectory(_folder);
        _factory = new TestSqliteFactory(_dbPath);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
    }

    private static string WriteFlac(string path, int seconds)
    {
        var ffmpeg = SpectrumAnalyzer.FindBinary("ffmpeg");
        if (ffmpeg is null) Assert.Ignore("ffmpeg is required for a real FLAC fixture");
        var info = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[]
                 {
                     "-y", "-v", "error", "-f", "lavfi", "-i",
                     $"sine=frequency=440:duration={seconds}", "-c:a", "flac", path,
                 })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        process.WaitForExit(30000);
        Assert.That(process.ExitCode, Is.Zero, "the FLAC fixture did not encode");
        return path;
    }

    private async Task<int> SeedTrackAsync()
    {
        await using var db = _factory.CreateDbContext();
        var track = new TrackEntity
        {
            MelodyId = "spotify:kids",
            Title = "Kids",
            Artist = "MGMT",
            DurationMs = 3000,
            Position = 0,
            DownloadStatus = "pending",
            PlaylistEntityId = "pl-chill",
        };
        db.Playlists.Add(new PlaylistEntity
        {
            Id = "pl-chill",
            Name = "Chill",
            SourceUrl = "https://open.spotify.com/playlist/chill",
            TargetDirectory = _folder,
            PreferredFormat = "flac",
            Tracks = new List<TrackEntity> { track },
        });
        await db.SaveChangesAsync();
        return track.Id;
    }

    private PlaylistStore Store(IDownloader downloader)
        => new(
            _factory,
            Array.Empty<ISourceProvider>(),
            new DownloadManager(new ListDownloaderRegistry(downloader),
                NullLogger<DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance);

    private sealed class ServingDownloader(string file) : IDownloader
    {
        public string Id => "serving";
        public string Name => "Serving";
        public string Description => "copies a prepared file";
        public PluginCapabilities Capabilities => new(
        [
            new QualityDeliverable(AudioFormat.Flac, Lossless: true, 900, 10000),
        ]);
        public int Calls { get; private set; }
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(new DownloaderSearchHit(
                query.Title, query.Artist, "https://serve.example/1",
                TimeSpan.FromSeconds(4), BitrateKbps: null));
        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            Calls++;
            var path = Path.Combine(outputDirectory, Path.GetFileName(file));
            File.Copy(file, path, overwrite: true);
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }

    private async Task<string?> CurrentPathAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.Tracks.AsNoTracking()
            .Where(t => t.PlaylistEntityId == "pl-chill")
            .Select(t => t.CurrentPath)
            .SingleAsync();
    }

    [Test]
    public async Task SameTrackOrphan_IsReplacedInsteadOfNumbered()
    {
        var newFile = WriteFlac(Path.Combine(_root, "new.flac"), seconds: 4);
        var plain = Path.Combine(_folder, "MGMT - Kids.flac");
        WriteFlac(plain, seconds: 3);
        TaglibHelper.WriteTags(plain, title: "Kids", artist: "MGMT");
        TaglibHelper.WriteMelodyId(plain, "spotify:kids");
        var trackId = await SeedTrackAsync();

        var downloader = new ServingDownloader(newFile);
        var store = Store(downloader);
        var status = await store.DownloadTrackAsync(trackId);
        var path = await CurrentPathAsync();

        Assert.That(status, Is.EqualTo("downloaded"));
        Assert.That(downloader.Calls, Is.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(Path.GetFileName(path!), Is.EqualTo("MGMT - Kids.flac"),
                "the orphan's plain name is reused");
            Assert.That(File.Exists(Path.Combine(_folder, "MGMT - Kids (2).flac")), Is.False,
                "no numbered duplicate is left behind");
            Assert.That(TagLib.File.Create(path!).Properties.Duration.TotalSeconds,
                Is.EqualTo(4).Within(0.5), "the new bytes are the file that survived");
        });
    }

    [Test]
    public async Task AnotherTracksFile_KeepsItsNameAndForcesANumberedTarget()
    {
        var newFile = WriteFlac(Path.Combine(_root, "new.flac"), seconds: 4);
        var plain = Path.Combine(_folder, "MGMT - Kids.flac");
        WriteFlac(plain, seconds: 3);
        TaglibHelper.WriteTags(plain, title: "Kids", artist: "MGMT");
        TaglibHelper.WriteMelodyId(plain, "spotify:another-song");
        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-other",
                Name = "Other",
                SourceUrl = "https://open.spotify.com/playlist/other",
                TargetDirectory = _folder,
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "spotify:another-song",
                        Title = "Kids",
                        Artist = "MGMT",
                        Position = 0,
                        DownloadStatus = "downloaded",
                        CurrentPath = plain,
                        PlaylistEntityId = "pl-other",
                    },
                },
            });
            await db.SaveChangesAsync();
        }
        var trackId = await SeedTrackAsync();

        var downloader = new ServingDownloader(newFile);
        var status = await Store(downloader).DownloadTrackAsync(trackId);
        var path = await CurrentPathAsync();

        Assert.That(status, Is.EqualTo("downloaded"));
        Assert.Multiple(() =>
        {
            Assert.That(path, Does.Contain(" (2).flac"),
                "another song's file is never overwritten");
            Assert.That(TaglibHelper.ReadMelodyId(plain), Is.EqualTo("spotify:another-song"));
            Assert.That(TaglibHelper.ReadMelodyId(path!), Is.EqualTo("spotify:kids"));
        });
    }
}
