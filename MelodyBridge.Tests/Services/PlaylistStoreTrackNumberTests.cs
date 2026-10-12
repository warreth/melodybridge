using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Track numbers written to the files must be 1-based: the snapshot
/// Position is 0-based and the UI shows 1-based, so the first track used
/// to get no number at all and every other track one too low. Real
/// SQLite, real audio, tags read back from the produced files.
/// </summary>
[TestFixture]
public class PlaylistStoreTrackNumberTests
{
    private string _root = null!;
    private TestSqliteFactory _factory = null!;
    private Func<SpectrumMode> _savedSpectrum = null!;
    private string? _sourceFlac;
    private string _downloadDir = null!;

    [SetUp]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-tags-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _downloadDir = Path.Combine(_root, "music");
        Directory.CreateDirectory(_downloadDir);
        _factory = new TestSqliteFactory(Path.Combine(_root, "tags.db"));
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();

        // Keep this test fast and deterministic: the spectrum check is a
        // separate feature with its own coverage.
        _savedSpectrum = PlaylistStore.SpectrumVerification;
        PlaylistStore.SpectrumVerification = () => SpectrumMode.Off;

        _sourceFlac = TryMakeFlac(Path.Combine(_root, "source.flac"));
    }

    [TearDown]
    public void TearDown()
    {
        PlaylistStore.SpectrumVerification = _savedSpectrum;
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static string? TryMakeFlac(string path)
    {
        var ffmpeg = SpectrumAnalyzer.FindBinary("ffmpeg");
        if (ffmpeg is null) return null;
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-y", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=3", path })
            psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi);
        if (proc is null) return null;
        proc.StandardError.ReadToEnd();
        proc.WaitForExit(30000);
        return proc.ExitCode == 0 ? path : null;
    }

    /// <summary>Copies one prepared FLAC for whichever track is asked for.</summary>
    private sealed class ServingDownloader(string source) : IDownloader
    {
        public string Id => "serving";
        public string Name => "Serving (test)";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(new DownloaderSearchHit(
                query.Title, query.Artist, "flac://serving", query.ExpectedDuration,
                MatchConfidence: MatchConfidence.High));

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"{melodyId}.flac");
            File.Copy(source, path, overwrite: true);
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }

    [Test]
    public async Task TwoTrackPlaylist_WritesOneBasedTrackNumbers()
    {
        if (_sourceFlac is null) Assert.Ignore("ffmpeg is not installed on this machine");
        if (SpectrumAnalyzer.FindFfprobe() is null) Assert.Ignore("ffprobe is not installed on this machine");

        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-tags",
                Name = "Tag numbers",
                SourceUrl = "stub:tags",
                TargetDirectory = _downloadDir,
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "mel-first", Title = "First Song", Artist = "A",
                        Position = 0, DurationMs = 3000,
                    },
                    new()
                    {
                        MelodyId = "mel-second", Title = "Second Song", Artist = "B",
                        Position = 1, DurationMs = 3000,
                    },
                },
            });
            await db.SaveChangesAsync();
        }

        var manager = new DownloadManager(
            new ListDownloaderRegistry(new ServingDownloader(_sourceFlac!)),
            NullLogger<DownloadManager>.Instance);
        var store = new PlaylistStore(
            _factory, Array.Empty<ISourceProvider>(), manager,
            NullLogger<PlaylistStore>.Instance);

        await store.DownloadMissingAsync("pl-tags");

        // Read the statuses and paths back from a fresh context, then the
        // tags from the actual bytes.
        var rows = await ReadTracksAsync();
        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows.All(r => r.DownloadStatus == "downloaded"), Is.True,
            "both tracks must download");

        var first = TagLib.File.Create(rows[0].CurrentPath!);
        var second = TagLib.File.Create(rows[1].CurrentPath!);
        Assert.Multiple(() =>
        {
            Assert.That(first.Tag.Track, Is.EqualTo(1),
                "snapshot position 0 is track 1, not 'no number'");
            Assert.That(second.Tag.Track, Is.EqualTo(2),
                "snapshot position 1 is track 2, not 1");
        });
    }

    private async Task<List<TrackEntity>> ReadTracksAsync()
    {
        await using var db = _factory.CreateDbContext();
        return await db.Tracks.AsNoTracking()
            .OrderBy(t => t.Position)
            .ToListAsync();
    }
}
