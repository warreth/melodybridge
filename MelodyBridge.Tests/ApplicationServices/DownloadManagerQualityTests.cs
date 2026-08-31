using MelodyBridge.Core;
using MelodyBridge.Application.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.ApplicationServices;

/// <summary>
/// Quality-floor behavior of the waterfall, with the real DownloadManager
/// and a real registry stub. Verifies that hits below the requested kbps
/// are skipped and the next plugin wins.
/// </summary>
[TestFixture]
public class DownloadManagerQualityTests
{
    private sealed class FixedHitDownloader : IDownloader
    {
        private readonly DownloaderSearchHit? _hit;
        public FixedHitDownloader(string id, string name, DownloaderSearchHit? hit)
        {
            Id = id;
            Name = name;
            _hit = hit;
        }
        public string Id { get; }
        public string Name { get; }
        public string Description => string.Empty;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DownloaderSearchHit?> SearchAsync(string artist, string title, int minimumKbps, CancellationToken ct = default)
            => Task.FromResult(_hit);
        public Task<DownloaderDownloadResult> DownloadAsync(string sourceUrl, string outputDirectory, string? melodyId, CancellationToken ct = default)
            => Task.FromResult(new DownloaderDownloadResult(true, $"/tmp/{Id}.mp3", null));
    }

    private sealed class ListRegistry : IDownloaderRegistry
    {
        private readonly IDownloader[] _downloaders;
        public ListRegistry(params IDownloader[] downloaders) => _downloaders = downloaders;
        public IReadOnlyList<IDownloader> GetAll() => _downloaders;
        public IDownloader? Get(string id) => _downloaders.FirstOrDefault(d => d.Id == id);
        public IReadOnlyList<IDownloader> GetEnabled() => _downloaders;
        public Task SetEnabledAsync(string id, bool enabled) => Task.CompletedTask;
        public bool IsEnabled(string id) => true;
        public Task<int> GetPriorityAsync(string id, CancellationToken ct = default) => Task.FromResult(0);
        public Task SetPriorityAsync(string id, int priority, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Test]
    public async Task TrackBelowQualityFloor_IsSkippedNextPluginWins()
    {
        var lowQuality = new DownloaderSearchHit("Low Rip", "Artist", "https://low.example/1", TimeSpan.FromSeconds(200), BitrateKbps: 128);
        var goodQuality = new DownloaderSearchHit("Good Rip", "Artist", "https://good.example/1", TimeSpan.FromSeconds(200), BitrateKbps: 320);

        var manager = new DownloadManager(
            new ListRegistry(
                new FixedHitDownloader("low", "Low Plugin", lowQuality),
                new FixedHitDownloader("good", "Good Plugin", goodQuality)),
            NullLogger<DownloadManager>.Instance);

        var path = await manager.DownloadTrackAsync("Artist", "Song", "/tmp", "mel-1", minimumKbps: 320);

        Assert.That(path, Is.EqualTo("/tmp/good.mp3"),
            "the 128 kbps hit must be skipped and the 320 kbps plugin must win");
    }

    [Test]
    public async Task TrackWithUnknownBitrate_IsAllowed()
    {
        // Search hits without bitrate info (YouTube flat entries) stay usable.
        var unknown = new DownloaderSearchHit("Mystery Rip", "Artist", "https://unknown.example/1", null);
        var manager = new DownloadManager(
            new ListRegistry(new FixedHitDownloader("u", "Unknown Plugin", unknown)),
            NullLogger<DownloadManager>.Instance);

        var path = await manager.DownloadTrackAsync("Artist", "Song", "/tmp", "mel-2", minimumKbps: 320);

        Assert.That(path, Is.EqualTo("/tmp/u.mp3"),
            "unknown bitrate must not be rejected: the download-time gate covers it");
    }

    [Test]
    public async Task AllHitsBelowFloor_FailsHonest()
    {
        var lowQuality = new DownloaderSearchHit("Low Rip", "Artist", "https://low.example/1", null, BitrateKbps: 96);
        var manager = new DownloadManager(
            new ListRegistry(new FixedHitDownloader("low", "Low Plugin", lowQuality)),
            NullLogger<DownloadManager>.Instance);

        var path = await manager.DownloadTrackAsync("Artist", "Song", "/tmp", "mel-3", minimumKbps: 320);

        Assert.That(path, Is.Null, "when every plugin is below the floor, no file must be produced");
    }
}
