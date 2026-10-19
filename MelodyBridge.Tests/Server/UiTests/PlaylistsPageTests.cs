using AngleSharp.Dom;
using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server.UiTests;

[TestFixture]
[Category("UI")]
public class PlaylistsPageTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-ui-{Guid.NewGuid():N}.db");
        var factory = new SqliteFactory(_dbPath);
        using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);
        _ctx.Services.AddDownloadPages(factory);
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(factory, Microsoft.Extensions.Logging.Abstractions.NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
        _ctx.Services.AddSingleton(tokenStore);
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider(
            tokenStore, Microsoft.Extensions.Logging.Abstractions.NullLogger<
                MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider>.Instance));
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider(
            tokenStore, Microsoft.Extensions.Logging.Abstractions.NullLogger<
                MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider>.Instance));
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

    [Test]
    public void Playlists_EmptyDb_ShowsEmptyState()
    {
        var cut = _ctx.Render<Playlists>();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("No playlists yet")), TimeSpan.FromSeconds(3));
        Assert.That(cut.Markup, Does.Contain("Add playlist"));
    }

    [Test]
    public void Playlists_WithSavedPlaylist_ShowsCardWithMetadata()
    {
        SeedOnePlaylist("Test Mix", 12, Platform.Spotify);

        var cut = _ctx.Render<Playlists>();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Test Mix"));
            Assert.That(cut.Markup, Does.Contain("12 tracks"));
            Assert.That(cut.Markup, Does.Contain("Spotify"));
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Playlists_WithSavedPlaylist_CardLinksToDetails()
    {
        SeedOnePlaylist("Another Mix", 3, Platform.Spotify);

        var cut = _ctx.Render<Playlists>();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Another Mix")), TimeSpan.FromSeconds(3));
        var card = cut.Find(".playlist-card");
        Assert.That(card.TagName, Is.EqualTo("DIV"),
            "the card is a plain div now");
        var links = card.QuerySelectorAll("a").Select(a => a.GetAttribute("href")).ToList();
        Assert.That(links, Has.All.Contains("playlists/"),
            "the cover and the title link to the playlist page");
        Assert.That(links.Count, Is.EqualTo(2),
            "only the cover and the title are clickable");
    }

    [Test]
    public void Playlists_CoverWithMosaicUrl_RendersSquareImgInCoverLink()
    {
        SeedOnePlaylist("Mosaic Mix", 9, Platform.Spotify, coverUrl: "https://mosaic.scdn.co/640/abc", owner: "Warre");

        var cut = _ctx.Render<Playlists>();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Mosaic Mix")), TimeSpan.FromSeconds(3));
        var cover = cut.Find(".playlist-card a.playlist-cover");
        var img = cover.QuerySelector("img");
        Assert.That(img, Is.Not.Null,
            "a playlist with a cover url shows the image");
        Assert.That(img!.GetAttribute("src"), Is.EqualTo("https://mosaic.scdn.co/640/abc"),
            "the Spotify 2x2 mosaic url is used verbatim");
        Assert.That(img.GetAttribute("alt"), Is.EqualTo("Mosaic Mix"),
            "the image names the playlist for screen readers");
        Assert.That(cover.QuerySelector(".cover-badge"), Is.Null,
            "a playlist with no run and no downloads shows no badge over the mosaic");
    }

    [Test]
    public void Playlists_TrackCountAndOwner_RenderAsSeparateBadges()
    {
        SeedOnePlaylist("Badge Mix", 89, Platform.Spotify, owner: "Warre");

        var cut = _ctx.Render<Playlists>();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Badge Mix")), TimeSpan.FromSeconds(3));
        var tags = cut.FindAll(".playlist-card .card-tags .quality-badge")
            .Select(b => b.TextContent.Trim()).ToList();
        Assert.That(tags, Has.Member("89 tracks"),
            "the track count is its own badge");
        Assert.That(tags, Has.Member("by Warre"),
            "the owner is its own badge so a CSS gap can keep them apart");
        Assert.That(string.Join("|", tags), Does.Not.Contain("tracksby"),
            "the count and the owner must never merge into one string");
    }

    [Test]
    public void Playlists_CardMenu_SitsInHeaderWithActions()
    {
        SeedOnePlaylist("Menu Mix", 5, Platform.Spotify);

        var cut = _ctx.Render<Playlists>();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Menu Mix")), TimeSpan.FromSeconds(3));
        var header = cut.Find(".playlist-card .panel-header");
        Assert.That(header.QuerySelector("h2 a")!.GetAttribute("href"), Does.Contain("playlists/"),
            "the title link stays in the header");
        Assert.That(header.QuerySelector("[class*=menu], [class*=Menu]"), Is.Not.Null,
            "the (...) action menu lives in the header next to the title");
    }

    [Test]
    public void Playlists_ImportModal_OpensOnAccounts_WithTheFilesTabNextToIt()
    {
        var cut = _ctx.Render<Playlists>();
        cut.Find("div.topbar-actions button.ghost").Click();

        Assert.That(cut.Markup, Does.Contain("Import your music"));
        Assert.That(cut.FindAll("button[data-tab]").Select(b => b.TextContent.Trim()),
            Is.EqualTo(new[] { "Accounts", "Files" }),
            "the modal splits accounts from files instead of stacking everything");
        // No account is connected in the test context, so the account
        // route shows its connect hint instead of its import buttons.
        Assert.That(cut.Markup, Does.Contain("Connect it in Settings"), "account route");

        cut.Find("button[data-tab='files']").Click();
        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Exportify CSV"), "file route 1");
            Assert.That(cut.Markup, Does.Contain("Spotify data export"), "file route 2");
        });
    }

    [Test]
    public void Playlists_ImportModal_ExplainsPremiumRequirement()
    {
        var cut = _ctx.Render<Playlists>();
        cut.Find("div.topbar-actions button.ghost").Click();

        Assert.That(cut.Markup, Does.Contain("Spotify Premium"),
            "the account route must say it needs Premium");
        Assert.That(cut.Markup, Does.Contain("works with a free account"),
            "the file routes must say they work without Premium");

        cut.Find("button[data-tab='files']").Click();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("exportify.net"),
            "the recommended tool must be linked"));
    }

    [Test]
    public void Playlists_ImportModal_FileRoutesAcceptZips()
    {
        var cut = _ctx.Render<Playlists>();
        cut.Find("div.topbar-actions button.ghost").Click();
        cut.Find("button[data-tab='files']").Click();

        cut.WaitForAssertion(() =>
        {
            var accepts = cut.FindAll("input[type=file]")
                .Select(i => i.GetAttribute("accept") ?? string.Empty)
                .ToList();
            Assert.That(accepts, Has.Count.EqualTo(2));
            Assert.That(accepts.All(a => a.Contains(".zip", StringComparison.Ordinal)), Is.True,
                "both file routes take the archive the exporters deliver");
            Assert.That(cut.Markup, Does.Contain("archives are read entry by entry"));
        });
    }

    [Test]
    public void Playlists_ImportModal_TakeoutRow_SaysManual_AndNamesTheFile()
    {
        var cut = _ctx.Render<Playlists>();
        cut.Find("div.topbar-actions button.ghost").Click();
        cut.Find("button[data-tab='files']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("manual"),
                "the Spotify data export must be labelled as manual");
            Assert.That(cut.Markup, Does.Contain("YourLibrary.json"),
                "must name the file the user should upload");
        });
    }

    [Test]
    public void Playlists_ImportModal_RecommendsExportifyForFreeUsers()
    {
        var cut = _ctx.Render<Playlists>();
        cut.Find("div.topbar-actions button.ghost").Click();
        cut.Find("button[data-tab='files']").Click();

        cut.WaitForAssertion(() =>
        {
            var recommended = cut.FindAll("span.pill.ok")
                .Any(p => p.TextContent.Trim() == "recommended");
            Assert.That(recommended, Is.True,
                "the Exportify row carries the recommended pill");
        });
    }

    [Test]
    public void Playlists_AddForm_ScheduleOptions_MatchTheSyncJobWordedSelect()
    {
        var cut = _ctx.Render<Playlists>();
        cut.Find("div.topbar-actions button.primary").Click();

        // Same six options the Library schedule picker and the sync job
        // wizard offer: a named select, not a bare checkbox.
        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Auto-sync schedule"));
            foreach (var option in new[] { "Manual", "Hourly", "Daily", "Weekly", "Monthly", "Cron" })
                Assert.That(cut.Markup, Does.Contain($">{option}</option>"),
                    $"the schedule select offers {option}");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Playlists_AddForm_OffersStrictQuality_OnByDefault()
    {
        var cut = _ctx.Render<Playlists>();
        cut.Find("div.topbar-actions button.primary").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Strict quality"),
                "the wizard lets the quality be strict");
            Assert.That(cut.Markup, Does.Contain("only the exact quality above is accepted"),
                "the question mark explains what strict means");
            Assert.That(cut.Markup, Does.Not.Contain("Allow container changes"),
                "the container choice appears only when strict is off");
        }, TimeSpan.FromSeconds(3));

        var toggle = cut.FindAll("input[type=checkbox]")
            .First(i => i.ParentElement!.TextContent.Contains("Strict quality"));
        Assert.That(toggle.HasAttribute("checked"), Is.True,
            "strict is the default for a new playlist");

        toggle.Change(false);
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Allow container changes"),
                "turning strict off reveals the container-change choice"),
            TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Playlists_Card_ShowsSizeOnDiskAndSyncSchedule()
    {
        SeedOnePlaylist("Sized Mix", 2, Platform.Spotify);

        // Give the two tracks real disk facts: one finished file.
        var factory = new SqliteFactory(_dbPath);
        using (var db = factory.CreateDbContext())
        {
            var tracks = db.Tracks.ToList();
            tracks[0].DownloadStatus = "downloaded";
            tracks[0].FileSizeBytes = 5 * 1024 * 1024;
            db.SaveChanges();
        }

        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("5 MB on disk"),
                "the card adds up the finished files and shows the size");
            Assert.That(cut.Markup, Does.Contain("· manual"),
                "the card states the sync schedule like the sync jobs do");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Playlists_ImportModal_IsWideEnoughForThePlaylistsTable()
    {
        var cut = _ctx.Render<Playlists>();
        cut.Find("div.topbar-actions button.ghost").Click();

        Assert.That(cut.Find(".wizard-modal").ClassList, Does.Contain("wide"),
            "the import modal carries the table, so it gets the wide variant");
    }

    [Test]
    public void Playlists_PublishWizard_CreatesOneNamedJobPerTickedPlaylist()
    {
        SeedOnePlaylist("Jazz Classics", 3, Platform.Spotify);
        SeedProfile("Home Jellyfin");

        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Jazz Classics")), TimeSpan.FromSeconds(3));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Publish")).Click();
        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Send playlists to your servers"));
            Assert.That(cut.Markup, Does.Contain("Sync jobs page"),
                "the dialog says where the full options live");
        }, TimeSpan.FromSeconds(3));

        cut.FindAll(".publish-option").Single(o => o.TextContent.Contains("Jazz Classics"))
            .QuerySelector("input")!.Change(true);
        cut.FindAll(".publish-option").Single(o => o.TextContent.Contains("Home Jellyfin"))
            .QuerySelector("input")!.Change(true);
        cut.FindAll("button").Single(b => b.TextContent.Contains("Create 1 sync job")).Click();

        cut.WaitForAssertion(() =>
        {
            using var db = new SqliteFactory(_dbPath).CreateDbContext();
            var job = db.SyncJobs.Single();
            Assert.That(job.Name, Is.EqualTo("Jazz Classics"),
                "the job takes the playlist name");
            Assert.That(job.Schedule, Is.EqualTo("Manual"),
                "the quick path never guesses a schedule");
            var targets = MelodyBridge.Infrastructure.Services.SyncJobMapping.TargetsOf(job);
            Assert.That(targets.Select(t => t.Kind),
                Is.EqualTo(new[] { OutputTargetType.JellyfinApi }));
        }, TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("Created the sync job 'Jazz Classics'"));
        Assert.That(cut.Markup, Does.Contain("Open Sync jobs"),
            "the result points at the full sync jobs page");
    }

    [Test]
    public void Playlists_PublishWizard_HidesPlaylistsThatAlreadyHaveAJob()
    {
        SeedOnePlaylist("Already Published", 1, Platform.Spotify);
        SeedOnePlaylist("Fresh Mix", 1, Platform.Spotify);
        using (var db = new SqliteFactory(_dbPath).CreateDbContext())
        {
            var taken = db.Playlists.Single(p => p.Name == "Already Published");
            db.SyncJobs.Add(new SyncJobEntity { Name = "Already Published", SourceId = taken.Id });
            db.SaveChanges();
        }
        SeedProfile("Home Jellyfin");

        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Fresh Mix")), TimeSpan.FromSeconds(3));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Publish")).Click();

        cut.WaitForAssertion(() =>
        {
            var list = cut.FindAll(".publish-list").First().TextContent;
            Assert.That(list, Does.Contain("Fresh Mix"));
            Assert.That(list, Does.Not.Contain("Already Published"),
                "a playlist with a job is never offered a second one");
            Assert.That(cut.Markup, Does.Contain("already has a sync job"),
                "the dialog says why a playlist is missing");
        }, TimeSpan.FromSeconds(3));
    }

    private void SeedProfile(string name)
    {
        using var db = new SqliteFactory(_dbPath).CreateDbContext();
        db.DownloaderSettings.Add(new DownloaderSettingEntity
        {
            Key = "media_server_profiles",
            Value = "[{\"Id\":\"px1\",\"Name\":\"" + name
                + "\",\"BaseUrl\":\"http://jellyfin:8096\",\"ApiKey\":\"key\",\"Kind\":\"Jellyfin\"}]",
        });
        db.SaveChanges();
    }

    private void SeedOnePlaylist(string name, int trackCount, Platform platform,
        string? coverUrl = null, string? owner = null)
    {
        var factory = new SqliteFactory(_dbPath);
        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
        var playlist = new PlaylistEntity
        {
            Id = Guid.NewGuid().ToString(),
            Name = name,
            SourcePlatform = platform,
            TrackCount = trackCount,
            LastSyncStatus = SyncStatus.Completed,
            CoverImageUrl = coverUrl,
            Owner = owner,
        };
        for (var i = 0; i < trackCount; i++)
        {
            playlist.Tracks.Add(new TrackEntity
            {
                MelodyId = $"mb-{i}",
                Title = $"Track {i}",
                Artist = "Artist",
                Position = i,
                DownloadStatus = "pending",
            });
        }
        db.Playlists.Add(playlist);
        db.SaveChanges();
    }

    private sealed class SqliteFactory(string dbPath) : IDbContextFactory<MelodyBridgeDbContext>
    {
        public MelodyBridgeDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
                .UseSqlite($"Data Source={dbPath};Pooling=False")
                .Options;
            return new MelodyBridgeDbContext(options);
        }

        public Task<MelodyBridgeDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class EmptyRegistry : IDownloaderRegistry
    {
        public IReadOnlyList<IDownloader> GetAll() => Array.Empty<IDownloader>();
        public IDownloader? Get(string id) => null;
        public IReadOnlyList<IDownloader> GetEnabled() => Array.Empty<IDownloader>();
        public Task SetEnabledAsync(string id, bool enabled) => Task.CompletedTask;
        public bool IsEnabled(string id) => false;
        public Task<int> GetPriorityAsync(string id, CancellationToken ct = default) => Task.FromResult(0);
        public Task SetPriorityAsync(string id, int priority, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
    public Task<string> GetConfigAsync(string id, string key, CancellationToken ct = default) => Task.FromResult("");
    public Task SetConfigAsync(string id, string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }
    [Test]
    public void Playlists_RowsToggle_RendersCompactRows_AndPersists()
    {
        SeedOnePlaylist("Test Mix", 12, Platform.Spotify);
        var cut = _ctx.Render<Playlists>();

        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".panel-card"), Is.Not.Empty), TimeSpan.FromSeconds(5));

        cut.FindAll(".view-chip").Single(b => b.TextContent.Contains("Rows")).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.FindAll(".entity-row"), Has.Count.EqualTo(1),
                "the compact row replaces the card");
            Assert.That(cut.FindAll(".panel-card"), Is.Empty);
        }, TimeSpan.FromSeconds(5));

        using var db = new SqliteFactory(_dbPath).CreateDbContext();
        Assert.That(db.DownloaderSettings.Any(s => s.Key == "playlists_view" && s.Value == "rows"),
            Is.True, "the choice sticks for the next visit");
    }

}
