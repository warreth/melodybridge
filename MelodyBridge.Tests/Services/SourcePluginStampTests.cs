using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Two download runs of the same song at the same moment (the track is
/// queued in two playlists). The producing plugin used to come from
/// manager-internal memory keyed by song, and the last finishing run
/// stamped both rows with its own provider, so a quality-review retry
/// avoided the wrong plugin afterwards. The producer now travels with the
/// run and each row keeps its own.
/// </summary>
[TestFixture]
[Category("PlaylistStore")]
public class SourcePluginStampTests
{
    private string _root = null!;
    private TestSqliteFactory _factory = null!;
    private string _mp3File = null!;
    private string _flacFile = null!;
    private const string MelodyId = "mel-shared-stamp";
    private const string SharedSong = "Shared Song";
    private const string SharedArtist = "Shared Artist";

    /// <summary>A real mp3, so the container and quality gates accept it.</summary>
    private string MakeRealAudio(string extension, string codec)
    {
        var path = Path.Combine(_root, $"fixture{extension}");
        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-hide_banner -loglevel error -f lavfi -i anullsrc=r=44100:cl=stereo -t 2 " +
                        $"-c:a {codec} \"{path}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        proc.StandardError.ReadToEnd();
        proc.WaitForExit(15000);
        return path;
    }

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-stamp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _mp3File = MakeRealAudio(".mp3", "libmp3lame");
        _flacFile = MakeRealAudio(".flac", "flac");
        _factory = new TestSqliteFactory(Path.Combine(_root, "stamp.db"));
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        var first = new PlaylistEntity
        {
            Id = "pl-stamp-1",
            Name = "Stamp One",
            SourceUrl = "stub:one",
            TargetDirectory = Path.Combine(_root, "one"),
            PreferredFormat = "mp3:320",
        };
        first.Tracks.Add(new TrackEntity
        {
            MelodyId = MelodyId, Title = SharedSong, Artist = SharedArtist, Position = 0,
            DownloadStatus = "pending",
            // No dedup link here: the stamping race lives in the shared
            // manager memory, both rows download their own file.
            SkipDedup = true,
            DurationMs = 200_000,
        });
        var second = new PlaylistEntity
        {
            Id = "pl-stamp-2",
            Name = "Stamp Two",
            SourceUrl = "stub:two",
            TargetDirectory = Path.Combine(_root, "two"),
            PreferredFormat = "flac:lossless",
        };
        second.Tracks.Add(new TrackEntity
        {
            MelodyId = MelodyId, Title = SharedSong, Artist = SharedArtist, Position = 0,
            DownloadStatus = "pending",
            SkipDedup = true,
            DurationMs = 200_000,
        });
        db.Playlists.AddRange(first, second);
        db.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private sealed class CopyingPlugin(string id, bool lossless, string fixtureFile,
        Func<Task>? beforeReturn = null) : IDownloader
    {
        public string Id => id;
        public string Name => id;
        public string Description => "";
        public PluginCapabilities Capabilities => lossless
            ? PluginCapabilities.LosslessSources(AudioFormat.Flac)
            : PluginCapabilities.Lossy(AudioFormat.Mp3);

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(new DownloaderSearchHit(
                "Song", "Artist", $"https://{id}.example/1", TimeSpan.FromSeconds(200)));

        public async Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"{melodyId}{Path.GetExtension(fixtureFile)}");
            await File.WriteAllBytesAsync(path, await System.IO.File.ReadAllBytesAsync(fixtureFile, ct), ct);
            if (beforeReturn is not null) await beforeReturn();
            return new DownloaderDownloadResult(true, path, null);
        }
    }

    private static DownloadPipeline NewPipeline(
        TestSqliteFactory factory, DownloadManager manager)
        => new(
            factory,
            manager,
            new SettingsStore(factory),
            new TrackLinker(factory, new SettingsStore(factory), new HardLinkService(),
                NullLogger<TrackLinker>.Instance),
            null, null, null,
            NullLogger<DownloadPipeline>.Instance,
            () => new MelodyBridge.Infrastructure.Audio.VerificationOptions(),
            pathLocks: null);

    [Test]
    public async Task TwoRunsOfOneSong_StampRowsWithTheirOwn_Plugins()
    {
        // Once the second row is stamped by its own run (the flac run),
        // the mp3 run's downloader lets go: the first run then reads its
        // attribution with the overwrite safely behind it.
        var secondStamped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new DownloadManager(
            new ListDownloaderRegistry(
                new CopyingPlugin("mp3-only", lossless: false, fixtureFile: _mp3File,
                    beforeReturn: () => secondStamped.Task.WaitAsync(TimeSpan.FromSeconds(15))),
                new CopyingPlugin("flac-only", lossless: true, fixtureFile: _flacFile,
                    beforeReturn: () =>
                    {
                        secondStamped.TrySetResult();
                        return Task.CompletedTask;
                    })),
            NullLogger<DownloadManager>.Instance);

        var firstRun = Task.Run(() => NewPipeline(_factory, manager).DownloadTrackAsync(1));
        var secondRun = Task.Run(() => NewPipeline(_factory, manager).DownloadTrackAsync(2));
        await Task.WhenAll(firstRun, secondRun);

        await using var db = _factory.CreateDbContext();
        var rows = await db.Tracks.AsNoTracking().ToListAsync();
        var rowState = string.Join(" | ", rows.Select(r =>
            $"{r.Id}: {r.DownloadStatus} plugin={r.SourcePluginId} err={r.DownloadError ?? "-"}"));
        Assert.That(rows.Count, Is.EqualTo(2), $"rows: {rowState}");
        Assert.Multiple(() =>
        {
            Assert.That(rows.Single(r => r.Id == 1).SourcePluginId, Is.EqualTo("mp3-only"),
                "the first run's attribution is the first run's own plugin; rows: " + rowState);
            Assert.That(rows.Single(r => r.Id == 2).SourcePluginId, Is.EqualTo("flac-only"),
                "the second run's attribution is the second run's own plugin; rows: " + rowState);
        });
    }
}
