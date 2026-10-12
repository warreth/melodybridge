using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.ApplicationServices;

/// <summary>
/// The waterfall's guard rails: a plugin that throws never stops the
/// track, wrong-length hits are rejected before bytes are spent,
/// non-audio output is deleted, and the runner-up candidates of a plugin
/// are tried when the first choice cannot be downloaded.
/// </summary>
[TestFixture]
public class DownloadManagerRobustnessTests
{
    private string _dir = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mb-robust-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Configurable plugin: every failure mode is switchable.</summary>
    private sealed class StubPlugin : IDownloader
    {
        public StubPlugin(string id) => Id = id;
        public string Id { get; }
        public string Name => Id;
        public PluginCapabilities Capabilities => PluginCapabilities.Any;

        public DownloaderSearchHit? Hit;
        public bool ThrowOnAvailable;
        public bool ThrowOnSearch;
        public bool ThrowOnDownload;
        /// <summary>Produces the file for a source URL; null means "download failed".</summary>
        public Func<string, string, string?>? WriteFile;
        public readonly List<string> DownloadedUrls = new();
        public readonly List<string> SearchedUrls = new();
        public TimeSpan? LastExpectedDuration;

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
            => ThrowOnAvailable
                ? throw new InvalidOperationException("availability exploded")
                : Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(
            string artist, string title, DownloadQuality quality,
            TimeSpan? expectedDuration = null, CancellationToken ct = default)
        {
            SearchedUrls.Add($"{artist} - {title}");
            LastExpectedDuration = expectedDuration;
            if (ThrowOnSearch) throw new InvalidOperationException("search exploded");
            return Task.FromResult(Hit);
        }

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            DownloadedUrls.Add(sourceUrl);
            if (ThrowOnDownload) throw new InvalidOperationException("download exploded");
            var path = WriteFile?.Invoke(sourceUrl, outputDirectory);
            return Task.FromResult(path is null
                ? new DownloaderDownloadResult(false, null, "stub failure")
                : new DownloaderDownloadResult(true, path, null));
        }
    }

    private static DownloadManager Manager(params IDownloader[] plugins)
        => new(new ListDownloaderRegistry(plugins), NullLogger<DownloadManager>.Instance);

    private string WriteMp3(string outputDirectory, string name)
    {
        var path = Path.Combine(outputDirectory, $"{name}.mp3");
        File.WriteAllBytes(path, TestAudio.MinimalMp3());
        return path;
    }

    [Test]
    public async Task GrossDurationMismatch_IsSkippedBeforeDownload()
    {
        var plugin = new StubPlugin("snippet")
        {
            Hit = new DownloaderSearchHit("Song", "Artist", "https://x/snippet", TimeSpan.FromSeconds(30)),
            WriteFile = (_, dir) => WriteMp3(dir, "snippet"),
        };
        var manager = Manager(plugin);

        var path = await manager.DownloadTrackAsync("Artist", "Song", _dir, "mel-1",
            expectedDuration: TimeSpan.FromSeconds(240));

        Assert.That(path, Is.Null, "a 30s clip for a 4-minute track must not download");
        Assert.That(plugin.DownloadedUrls, Is.Empty);
        Assert.That(manager.LastFailure("mel-1"), Does.Contain("wrong length"));
    }

    [Test]
    public async Task ExpectedDuration_IsPassedToThePlugin()
    {
        var plugin = new StubPlugin("p")
        {
            Hit = new DownloaderSearchHit("Song", "Artist", "https://x/1", TimeSpan.FromSeconds(200)),
            WriteFile = (_, dir) => WriteMp3(dir, "p"),
        };
        var manager = Manager(plugin);

        await manager.DownloadTrackAsync("Artist", "Song", _dir, "mel-1",
            expectedDuration: TimeSpan.FromSeconds(200));

        Assert.That(plugin.LastExpectedDuration, Is.EqualTo(TimeSpan.FromSeconds(200)));
    }

    [Test]
    public async Task NonAudioOutput_IsDeleted_AndTheNextPluginWins()
    {
        var bad = new StubPlugin("bad")
        {
            Hit = new DownloaderSearchHit("Song", "Artist", "https://x/bad", TimeSpan.FromSeconds(200)),
            WriteFile = (_, dir) =>
            {
                var path = Path.Combine(dir, "bad.mp3");
                File.WriteAllText(path, "<html>captcha required</html>");
                return path;
            },
        };
        var good = new StubPlugin("good")
        {
            Hit = new DownloaderSearchHit("Song", "Artist", "https://x/good", TimeSpan.FromSeconds(200)),
            WriteFile = (_, dir) => WriteMp3(dir, "good"),
        };
        var manager = Manager(bad, good);

        var path = await manager.DownloadTrackAsync("Artist", "Song", _dir, "mel-1");

        Assert.That(path, Is.Not.Null);
        Assert.That(path, Does.EndWith("good.mp3"));
        Assert.That(File.Exists(Path.Combine(_dir, "bad.mp3")), Is.False,
            "the error page must not survive as an audio file");
    }

    [Test]
    public async Task ThrowingAvailability_DoesNotStopTheWaterfall()
    {
        var broken = new StubPlugin("broken") { ThrowOnAvailable = true };
        var good = new StubPlugin("good")
        {
            Hit = new DownloaderSearchHit("Song", "Artist", "https://x/1", TimeSpan.FromSeconds(200)),
            WriteFile = (_, dir) => WriteMp3(dir, "good"),
        };
        var manager = Manager(broken, good);

        var path = await manager.DownloadTrackAsync("Artist", "Song", _dir, "mel-1");

        Assert.That(path, Is.Not.Null, "a throwing availability probe is a skip, not a crash");
        Assert.That(good.DownloadedUrls, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ThrowingSearch_DoesNotStopTheWaterfall()
    {
        var broken = new StubPlugin("broken") { ThrowOnSearch = true };
        var good = new StubPlugin("good")
        {
            Hit = new DownloaderSearchHit("Song", "Artist", "https://x/1", TimeSpan.FromSeconds(200)),
            WriteFile = (_, dir) => WriteMp3(dir, "good"),
        };
        var manager = Manager(broken, good);

        var path = await manager.DownloadTrackAsync("Artist", "Song", _dir, "mel-1");

        Assert.That(path, Is.Not.Null);
        Assert.That(good.DownloadedUrls, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ThrowingDownload_DoesNotStopTheWaterfall()
    {
        var broken = new StubPlugin("broken")
        {
            Hit = new DownloaderSearchHit("Song", "Artist", "https://x/1", TimeSpan.FromSeconds(200)),
            ThrowOnDownload = true,
        };
        var good = new StubPlugin("good")
        {
            Hit = new DownloaderSearchHit("Song", "Artist", "https://x/2", TimeSpan.FromSeconds(200)),
            WriteFile = (_, dir) => WriteMp3(dir, "good"),
        };
        var manager = Manager(broken, good);

        var path = await manager.DownloadTrackAsync("Artist", "Song", _dir, "mel-1");

        Assert.That(path, Is.Not.Null);
        Assert.That(good.DownloadedUrls, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Alternatives_AreTried_WhenThePrimaryDownloadFails()
    {
        var primary = new DownloaderSearchHit("Song", "Artist", "https://x/primary", TimeSpan.FromSeconds(200));
        var alternative = new DownloaderSearchHit("Song (Official Audio)", "Artist", "https://x/alt", TimeSpan.FromSeconds(200));
        var plugin = new StubPlugin("p")
        {
            Hit = primary with { Alternatives = new[] { alternative } },
            WriteFile = (url, dir) => url == "https://x/alt" ? WriteMp3(dir, "alt") : null,
        };
        var manager = Manager(plugin);

        var path = await manager.DownloadTrackAsync("Artist", "Song", _dir, "mel-1");

        Assert.That(path, Is.Not.Null, "the runner-up candidate must rescue the plugin");
        Assert.That(plugin.DownloadedUrls, Is.EqualTo(new[] { "https://x/primary", "https://x/alt" }));
    }

    [Test]
    public async Task FailedProgress_CarriesAWarning_NotAPath()
    {
        var plugin = new StubPlugin("p") { Hit = null };
        var manager = Manager(plugin);

        await manager.DownloadTrackAsync("Artist", "Song", _dir, "mel-1");

        var progress = manager.SnapshotProgress().Single(p => p.MelodyId == "mel-1");
        Assert.That(progress.Status, Is.EqualTo("failed"));
        Assert.That(progress.FilePath, Is.Null, "the failure message must not sit in FilePath");
        Assert.That(progress.Warning, Is.Not.Null.And.Not.Empty);
    }

    [Test]
    public async Task SearchHitAboveTheBand_IsSkipped_NextPluginWins()
    {
        var fat = new StubPlugin("fat")
        {
            Hit = new DownloaderSearchHit("Song", "Artist", "https://x/fat", TimeSpan.FromSeconds(200), BitrateKbps: 320),
            WriteFile = (_, dir) => WriteMp3(dir, "fat"),
        };
        var fit = new StubPlugin("fit")
        {
            Hit = new DownloaderSearchHit("Song", "Artist", "https://x/fit", TimeSpan.FromSeconds(200), BitrateKbps: 192),
            WriteFile = (_, dir) => WriteMp3(dir, "fit"),
        };
        var manager = Manager(fat, fit);

        var path = await manager.DownloadTrackAsync("Artist", "Song", _dir, "mel-1",
            quality: new DownloadQuality(AudioFormat.Auto, null, MaxKbps: 256));

        Assert.That(path, Does.EndWith("fit.mp3"));
        Assert.That(fat.DownloadedUrls, Is.Empty, "the 320 kbps hit must not download under a 256 cap");
    }
}
