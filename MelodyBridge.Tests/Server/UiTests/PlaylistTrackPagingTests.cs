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
/// A long playlist renders one page of rows, not its whole snapshot. The
/// test seeds 250 real tracks in a real SQLite file; the assertions read the
/// rendered table, then click the pager, then sort and check that sorting
/// still sees every row rather than only the page that was sent first.
/// </summary>
[TestFixture]
[Category("UI")]
public class PlaylistTrackPagingTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _ctx.Services.AddPluginAuthMonitor();
        _ctx.Services.AddSingleton<IDownloaderRegistry>(new EmptyRegistry());
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-paging-{Guid.NewGuid():N}.db");
        var factory = new TestSqliteFactory(_dbPath);
        using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-large",
                Name = "Large playlist",
                SourceUrl = "https://example.com/large",
                TargetDirectory = "/tmp",
                Tracks = Enumerable.Range(0, 250).Select(i => new TrackEntity
                {
                    MelodyId = $"m{i}",
                    // Reverse order on purpose: if paging happens before the
                    // sort, an ascending title sort would still start at 150.
                    Title = $"Track {249 - i:D3}",
                    Artist = "Artist",
                    Position = i,
                    DownloadStatus = "pending",
                }).ToList(),
            });
            db.SaveChanges();
        }
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);
        _ctx.Services.AddDownloadPages(factory);
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(
            factory, NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
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
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
        }
    }

    private IRenderedComponent<PlaylistDetails> RenderPage()
        => _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-large"));

    private static int DataRows(IRenderedComponent<PlaylistDetails> cut)
        => cut.FindAll("table tbody tr").Count;

    private static string FirstTitle(IRenderedComponent<PlaylistDetails> cut)
        => cut.FindAll("table tbody tr").First().QuerySelector("td strong")!.TextContent;

    [Test]
    public void LongPlaylist_StartsAtOnePage_AndShowsMoreOnRequest()
    {
        var cut = RenderPage();

        cut.WaitForAssertion(() =>
            Assert.That(DataRows(cut), Is.EqualTo(100), "the first page is a screenful of rows"));

        Assert.That(cut.Markup, Does.Contain("Showing 100 of 250 tracks"));
        Assert.That(cut.Markup, Does.Contain("Show 100 more"));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Show 100 more")).Click();

        cut.WaitForAssertion(() =>
            Assert.That(DataRows(cut), Is.EqualTo(200)));
        Assert.That(cut.Markup, Does.Contain("Showing 200 of 250 tracks"));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Show all").Click();

        cut.WaitForAssertion(() =>
            Assert.That(DataRows(cut), Is.EqualTo(250)));
        Assert.That(cut.Markup, Does.Not.Contain("Showing 250 of 250 tracks"),
            "the pager disappears when every row is visible");
    }

    [Test]
    public void Sorting_AppliesToTheWholePlaylist_NotOnlyTheFirstPage()
    {
        var cut = RenderPage();

        cut.WaitForAssertion(() => Assert.That(DataRows(cut), Is.EqualTo(100)));
        Assert.That(FirstTitle(cut), Is.EqualTo("Track 249"), "the unsorted page starts at position 1");

        cut.FindAll("button.th-sort").Single(b => b.TextContent.Contains("Title")).Click();

        cut.WaitForAssertion(() =>
            Assert.That(FirstTitle(cut), Is.EqualTo("Track 000"),
                "sorting considers all 250 tracks, then the table shows the first page"));
        Assert.That(DataRows(cut), Is.EqualTo(100));
    }
}
