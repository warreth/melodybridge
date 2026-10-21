using MelodyBridge.Application.Services;

namespace MelodyBridge.Tests.ApplicationServices;

/// <summary>
/// The download counters on the playlists page. The store stamp is read
/// before the counters, so a write that lands while they are being read
/// sends the next tick back to the store instead of leaving the page on
/// numbers that were read before that write.
/// </summary>
[TestFixture]
public class DownloadStatsRefresherTests
{
    private static Dictionary<string, (int downloaded, int failed, int total, long sizeBytes, int attention)> Empty()
        => new();

    [Test]
    public async Task AWriteDuringTheReadSendsTheNextTickBackToTheStore()
    {
        var version = 7L;
        var refresher = new DownloadStatsRefresher(
            () =>
            {
                // A download writes its status while the counters are
                // being read: the stamp moves past the one the page read.
                version++;
                return Task.FromResult(Empty());
            },
            () => version);

        await refresher.RefreshAsync();

        Assert.Multiple(() =>
        {
            Assert.That(refresher.ObservedVersion, Is.EqualTo(7),
                "the stamp is the one from before the read");
            Assert.That(refresher.ShouldReload(active: false), Is.True,
                "the write during the read is not lost: the next tick reloads");
        });
    }

    [Test]
    public async Task AnIdlePageDoesNotReload()
    {
        var version = 3L;
        var reads = 0;
        var refresher = new DownloadStatsRefresher(
            () =>
            {
                reads++;
                return Task.FromResult(Empty());
            },
            () => version);

        await refresher.RefreshAsync();
        Assert.That(reads, Is.EqualTo(1));

        Assert.Multiple(() =>
        {
            Assert.That(refresher.ShouldReload(active: false), Is.False,
                "nothing moved, so the counters stay as they are");
            Assert.That(refresher.ShouldReload(active: true), Is.True,
                "a live run always refreshes");
        });

        version++;
        Assert.That(refresher.ShouldReload(active: false), Is.True,
            "a write elsewhere in the app refreshes the counters");
    }
}
