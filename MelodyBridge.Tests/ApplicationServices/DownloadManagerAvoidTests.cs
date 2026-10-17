using MelodyBridge.Core;
using MelodyBridge.Application.Services;
using MelodyBridge.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.ApplicationServices;

/// <summary>
/// The provider-avoiding retry of the waterfall: avoidPluginId keeps one
/// plugin out of the whole attempt (ladder steps included), the manager
/// remembers which plugin produced the winning file, and an avoid that
/// leaves no provider fails with an honest reason instead of "no source
/// had this track".
/// </summary>
[TestFixture]
public class DownloadManagerAvoidTests
{
    /// <summary>Counts its calls and serves a real file when given one.</summary>
    private sealed class SpyDownloader : IDownloader
    {
        private readonly DownloaderSearchHit _hit;
        private readonly string? _file;

        public SpyDownloader(string id, DownloaderSearchHit hit, string? file = null)
        {
            Id = id;
            _hit = hit;
            _file = file;
        }

        public string Id { get; }
        public string Name => $"Spy {Id}";
        public string Description => string.Empty;
        public PluginCapabilities Capabilities { get; } = PluginCapabilities.Lossy(AudioFormat.Mp3);
        public int SearchCalls { get; private set; }
        public int DownloadCalls { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
        {
            SearchCalls++;
            return Task.FromResult<DownloaderSearchHit?>(_hit);
        }

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            DownloadCalls++;
            if (_file is null) return Task.FromResult(new DownloaderDownloadResult(false, null, "stub"));
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"{Id}-{Path.GetFileName(_file)}");
            File.Copy(_file, path, overwrite: true);
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }

    private string _dir = null!;

    [SetUp]
    public void Setup() => _dir = Path.Combine(Path.GetTempPath(), $"mb-avoid-{Guid.NewGuid():N}");

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_dir))
            try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>A real MP3 produced by ffmpeg; skips the test when ffmpeg is absent.</summary>
    private string MakeRealMp3()
    {
        var ffmpeg = MelodyBridge.Infrastructure.Audio.SpectrumAnalyzer.FindBinary("ffmpeg");
        if (ffmpeg is null) Assert.Ignore("ffmpeg is not installed on this machine");
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "source.mp3");
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-hide_banner -loglevel error -f lavfi -i anullsrc=r=44100:cl=stereo -t 2 -c:a libmp3lame -b:a 128k \"{path}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = System.Diagnostics.Process.Start(psi)!;
        proc.StandardError.ReadToEnd();
        proc.WaitForExit(10000);
        return path;
    }

    private static DownloadManager Manager(params IDownloader[] downloaders)
        => new(new ListDownloaderRegistry(downloaders), NullLogger<DownloadManager>.Instance);

    [Test]
    public async Task AvoidPlugin_SkipsItEntirely_AndTheNextPluginWins()
    {
        var file = MakeRealMp3();
        var hit = new DownloaderSearchHit("Song", "Artist", "https://example.com/1", TimeSpan.FromSeconds(200));
        var first = new SpyDownloader("first", hit, file);
        var second = new SpyDownloader("second", hit, file);
        var manager = Manager(first, second);

        var path = await manager.DownloadTrackAsync(
            new TrackQuery("Artist", "Song", DownloadQuality.Any), _dir, "mel-avoid-1",
            avoidPluginId: "first");

        Assert.That(path, Does.StartWith(Path.Combine(_dir, "second")),
            "the avoided plugin must not deliver the retry");
        Assert.That(first.SearchCalls, Is.Zero, "the avoided plugin is not even searched");
        Assert.That(first.DownloadCalls, Is.Zero);
        Assert.That(second.SearchCalls, Is.EqualTo(1));
        Assert.That(manager.LastPluginId("mel-avoid-1"), Is.EqualTo("second"),
            "the winner is remembered for a later retry");
    }

    [Test]
    public async Task WithoutAvoid_TheFirstPluginWins_AndIsRemembered()
    {
        var file = MakeRealMp3();
        var hit = new DownloaderSearchHit("Song", "Artist", "https://example.com/1", TimeSpan.FromSeconds(200));
        var first = new SpyDownloader("first", hit, file);
        var second = new SpyDownloader("second", hit, file);
        var manager = Manager(first, second);

        var path = await manager.DownloadTrackAsync(
            new TrackQuery("Artist", "Song", DownloadQuality.Any), _dir, "mel-avoid-2");

        Assert.That(path, Does.StartWith(Path.Combine(_dir, "first")));
        Assert.That(second.DownloadCalls, Is.Zero);
        Assert.That(manager.LastPluginId("mel-avoid-2"), Is.EqualTo("first"));
    }

    [Test]
    public async Task AvoidPlugin_WithNoOtherProvider_FailsWithTheHonestReason()
    {
        var hit = new DownloaderSearchHit("Song", "Artist", "https://example.com/1", TimeSpan.FromSeconds(200));
        var only = new SpyDownloader("only", hit);
        var manager = Manager(only);

        var path = await manager.DownloadTrackAsync(
            new TrackQuery("Artist", "Song", DownloadQuality.Any), _dir, "mel-avoid-3",
            avoidPluginId: "only");

        Assert.That(path, Is.Null);
        Assert.That(only.SearchCalls, Is.Zero, "the avoided plugin gets no call");
        Assert.That(manager.LastFailure("mel-avoid-3"), Does.Contain("no other provider"),
            "the failure names the skipped provider instead of pretending the track does not exist");
        Assert.That(manager.LastFailure("mel-avoid-3"), Does.Contain("Spy only"));
    }

    [Test]
    public async Task AvoidPlugin_UnknownId_ChangesNothing()
    {
        var file = MakeRealMp3();
        var hit = new DownloaderSearchHit("Song", "Artist", "https://example.com/1", TimeSpan.FromSeconds(200));
        var first = new SpyDownloader("first", hit, file);
        var manager = Manager(first);

        var path = await manager.DownloadTrackAsync(
            new TrackQuery("Artist", "Song", DownloadQuality.Any), _dir, "mel-avoid-4",
            avoidPluginId: "no-such-plugin");

        Assert.That(path, Is.Not.Null, "an unknown avoid id must not break the run");
        Assert.That(manager.LastPluginId("mel-avoid-4"), Is.EqualTo("first"));
    }

    [Test]
    public async Task LastPluginId_IsClearedByTheNextAttempt()
    {
        var file = MakeRealMp3();
        var hit = new DownloaderSearchHit("Song", "Artist", "https://example.com/1", TimeSpan.FromSeconds(200));
        var manager = Manager(new SpyDownloader("first", hit, file));

        await manager.DownloadTrackAsync(
            new TrackQuery("Artist", "Song", DownloadQuality.Any), _dir, "mel-avoid-5");
        Assert.That(manager.LastPluginId("mel-avoid-5"), Is.EqualTo("first"));

        // A miss (no hit at all) must not leave the previous winner behind.
        var miss = Manager(new SpyDownloader("silent", hit with { SourceUrl = null }));
        await miss.DownloadTrackAsync(
            new TrackQuery("Artist", "Song", DownloadQuality.Any), _dir, "mel-avoid-6");
        Assert.That(miss.LastPluginId("mel-avoid-6"), Is.Null);
    }
}
