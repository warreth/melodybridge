using AngleSharp.Dom;
using Bunit;
using MelodyBridge.Application.Services;
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
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
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
        Assert.That(cut.Markup, Does.Contain("works on a free account"),
            "the unofficial route must say it works without Premium");

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
    public void Playlists_AddForm_InvalidCron_BlocksSavingAndSaysWhy()
    {
        var cut = _ctx.Render<Playlists>();
        cut.Find("div.topbar-actions button.primary").Click();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Auto-sync schedule")), TimeSpan.FromSeconds(3));

        cut.Find("input[placeholder^='https://open.spotify.com']")
            .Change("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M");
        var schedule = cut.FindAll("select")
            .Single(s => s.QuerySelectorAll("option").Any(o => o.TextContent == "Cron"));
        schedule.Change("Cron");
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll("input[placeholder='30 4 * * *']"), Has.Count.EqualTo(1)),
            TimeSpan.FromSeconds(3));
        cut.Find("input[placeholder='30 4 * * *']").Input("not a cron");

        cut.WaitForAssertion(() =>
        {
            var save = cut.FindAll(".wizard-footer button")
                .Single(b => b.TextContent.Contains("Fetch & save"));
            Assert.That(save.HasAttribute("disabled"), Is.True,
                "invalid cron text blocks the create form instead of saving the previous schedule");
            Assert.That(cut.Markup, Does.Contain("Enter a valid five-field cron expression"),
                "the form says why saving is blocked");
        }, TimeSpan.FromSeconds(3));

        // bUnit dispatches the click even though the button is disabled, so
        // this reaches Add() with a valid URL and proves the guard itself.
        cut.FindAll(".wizard-footer button")
            .Single(b => b.TextContent.Contains("Fetch & save")).Click();
        cut.WaitForAssertion(() =>
        {
            var error = cut.FindAll(".wizard-footer .wizard-error");
            Assert.That(error, Has.Count.EqualTo(1),
                "a save attempt with an invalid schedule reports the guard");
            Assert.That(error[0].TextContent, Does.Contain("valid five-field cron expression"),
                "the guard names the schedule problem");
        }, TimeSpan.FromSeconds(3));

        using (var db = new SqliteFactory(_dbPath).CreateDbContext())
            Assert.That(db.Playlists.Count(), Is.Zero,
                "nothing was saved with a stale schedule even when Add() is reached");

        cut.Find("input[placeholder='30 4 * * *']").Input("5 4 * * *");
        cut.WaitForAssertion(() =>
        {
            var save = cut.FindAll(".wizard-footer button")
                .Single(b => b.TextContent.Contains("Fetch & save"));
            Assert.That(save.HasAttribute("disabled"), Is.False,
                "a valid expression enables the create form again");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Playlists_AddForm_OffersStrictQuality_OnByDefault()
    {
        var cut = _ctx.Render<Playlists>();
        cut.Find("div.topbar-actions button.primary").Click();

        // No filter has no bitrate gate, so the strict controls only appear
        // once a real quality preset is chosen.
        cut.FindAll("select").First(s => s.TextContent.Contains("No filter"))
            .Change("preset:saver");
        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Strict quality"),
                "the wizard lets the selected quality be strict");
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
            Assert.That(cut.Markup, Does.Contain("Open a job afterwards"),
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
    public async Task Playlists_PublishWizard_UsesTheSelectedRuleProfile()
    {
        SeedOnePlaylist("Jazz Classics", 3, Platform.Spotify);
        SeedProfile("Home Jellyfin");
        var rules = new MelodyBridge.Infrastructure.Services.SyncRuleProfileStore(
            new SqliteFactory(_dbPath));
        await rules.SaveAsync(new MelodyBridge.Core.SyncRuleProfile
        {
            Id = "rules-1",
            Name = "Living room",
            M3uOutputFolder = "/srv/playlists",
            M3uFileNamePattern = "{playlist}.m3u8",
            M3uCrlf = true,
            M3uBom = true,
        });

        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Jazz Classics")), TimeSpan.FromSeconds(3));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Publish")).Click();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".publish-option"), Is.Not.Empty), TimeSpan.FromSeconds(3));

        cut.FindAll(".publish-option")
            .Single(o => o.TextContent.Contains("Jazz Classics"))
            .QuerySelector("input")!.Change(true);
        cut.FindAll(".publish-option")
            .Single(o => o.TextContent.Contains("M3U file"))
            .QuerySelector("input")!.Change(true);

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("M3U rule profile"));
            Assert.That(cut.Find("select[aria-label='Rule profile for quick publish']")
                .GetAttribute("value"), Is.EqualTo("rules-1"),
                "the first profile is the ready default, not a hidden folder");
            Assert.That(cut.Markup, Does.Contain("Living room"));
            Assert.That(cut.Markup, Does.Contain("One file per playlist through"),
                "the M3U row names the selected profile");
        }, TimeSpan.FromSeconds(3));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Create 1 sync job")).Click();

        cut.WaitForAssertion(() =>
        {
            using var db = new SqliteFactory(_dbPath).CreateDbContext();
            var job = db.SyncJobs.Single();
            Assert.That(job.RuleProfileId, Is.EqualTo("rules-1"),
                "the created job follows the chosen profile");
            var file = MelodyBridge.Infrastructure.Services.SyncJobMapping
                .TargetsOf(job).Single(t => t.Kind == OutputTargetType.M3uFile);
            Assert.That(file.M3uPath,
                Is.EqualTo(Path.Combine("/srv/playlists", "Jazz Classics.m3u8")),
                "the profile's folder and pattern name the file");
        }, TimeSpan.FromSeconds(5));

        Assert.That(cut.Markup, Does.Contain("M3U files from 'Living room'"));
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

    private sealed class StubSpotifyAccountProvider : MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider
    {
        private readonly IReadOnlyList<UserPlaylist> _playlists;

        public StubSpotifyAccountProvider(
            MelodyBridge.Infrastructure.Accounts.AccountTokenStore tokens,
            IReadOnlyList<UserPlaylist> playlists)
            : base(tokens, NullLogger<MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider>.Instance)
        {
            _playlists = playlists;
        }

        public override Task<bool> IsConnectedAsync(CancellationToken ct = default)
            => Task.FromResult(true);

        public override Task<IReadOnlyList<UserPlaylist>> GetUserPlaylistsAsync(CancellationToken ct = default)
            => Task.FromResult(_playlists);
    }

    private sealed class StubSourceProvider : ISourceProvider
    {
        public string Name => "Stub";
        public Platform Platform => Platform.Spotify;

        public bool CanHandle(string sourceIdentifier)
            => sourceIdentifier.StartsWith("https://open.spotify.com/playlist/", StringComparison.Ordinal);

        public Task<Playlist> GetPlaylistAsync(string sourceIdentifier)
        {
            if (sourceIdentifier.EndsWith("/bad", StringComparison.Ordinal)
                || sourceIdentifier.EndsWith("/bad-one", StringComparison.Ordinal)
                || sourceIdentifier.EndsWith("/bad-two", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("source unavailable");
            }
            if (!sourceIdentifier.EndsWith("/good", StringComparison.Ordinal))
                throw new InvalidOperationException($"unexpected source url: {sourceIdentifier}");

            return Task.FromResult(new Playlist
            {
                Id = "good",
                Name = "Good Mix",
                Tracks = new List<Track>
                {
                    new()
                    {
                        Title = "One",
                        Artist = "Artist",
                        SongID = new SongID(Platform.Spotify, "good-1"),
                        PlatformSongID = new SongID(Platform.Spotify, "good-1"),
                    },
                },
            });
        }
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

    [Test]
    public void Playlists_CardSize_ChangesTheGrid_AndPersists()
    {
        SeedOnePlaylist("Size Mix", 3, Platform.Spotify);
        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".playlist-card"), Has.Count.EqualTo(1)), TimeSpan.FromSeconds(5));

        Assert.That(cut.Find(".playlist-cards").ClassList, Does.Contain("cards-medium"),
            "the default is the medium grid");
        cut.Find("select[aria-label='Playlist card size']").Change("large");

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Find(".playlist-cards").ClassList, Does.Contain("cards-large"),
                "the grid class changes with the picker");
            Assert.That(cut.Find(".playlist-cards").ClassList, Does.Not.Contain("cards-medium"));
        }, TimeSpan.FromSeconds(3));

        using var db = new SqliteFactory(_dbPath).CreateDbContext();
        Assert.That(db.DownloaderSettings.Any(s => s.Key == "playlists_card_size" && s.Value == "large"),
            Is.True, "the card size sticks for the next visit");
    }

    [Test]
    public void Playlists_SortByName_ReordersCards_AndPersists()
    {
        SeedOnePlaylist("Zulu Mix", 2, Platform.Spotify);
        SeedOnePlaylist("Alpha Mix", 5, Platform.Spotify);
        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".playlist-card"), Has.Count.EqualTo(2)), TimeSpan.FromSeconds(5));

        cut.Find(".sort-control select").Change("name");

        cut.WaitForAssertion(() =>
        {
            var names = cut.FindAll(".playlist-card h2").Select(h => h.TextContent.Trim()).ToList();
            Assert.That(names, Is.EqualTo(new[] { "Alpha Mix", "Zulu Mix" }),
                "the name order wins over the stored recency order");
        }, TimeSpan.FromSeconds(3));

        using var db = new SqliteFactory(_dbPath).CreateDbContext();
        Assert.That(db.DownloaderSettings.Any(s => s.Key == "playlists_sort" && s.Value == "name"),
            Is.True, "the chosen sort sticks for the next visit");
    }

    [Test]
    public void Playlists_CardMenu_OffersEverydayActions()
    {
        SeedOnePlaylist("Menu Mix", 4, Platform.Spotify);
        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Menu Mix")), TimeSpan.FromSeconds(3));

        cut.Find("button.card-menu-trigger").Click();

        var labels = cut.FindAll(".card-menu-items button")
            .Select(b => b.TextContent.Trim()).ToList();
        Assert.That(labels, Is.EqualTo(new[]
        {
            "Pin to top", "Refresh", "Download missing", "Export CSV", "Remove",
        }), "the menu carries the pin and the everyday actions, not only removal");
    }

    [Test]
    public void Playlists_CardMenuRemove_KeepsFocusInTheDialogAndHandsBackTheTrigger()
    {
        SeedOnePlaylist("Menu Mix", 4, Platform.Spotify);
        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Menu Mix")), TimeSpan.FromSeconds(3));

        cut.Find("button.card-menu-trigger").Click();
        cut.Find("button[title='Remove this playlist']").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Remove 'Menu Mix'?"),
                "the Remove item opens the confirm dialog");
            _ctx.JSInterop.VerifyFocusAsyncInvoke(1,
                "only the dialog focuses while it is open; the covered menu trigger must not take focus back");
            Assert.That(_ctx.JSInterop.Invocations["melody.rememberModalTrigger"].Count,
                Is.EqualTo(1),
                "the menu hands its trigger to the modal close path");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Playlists_CardMenuPlainAction_StillReturnsFocusToTheTrigger()
    {
        SeedOnePlaylist("Menu Mix", 4, Platform.Spotify);
        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Menu Mix")), TimeSpan.FromSeconds(3));

        cut.Find("button.card-menu-trigger").Click();
        cut.FindAll(".card-menu-items button")
            .Single(b => b.TextContent.Contains("Pin to top")).Click();

        cut.WaitForAssertion(() =>
        {
            _ctx.JSInterop.VerifyFocusAsyncInvoke(1,
                "a plain menu action returns focus to the trigger");
            Assert.That(_ctx.JSInterop.Invocations.Identifiers,
                Does.Not.Contain("melody.rememberModalTrigger"),
                "no modal handoff is needed for a plain action");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Playlists_SelectMode_ShowsBulkActionsAndSelectsCards()
    {
        SeedOnePlaylist("First Mix", 2, Platform.Spotify);
        SeedOnePlaylist("Second Mix", 3, Platform.Spotify);
        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".playlist-card"), Has.Count.EqualTo(2)), TimeSpan.FromSeconds(5));

        cut.FindAll(".list-toolbar button").Single(b => b.TextContent.Contains("Select")).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.FindAll(".selection-bar"), Has.Count.EqualTo(1));
            var labels = cut.FindAll(".selection-actions button")
                .Select(b => b.TextContent.Trim()).ToList();
            Assert.That(labels, Is.EqualTo(new[]
            {
                "Refresh", "Download missing", "Export CSV", "Remove", "Cancel",
            }), "the bulk bar offers the same everyday actions");
            Assert.That(cut.FindAll(".playlist-card .select-check"), Has.Count.EqualTo(2),
                "every card gets a selection mark");
            Assert.That(cut.Find(".selection-all input").GetAttribute("aria-label"),
                Is.EqualTo("Select all visible playlists"),
                "the select-all checkbox explains what it does");
        }, TimeSpan.FromSeconds(3));

        cut.FindAll(".playlist-card a.playlist-cover").First().Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.FindAll(".playlist-card.selected"), Has.Count.EqualTo(1),
                "clicking a cover selects the card instead of navigating");
            Assert.That(cut.FindAll(".playlist-card a.playlist-cover").First().GetAttribute("title"),
                Does.StartWith("Deselect ").Or.StartWith("Select "),
                "selection mode says what a click will do");
            Assert.That(cut.Find(".selection-all").TextContent, Does.Contain("1 selected"));
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Playlists_SelectMode_ExportsSeveralPlaylistsAsOneZip()
    {
        SeedOnePlaylist("First Mix", 2, Platform.Spotify);
        SeedOnePlaylist("Second Mix", 3, Platform.Spotify);
        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".playlist-card"), Has.Count.EqualTo(2)), TimeSpan.FromSeconds(5));

        cut.FindAll(".list-toolbar button").Single(b => b.TextContent.Contains("Select")).Click();
        cut.FindAll(".playlist-card a.playlist-cover")[0].Click();
        cut.FindAll(".playlist-card a.playlist-cover")[1].Click();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Find(".selection-all").TextContent, Does.Contain("2 selected")),
            TimeSpan.FromSeconds(3));

        cut.FindAll(".selection-actions button").Single(b => b.TextContent.Contains("Export CSV")).Click();

        cut.WaitForAssertion(() =>
        {
            var call = _ctx.JSInterop.Invocations["melody.downloadFile"].Single();
            Assert.That(call.Arguments[0], Is.EqualTo("playlists-csv.zip"),
                "a bulk export is one download, not a burst the browser may block");
            Assert.That(call.Arguments[1], Is.EqualTo("application/zip"));
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Playlists_PinAction_MovesThePlaylistToTheTopAndPersists()
    {
        SeedOnePlaylist("Alpha Mix", 2, Platform.Spotify);
        SeedOnePlaylist("Zulu Mix", 3, Platform.Spotify);
        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".playlist-card"), Has.Count.EqualTo(2)), TimeSpan.FromSeconds(5));

        var zulu = cut.FindAll(".playlist-card").Single(c => c.TextContent.Contains("Zulu Mix"));
        zulu.QuerySelector("button.card-menu-trigger")!.Click();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Pin to top")).Click();

        cut.WaitForAssertion(() =>
        {
            var names = cut.FindAll(".playlist-card h2").Select(h => h.TextContent.Trim()).ToList();
            Assert.That(names.First(), Is.EqualTo("Zulu Mix"),
                "a pinned playlist sorts above the chosen order");
            Assert.That(cut.FindAll(".playlist-card .pin-mark"), Has.Count.EqualTo(1),
                "the card shows why it is on top");
            Assert.That(cut.Markup, Does.Contain("Pinned:"),
                "the pin marker also has text for screen readers");
        }, TimeSpan.FromSeconds(3));

        using (var db = new SqliteFactory(_dbPath).CreateDbContext())
            Assert.That(db.Playlists.Single(p => p.Name == "Zulu Mix").IsPinned, Is.True);

        cut.FindAll(".playlist-card").First()
            .QuerySelector("button.card-menu-trigger")!.Click();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Unpin")).Click();
        cut.WaitForAssertion(() =>
        {
            var names = cut.FindAll(".playlist-card h2").Select(h => h.TextContent.Trim()).ToList();
            Assert.That(names.First(), Is.EqualTo("Alpha Mix"), "unpinning restores the normal order");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Playlists_NeedsAttention_FiltersToFailedSyncsAndTracks()
    {
        SeedOnePlaylist("Healthy Mix", 2, Platform.Spotify);
        SeedOnePlaylist("Failed Tracks", 3, Platform.Spotify);
        SeedOnePlaylist("Broken Sync", 1, Platform.Spotify);
        using (var db = new SqliteFactory(_dbPath).CreateDbContext())
        {
            var failedTrack = db.Tracks.First(t => t.PlaylistEntityId ==
                db.Playlists.Single(p => p.Name == "Failed Tracks").Id);
            failedTrack.DownloadStatus = "failed";
            db.Playlists.Single(p => p.Name == "Broken Sync").LastSyncStatus = SyncStatus.Failed;
            db.SaveChanges();
        }

        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".playlist-card"), Has.Count.EqualTo(3)), TimeSpan.FromSeconds(5));

        var filter = cut.Find(".filter-chip.attention");
        Assert.That(filter.TextContent, Does.Contain("2"),
            "the chip counts both attention playlists");
        Assert.That(filter.GetAttribute("aria-pressed"), Is.EqualTo("false"),
            "the filter exposes its toggle state");
        filter.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Find(".filter-chip.attention").GetAttribute("aria-pressed"),
                Is.EqualTo("true"), "the active filter says it is pressed");
            var names = cut.FindAll(".playlist-card h2").Select(h => h.TextContent.Trim()).ToList();
            Assert.That(names, Is.EquivalentTo(new[] { "Failed Tracks", "Broken Sync" }),
                "the filter hides the healthy playlist");
            Assert.That(cut.Find(".list-count").TextContent, Does.Contain("2 playlists"));
        }, TimeSpan.FromSeconds(3));

        cut.Find(".filter-chip.attention").Click();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".playlist-card"), Has.Count.EqualTo(3),
                "turning the filter off restores the full list"), TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Playlists_PublishWizard_RefusesAnInvalidCustomCron()
    {
        SeedOnePlaylist("Jazz Classics", 3, Platform.Spotify);
        SeedProfile("Home Jellyfin");
        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Jazz Classics")), TimeSpan.FromSeconds(3));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Publish")).Click();
        cut.WaitForAssertion(() => Assert.That(cut.FindAll(".publish-option"), Is.Not.Empty),
            TimeSpan.FromSeconds(3));

        cut.FindAll(".publish-option")
            .Single(o => o.TextContent.Contains("Jazz Classics"))
            .QuerySelector("input")!.Change(true);
        cut.FindAll(".publish-option")
            .Single(o => o.TextContent.Contains("Home Jellyfin"))
            .QuerySelector("input")!.Change(true);
        cut.Find(".publish-schedule select").Change("Cron");
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".publish-schedule input"), Has.Count.EqualTo(1)),
            TimeSpan.FromSeconds(3));
        cut.Find(".publish-schedule input").Input("not a cron");

        cut.WaitForAssertion(() =>
        {
            var create = cut.FindAll(".wizard-footer button")
                .Single(b => b.TextContent.Contains("Create 1 sync job"));
            Assert.That(create.HasAttribute("disabled"), Is.True,
                "an invalid custom cron blocks creation");
            Assert.That(cut.Markup, Does.Contain("Enter a valid five-field cron expression"));
        }, TimeSpan.FromSeconds(3));

        cut.Find(".publish-schedule input").Input("5 4 * * *");

        cut.WaitForAssertion(() =>
        {
            var create = cut.FindAll(".wizard-footer button")
                .Single(b => b.TextContent.Contains("Create 1 sync job"));
            Assert.That(create.HasAttribute("disabled"), Is.False,
                "a valid expression enables creation again");
        }, TimeSpan.FromSeconds(3));

        cut.FindAll(".wizard-footer button")
            .Single(b => b.TextContent.Contains("Create 1 sync job")).Click();

        cut.WaitForAssertion(() =>
        {
            using var db = new SqliteFactory(_dbPath).CreateDbContext();
            var job = db.SyncJobs.Single();
            Assert.That(job.Schedule, Is.EqualTo("Cron"));
            Assert.That(job.CronExpression, Is.EqualTo("5 4 * * *"));
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Playlists_NeedsAttention_DropsARecoveredPlaylistFromTheSelection()
    {
        SeedOnePlaylist("Recovering Mix", 2, Platform.Spotify);
        using (var db = new SqliteFactory(_dbPath).CreateDbContext())
        {
            db.Tracks.First(t => t.PlaylistEntityId == db.Playlists.Single().Id).DownloadStatus = "failed";
            db.SaveChanges();
        }

        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".playlist-card"), Has.Count.EqualTo(1)), TimeSpan.FromSeconds(5));

        cut.Find(".filter-chip.attention").Click();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".playlist-card"), Has.Count.EqualTo(1)), TimeSpan.FromSeconds(3));
        cut.FindAll(".list-toolbar button").Single(b => b.TextContent.Contains("Select")).Click();
        cut.FindAll(".playlist-card a.playlist-cover").Single().Click();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Find(".selection-all").TextContent, Does.Contain("1 selected")),
            TimeSpan.FromSeconds(3));

        // The fix resolves while selection mode is open. The next stats
        // reload, which the timer drives in the running app, must remove
        // the now hidden id instead of leaving a phantom bulk action.
        using (var db = new SqliteFactory(_dbPath).CreateDbContext())
        {
            db.Tracks.First(t => t.DownloadStatus == "failed").DownloadStatus = "downloaded";
            db.SaveChanges();
        }
        var load = typeof(Playlists).GetMethod("Load",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await cut.InvokeAsync(() => (Task)load.Invoke(cut.Instance, new object[] { false })!);
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Find(".selection-all").TextContent, Does.Contain("0 selected"),
                "the hidden playlist is no longer part of the selection");
            Assert.That(cut.Markup, Does.Contain("Nothing needs attention"));
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Playlists_AccountImport_ListsCheckboxesAndCanSelectAll()
    {
        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Add playlist")), TimeSpan.FromSeconds(3));
        cut.Find("div.topbar-actions button.ghost").Click();

        var list = new List<UserPlaylist>
        {
            new("acc-1", "Chill Pop", "Warre", 24, false),
            new("acc-2", "Techno", "Warre", 40, false),
        };
        typeof(Playlists).GetField("accountPlaylists",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(cut.Instance, list);
        cut.Render();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.FindAll(".account-playlist-table tbody tr"), Has.Count.EqualTo(2),
                "the account's playlists are listed once");
            Assert.That(cut.FindAll(".account-playlist-table tbody input[type=checkbox]"),
                Has.Count.EqualTo(2), "every row can be ticked");
            Assert.That(cut.FindAll(".account-list-toolbar button").Single().HasAttribute("disabled"),
                Is.True, "import stays disabled while nothing is selected");
        }, TimeSpan.FromSeconds(3));

        cut.Find(".account-list-toolbar .selection-all input").Change(true);

        cut.WaitForAssertion(() =>
        {
            var import = cut.FindAll(".account-list-toolbar button").Single();
            Assert.That(import.TextContent, Does.Contain("Import 2 selected"));
            Assert.That(import.HasAttribute("disabled"), Is.False);
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Playlists_AccountImport_BatchImportsGoodPlaylistsAndReportsFailures()
    {
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(
            new SqliteFactory(_dbPath), NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
        _ctx.Services.AddSingleton<MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider>(
            new StubSpotifyAccountProvider(tokenStore, new[]
            {
                new UserPlaylist("good", "Good Mix", "Warre", 1, false),
                new UserPlaylist("bad", "Bad Mix", "Warre", 1, false),
            }));

        var manager = new MelodyBridge.Application.Services.DownloadManager(
            new EmptyRegistry(),
            NullLogger<MelodyBridge.Application.Services.DownloadManager>.Instance);
        _ctx.Services.AddSingleton(new PlaylistStore(
            new SqliteFactory(_dbPath), new ISourceProvider[] { new StubSourceProvider() },
            manager, NullLogger<PlaylistStore>.Instance));

        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Add playlist")), TimeSpan.FromSeconds(3));
        cut.Find("div.topbar-actions button.ghost").Click();
        cut.WaitForAssertion(() =>
        {
            var card = cut.FindAll(".account-card")
                .First(c => c.QuerySelector("strong")?.TextContent.Trim() == "Spotify");
            card.QuerySelectorAll("button")
                .Single(b => b.TextContent.Contains("Show my playlists")).Click();
        }, TimeSpan.FromSeconds(3));

        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".account-playlist-table tbody tr"), Has.Count.EqualTo(2)),
            TimeSpan.FromSeconds(3));
        cut.Find(".account-list-toolbar .selection-all input").Change(true);
        cut.Find(".account-list-toolbar button").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.FindAll(".import-result"), Has.Count.EqualTo(1),
                "the successful playlist is listed in the result dialog");
            Assert.That(cut.Find(".import-result").TextContent, Does.Contain("Good Mix"));
            Assert.That(cut.Find(".import-result a").GetAttribute("href"), Does.StartWith("/playlists/"));
            Assert.That(cut.Markup, Does.Contain("Bad Mix: source unavailable"),
                "the failed playlist is named with its reason");
        }, TimeSpan.FromSeconds(5));

        using var db = new SqliteFactory(_dbPath).CreateDbContext();
        Assert.That(db.Playlists.Count(), Is.EqualTo(1), "only the successful import persists");
        Assert.That(db.Playlists.Single().Name, Is.EqualTo("Good Mix"));
    }

    [Test]
    public void Playlists_AccountImport_AllFailuresKeepTheImportPanelOpen()
    {
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(
            new SqliteFactory(_dbPath), NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
        _ctx.Services.AddSingleton<MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider>(
            new StubSpotifyAccountProvider(tokenStore, new[]
            {
                new UserPlaylist("bad-one", "Bad One", "Warre", 1, false),
                new UserPlaylist("bad-two", "Bad Two", "Warre", 1, false),
            }));

        var manager = new MelodyBridge.Application.Services.DownloadManager(
            new EmptyRegistry(),
            NullLogger<MelodyBridge.Application.Services.DownloadManager>.Instance);
        _ctx.Services.AddSingleton(new PlaylistStore(
            new SqliteFactory(_dbPath), new ISourceProvider[] { new StubSourceProvider() },
            manager, NullLogger<PlaylistStore>.Instance));

        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Add playlist")), TimeSpan.FromSeconds(3));
        cut.Find("div.topbar-actions button.ghost").Click();
        cut.WaitForAssertion(() =>
        {
            var card = cut.FindAll(".account-card")
                .First(c => c.QuerySelector("strong")?.TextContent.Trim() == "Spotify");
            card.QuerySelectorAll("button")
                .Single(b => b.TextContent.Contains("Show my playlists")).Click();
        }, TimeSpan.FromSeconds(3));
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".account-playlist-table tbody tr"), Has.Count.EqualTo(2)),
            TimeSpan.FromSeconds(3));
        cut.Find(".account-list-toolbar .selection-all input").Change(true);
        cut.Find(".account-list-toolbar button").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Import your music"),
                "the import panel stays open when every playlist fails");
            Assert.That(cut.Markup, Does.Contain("Bad One: source unavailable"));
            Assert.That(cut.FindAll(".import-result"), Is.Empty);
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Playlists_SelectMode_DoubleClickChecksPendingWorkOnce()
    {
        SeedOnePlaylist("First Mix", 2, Platform.Spotify);
        SeedOnePlaylist("Second Mix", 3, Platform.Spotify);
        var coordinator = new YieldingCoordinator();
        _ctx.Services.AddSingleton<IDownloadCoordinator>(coordinator);

        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".playlist-card"), Has.Count.EqualTo(2)), TimeSpan.FromSeconds(5));
        cut.FindAll(".list-toolbar button").Single(b => b.TextContent.Contains("Select")).Click();
        cut.FindAll(".playlist-card a.playlist-cover")[0].Click();
        cut.FindAll(".playlist-card a.playlist-cover")[1].Click();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Find(".selection-all").TextContent, Does.Contain("2 selected")),
            TimeSpan.FromSeconds(3));

        var download = cut.FindAll(".selection-actions button")
            .Single(b => b.TextContent.Contains("Download missing"));
        var firstClick = download.ClickAsync();
        await coordinator.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var secondClick = cut.FindAll(".selection-actions button")
            .Single(b => b.TextContent.Contains("Download missing")).ClickAsync();
        try
        {
            await secondClick.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.That(coordinator.PendingChecks, Is.EqualTo(1),
                "a double click while the first check is still running starts no second check");
        }
        finally
        {
            coordinator.Release.TrySetResult();
        }
        await firstClick;
    }

    [Test]
    public void AccountSourceUrl_UsesTheProviderPlatform()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Playlists.AccountSourceUrl(
                    MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider.ProviderName, "abc"),
                Is.EqualTo("https://open.spotify.com/playlist/abc"));
            Assert.That(Playlists.AccountSourceUrl(
                    MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider.ProviderName, "xyz"),
                Is.EqualTo("https://www.youtube.com/playlist?list=xyz"));
        });
    }

    [Test]
    public void Playlists_PublishWizard_OffersScheduleM3uAndAddProfile()
    {
        SeedOnePlaylist("Jazz Classics", 3, Platform.Spotify);
        SeedProfile("Home Jellyfin");

        var cut = _ctx.Render<Playlists>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Jazz Classics")), TimeSpan.FromSeconds(3));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Publish")).Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("M3U file"),
                "the quick publish dialog offers an M3U target");
            Assert.That(cut.Markup, Does.Contain("Schedule for every job"),
                "one schedule applies to every created job");
            Assert.That(cut.Find("a[href='/settings#media-servers']").TextContent,
                Does.Contain("Add profile"),
                "a missing profile can be created without hunting through Settings");
        }, TimeSpan.FromSeconds(3));

        cut.FindAll(".publish-option")
            .Single(o => o.TextContent.Contains("Jazz Classics"))
            .QuerySelector("input")!.Change(true);
        cut.FindAll(".publish-option")
            .Single(o => o.TextContent.Contains("M3U file"))
            .QuerySelector("input")!.Change(true);
        cut.Find(".publish-schedule select").Change("Weekly");
        cut.FindAll("button").Single(b => b.TextContent.Contains("Create 1 sync job")).Click();

        cut.WaitForAssertion(() =>
        {
            using var db = new SqliteFactory(_dbPath).CreateDbContext();
            var job = db.SyncJobs.Single();
            Assert.That(job.Schedule, Is.EqualTo("Weekly"),
                "the picked schedule applies to the created job");
            var targets = MelodyBridge.Infrastructure.Services.SyncJobMapping.TargetsOf(job);
            Assert.That(targets.Select(t => t.Kind), Is.EqualTo(new[] { OutputTargetType.M3uFile }),
                "M3U was the only ticked target");
            Assert.That(targets[0].M3uPath, Does.EndWith("Jazz Classics.m3u8"));
        }, TimeSpan.FromSeconds(5));
    }

    /// <summary>Holds the first pending-work check open so a second click
    /// arrives while the first is still in flight.</summary>
    private sealed class YieldingCoordinator : IDownloadCoordinator
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PendingChecks;

        public IReadOnlyList<DownloadRun> Snapshot() => Array.Empty<DownloadRun>();

        public DownloadRun? RunFor(string playlistId) => null;

        public bool IsActive(string playlistId) => false;

        public void Pause(string playlistId) { }

        public void Cancel(string playlistId) { }

        public async Task<bool> HasPendingWorkAsync(
            string playlistId, CancellationToken ct = default)
        {
            Interlocked.Increment(ref PendingChecks);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return false;
        }

        public void Start(string playlistId) { }
    }
}
