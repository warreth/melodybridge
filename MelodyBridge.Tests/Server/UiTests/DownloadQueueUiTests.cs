using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// PlaylistDetails with a live download run: pending tracks show their
/// position in the visible queue ("downloading" / "in queue · #N") and the
/// progress panel grows an ETA line. Real SQLite, the real coordinator from
/// the DI container, a downloader slow enough to keep the run alive. The
/// page polls on a 1s timer, so the test polls the rendered markup the same
/// way instead of bUnit's render-driven WaitForAssertion.
/// </summary>
[TestFixture]
[Category("UI")]
public class DownloadQueueUiTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;
    private string _dir = null!;

    /// <summary>Downloader slow enough that the run outlives the test.</summary>
    private sealed class SlowDownloader : IDownloader
    {
        public string Id => "slow-ui";
        public string Name => "Slow (test)";
        public string Description => "";
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(string artist, string title, DownloadQuality quality, CancellationToken ct = default)
            => Task.FromResult(new DownloaderSearchHit(title, artist, "https://slow.example/" + title, TimeSpan.FromSeconds(1)));

        public async Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            try { await Task.Delay(1500, ct); } catch (TaskCanceledException) { }
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, melodyId + ".mp3");
            await File.WriteAllTextAsync(path, "x", ct);
            return new DownloaderDownloadResult(true, path, null);
        }
    }

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-q-{Guid.NewGuid():N}.db");
        _dir = Path.Combine(Path.GetTempPath(), $"mb-q-{Guid.NewGuid():N}");
        var factory = new TestSqliteFactory(_dbPath);
        using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-queue",
                Name = "Queue UI test",
                SourceUrl = "https://example.com/pl",
                TargetDirectory = _dir,
                Tracks = new List<TrackEntity>
                {
                    new() { MelodyId = "q1", Title = "Alpha", Artist = "Artist", Position = 0, DownloadStatus = "pending" },
                    new() { MelodyId = "q2", Title = "Beta", Artist = "Artist", Position = 1, DownloadStatus = "pending" },
                    new() { MelodyId = "q3", Title = "Gamma", Artist = "Artist", Position = 2, DownloadStatus = "pending" },
                },
            });
            db.SaveChanges();
        }
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);
        _ctx.Services.AddDownloadPages(factory, new SlowDownloader());
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(factory, NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
        _ctx.Services.AddSingleton(tokenStore);
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider>.Instance));
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider>.Instance));
    }

    [TearDown]
    public void TearDown()
    {
        _ctx.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
        }
    }

    /// <summary>Starts the download run through the page button.</summary>
    private IRenderedComponent<PlaylistDetails> RenderWithRun()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-queue"));
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Alpha")), TimeSpan.FromSeconds(5));
        cut.FindAll("button").First(b => b.TextContent.Contains("Download missing")).Click();
        return cut;
    }

    /// <summary>Polls the rendered markup until the condition holds.</summary>
    private static async Task<bool> WaitMarkupAsync(Func<string, bool> condition, TimeSpan timeout, Func<string> markup)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition(markup())) return true;
            await Task.Delay(250);
        }
        return condition(markup());
    }

    [Test]
    public async Task TrackTable_ShowsQueuePosition_ForPendingTracks()
    {
        var cut = RenderWithRun();

        // The claimed head shows "downloading"; the rest show their position
        // ("in queue · #2" / "#3"). Track titles are deliberately free of
        // those substrings so the check cannot match a title by accident.
        var ok = await WaitMarkupAsync(
            m => m.Contains("downloading") && m.Contains("in queue · #"),
            TimeSpan.FromSeconds(10), () => cut.Markup);

        Assert.That(ok, Is.True, "mid-run the live queue must be visible: " +
            "the head downloads and the tail shows its position");
    }

    [Test]
    public async Task ProgressPanel_ShowsEta_OnceMeasured()
    {
        var cut = RenderWithRun();

        // First track done → pace measured → the ETA line appears in the
        // live progress panel.
        var ok = await WaitMarkupAsync(
            m => m.Contains("ETA ") && m.Contains("in queue"),
            TimeSpan.FromSeconds(10), () => cut.Markup);

        Assert.That(ok, Is.True,
            "once at least one track completed, the panel must show the ETA");
    }
}
