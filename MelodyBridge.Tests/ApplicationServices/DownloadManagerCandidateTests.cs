using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.ApplicationServices;

/// <summary>
/// The manual source picker's two manager calls: SearchCandidatesAsync
/// returns what every enabled plugin found (best first, no acceptance
/// gate, guarded per plugin) and DownloadCandidateAsync downloads exactly
/// the picked candidate, whichever plugin it came from.
/// </summary>
[TestFixture]
public class DownloadManagerCandidateTests
{
    private string _dir = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mb-pick-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A plugin with canned hits that writes a real MP3 on download.</summary>
    private sealed class StubPlugin : IDownloader
    {
        private readonly IReadOnlyList<DownloaderSearchHit> _hits;

        public StubPlugin(string id, params DownloaderSearchHit[] hits)
        {
            Id = id;
            _hits = hits;
        }

        public string Id { get; }
        public string Name => $"Plugin {Id}";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;
        public bool Available { get; init; } = true;
        /// <summary>Optional barrier both plugins must be inside, proving
        /// the searches run at the same time.</summary>
        public Func<Task>? SearchBarrier { get; init; }
        public int DownloadCalls { get; private set; }
        public string? LastSourceUrl { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
            => Task.FromResult(Available);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult(_hits.FirstOrDefault());

        public async Task<IReadOnlyList<DownloaderSearchHit>> SearchAllAsync(
            TrackQuery query, CancellationToken ct = default)
        {
            if (SearchBarrier is not null)
                await SearchBarrier().WaitAsync(TimeSpan.FromSeconds(10));
            return _hits;
        }

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            DownloadCalls++;
            LastSourceUrl = sourceUrl;
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"{melodyId ?? "x"}-{Id}.mp3");
            File.WriteAllBytes(path, TestAudio.MinimalMp3());
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }

    private static DownloaderSearchHit Hit(string title, string artist, string url, double? seconds = null)
        => new(title, artist, url, seconds is null ? null : TimeSpan.FromSeconds(seconds.Value));

    [Test]
    public async Task SearchCandidates_ListsEveryPlugin_BestFirst_WithoutTheGate()
    {
        // The second hit is the shape the automatic search refuses (wrong
        // artist, shared words): the picker must still show it.
        var first = new StubPlugin("alpha",
            Hit("The Real Song", "The Artist", "https://a/1", 210));
        var slow = new StubPlugin("beta",
            Hit("The Real Song (Karaoke)", "Karaoke Band", "https://b/1", 208),
            Hit("Some Other Song", "Other Band", "https://b/2", 300));
        IDownloadManager manager = new DownloadManager(
            new ListDownloaderRegistry(first, slow),
            NullLogger<DownloadManager>.Instance);

        var candidates = await manager.SearchCandidatesAsync(
            new TrackQuery("The Artist", "The Real Song", DownloadQuality.Any, TimeSpan.FromSeconds(210)));

        Assert.That(candidates, Has.Count.EqualTo(3), "every hit is offered, gate or not");
        Assert.Multiple(() =>
        {
            Assert.That(candidates[0].SourceUrl, Is.EqualTo("https://a/1"),
                "the confident match ranks first");
            Assert.That(candidates[0].PluginId, Is.EqualTo("alpha"));
            Assert.That(candidates.Select(c => c.SourceUrl).Distinct().Count(), Is.EqualTo(candidates.Count),
                "a URL twice is one candidate");
            Assert.That(candidates.Any(c => c.SourceUrl == "https://b/2"), Is.True,
                "a refused hit is exactly what the manual picker is for");
        });
    }

    [Test]
    public async Task SearchCandidates_SkipsUnavailablePlugins()
    {
        var down = new StubPlugin("down", Hit("Song", "Artist", "https://d/1")) { Available = false };
        var up = new StubPlugin("up", Hit("Song", "Artist", "https://u/1"));
        IDownloadManager manager = new DownloadManager(
            new ListDownloaderRegistry(down, up),
            NullLogger<DownloadManager>.Instance);

        var candidates = await manager.SearchCandidatesAsync(
            new TrackQuery("Artist", "Song", DownloadQuality.Any));

        Assert.That(candidates.Select(c => c.SourceUrl), Is.EqualTo(new[] { "https://u/1" }));
    }

    [Test]
    public async Task DownloadCandidate_UsesThePickedPlugin_NotTheFirstEnabledOne()
    {
        var first = new StubPlugin("alpha", Hit("Song", "Artist", "https://a/1"));
        var second = new StubPlugin("beta", Hit("Song", "Artist", "https://b/1"));
        IDownloadManager manager = new DownloadManager(
            new ListDownloaderRegistry(first, second),
            NullLogger<DownloadManager>.Instance);

        var candidates = await manager.SearchCandidatesAsync(
            new TrackQuery("Artist", "Song", DownloadQuality.Any));
        var picked = candidates.Single(c => c.PluginId == "beta");

        var path = await manager.DownloadCandidateAsync(
            picked, new TrackQuery("Artist", "Song", DownloadQuality.Any), _dir, "mel-pick");

        Assert.Multiple(() =>
        {
            Assert.That(path, Is.Not.Null);
            Assert.That(File.Exists(path), Is.True);
            Assert.That(second.DownloadCalls, Is.EqualTo(1), "the picked plugin delivered");
            Assert.That(first.DownloadCalls, Is.EqualTo(0), "the first plugin was not consulted");
            Assert.That(second.LastSourceUrl, Is.EqualTo("https://b/1"), "the picked URL is the one downloaded");
            Assert.That(manager.LastPluginId("mel-pick"), Is.EqualTo("beta"));
        });
    }

    [Test]
    public async Task DownloadCandidate_WithADisabledPlugin_FailsHonestly()
    {
        var plugin = new StubPlugin("alpha", Hit("Song", "Artist", "https://a/1"));
        IDownloadManager manager = new DownloadManager(
            new ListDownloaderRegistry(plugin),
            NullLogger<DownloadManager>.Instance);

        var result = await manager.DownloadCandidateAsync(
            new DownloadCandidate("ghost", "Ghost", "Song", "Artist", null, null, "https://g/1",
                MatchConfidence.Low),
            new TrackQuery("Artist", "Song", DownloadQuality.Any), _dir, "mel-ghost");

        Assert.That(result, Is.Null);
        Assert.That(manager.LastFailure("mel-ghost"), Does.Contain("not enabled"));
    }
    [Test]
    public async Task SearchCandidates_RunsPluginsAtTheSameTime_AndStreamsHits()
    {
        // Both plugins must be inside their search before either may
        // answer: a sequential loop would time one out and lose it.
        var both = new TaskCompletionSource();
        var inside = 0;
        async Task BarrierAsync()
        {
            if (Interlocked.Increment(ref inside) == 2) both.TrySetResult();
            await both.Task;
        }
        var alpha = new StubPlugin("alpha", Hit("Song", "Artist", "https://a/1", 210))
        {
            SearchBarrier = BarrierAsync,
        };
        var beta = new StubPlugin("beta", Hit("Song (Live)", "Artist", "https://b/1", 220))
        {
            SearchBarrier = BarrierAsync,
        };
        IDownloadManager manager = new DownloadManager(
            new ListDownloaderRegistry(alpha, beta),
            NullLogger<DownloadManager>.Instance);

        var streamed = new List<DownloadCandidate>();
        var all = await manager.SearchCandidatesAsync(
            new TrackQuery("Artist", "Song", DownloadQuality.Any),
            streamed.Add);

        Assert.Multiple(() =>
        {
            Assert.That(all, Has.Count.EqualTo(2), "both plugins answered");
            Assert.That(streamed, Has.Count.EqualTo(2),
                "each hit is streamed as soon as its plugin answers");
        });
    }

}
