using TestContext = Bunit.TestContext;
using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Core.Logging;
using MelodyBridge.Tests;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Pages;
using MelodyBridge.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The home page has two faces. A fresh install gets the intro with a live
/// checklist and a skip button; once setup is complete (or skipped) the
/// page becomes the dashboard with stats, connections, errors, sync runs
/// and playlist cards. These tests drive both with a real database.
/// </summary>
[TestFixture]
[Category("UI")]
public class HomePageTests
{
    private TestContext _ctx = null!;
    private Mock<IDownloaderRegistry> _providerRegistry = null!;
    private LogCollector _collector = null!;
    private ILogCollector? _collectorForPage;
    private ScriptedDirectory _jellyfinDirectory = null!;
    private ScriptedDirectory _plexDirectory = null!;
    private ScriptedDirectory _subsonicDirectory = null!;
    private InMemFactory _dbFactory = null!;
    private DataChangeTracker _changes = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _ctx.Services.AddPluginAuthMonitor();
        // The dashboard's guided tour calls melody.spotlight; loose mode
        // lets JS interop run without scripted handlers.
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _providerRegistry = new Mock<IDownloaderRegistry>();
        _collector = new LogCollector(maxEntries: 500);
        _collectorForPage = null;

        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"HomeTest_{Guid.NewGuid()}")
            .Options;
        _dbFactory = new InMemFactory(options);
        _changes = new DataChangeTracker();
        var dbFactory = _dbFactory;

        using (var db = dbFactory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity { Id = "p1", Name = "Techno", SourceUrl = "s:1", TrackCount = 101 });
            db.Tracks.Add(new TrackEntity { Id = 1, Title = "Downloaded", DownloadStatus = "downloaded", CurrentPath = "/x/1.mp3" });
            db.SyncJobs.Add(new SyncJobEntity { Id = "j1", Name = "Test Job", LastRunStatus = "Completed" });
            db.SaveChanges();
        }

        _providerRegistry.Setup(r => r.GetAll()).Returns(new List<IDownloader>());
        _providerRegistry.Setup(r => r.IsEnabled(It.IsAny<string>())).Returns(true);

        _ctx.Services.AddSingleton<IDownloaderRegistry>(_providerRegistry.Object);
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(dbFactory);
        _ctx.Services.AddSingleton(_changes);
        var settingsStore = new SettingsStore(dbFactory);
        _ctx.Services.AddSingleton(settingsStore);
        _ctx.Services.AddSingleton(new MediaServerProfileStore(settingsStore));
        _ctx.Services.AddSingleton<ILogCollector>(_ => _collectorForPage ?? _collector);
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(
            dbFactory,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
        _ctx.Services.AddSingleton(tokenStore);
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider(
            tokenStore,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider>.Instance));
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider(
            tokenStore,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider>.Instance));
        _ctx.Services.AddSingleton<MelodyBridge.Infrastructure.MediaServers.IJellyfinSettings>(
            new MelodyBridge.Infrastructure.MediaServers.ConfigJellyfinSettings(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));
        // Plex/Navidrome connection cards read these; defaults mean "not configured".
        _ctx.Services.AddSingleton<MelodyBridge.Infrastructure.MediaServers.IPlexSettings>(
            new TestServices.FixedPlexSettings());
        _ctx.Services.AddSingleton<MelodyBridge.Infrastructure.MediaServers.INavidromeSettings>(
            new TestServices.FixedNavidromeSettings());
        _ctx.Services.AddSingleton<IMediaServerStatusProbe>(sp =>
            new MelodyBridge.Infrastructure.MediaServers.MediaServerStatusProbe(
                sp.GetRequiredService<MediaServerProfileStore>(),
                sp.GetRequiredService<MelodyBridge.Infrastructure.MediaServers.IJellyfinSettings>(),
                sp.GetRequiredService<MelodyBridge.Infrastructure.MediaServers.IPlexSettings>(),
                sp.GetRequiredService<MelodyBridge.Infrastructure.MediaServers.INavidromeSettings>(),
                sp.GetRequiredService<IMediaServerDirectoryFactory>(),
                Microsoft.Extensions.Logging.Abstractions
                    .NullLogger<MelodyBridge.Infrastructure.MediaServers.MediaServerStatusProbe>.Instance));
        _jellyfinDirectory = new ScriptedDirectory("Jellyfin");
        _plexDirectory = new ScriptedDirectory("Plex");
        _subsonicDirectory = new ScriptedDirectory("Subsonic");
        _ctx.Services.AddSingleton<IMediaServerDirectoryFactory>(
            new MelodyBridge.Infrastructure.MediaServers.MediaServerDirectoryFactory(
                new IMediaServerDirectory[] { _jellyfinDirectory, _plexDirectory, _subsonicDirectory }));
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    // ── Intro ──────────────────────────────────────────────────────

    [Test]
    public void FreshInstall_ShowsIntroWithChecklistAndSkip()
    {
        // Fresh install: no playlists, downloads or jobs anywhere.
        var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            db.Playlists.RemoveRange(db.Playlists);
            db.Tracks.RemoveRange(db.Tracks);
            db.SyncJobs.RemoveRange(db.SyncJobs);
            db.SaveChanges();
        }

        var cut = _ctx.Render<Home>();

        Assert.That(cut.Markup, Does.Contain("Get everything running"),
            "the intro headline must appear for a fresh install");
        Assert.That(cut.Markup, Does.Contain("Add a playlist"));
        Assert.That(cut.Markup, Does.Contain("Download the music"));
        Assert.That(cut.Markup, Does.Contain("Publish it"));
        Assert.That(cut.Markup, Does.Contain("Skip checklist"),
            "the checklist must be skippable, it is optional");
    }

    [Test]
    public void Intro_Skipped_WhenDatabaseAlreadyHasData()
    {
        // Setup seeds a playlist, a downloaded track and a job: the intro
        // would render fully done, so it must never appear at all.
        var cut = _ctx.Render<Home>();

        cut.WaitForState(() => cut.Markup.Contains("Dashboard"), TimeSpan.FromSeconds(3));
        Assert.That(cut.Markup, Does.Not.Contain("Get everything running"),
            "a completed setup goes straight to the dashboard");
    }

    [Test]
    public async Task Intro_SelfDestruct_PersistsDismissal()
    {
        var cut = _ctx.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Dashboard"), TimeSpan.FromSeconds(3));

        var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        Assert.That(await db.DownloaderSettings.AnyAsync(s => s.Key == "intro_dismissed"),
            Is.True, "the dismissal must persist so the intro never returns");
    }

    [Test]
    public async Task SkipButton_HidesIntro_AndPersists()
    {
        // Empty database: steps are not done, the intro stays until skipped.
        var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            db.Playlists.RemoveRange(db.Playlists);
            db.Tracks.RemoveRange(db.Tracks);
            db.SyncJobs.RemoveRange(db.SyncJobs);
            db.SaveChanges();
        }

        var cut = _ctx.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Skip checklist"), TimeSpan.FromSeconds(3));

        cut.FindAll("button").First(b => b.TextContent.Contains("Skip checklist")).Click();

        cut.WaitForState(() => cut.Markup.Contains("Dashboard"), TimeSpan.FromSeconds(3));
        Assert.That(cut.Markup, Does.Not.Contain("Get everything running"),
            "skipping must replace the intro with the dashboard immediately");

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            Assert.That(await db.DownloaderSettings.AnyAsync(s => s.Key == "intro_dismissed"),
                Is.True, "the skip must be remembered across restarts");
        }
    }

    [Test]
    public void FreshInstall_ShowsTourFirst_ThenTheChecklist()
    {
        var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            db.Playlists.RemoveRange(db.Playlists);
            db.Tracks.RemoveRange(db.Tracks);
            db.SyncJobs.RemoveRange(db.SyncJobs);
            db.SaveChanges();
        }

        var cut = _ctx.Render<Home>();
        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Tour 1 of 6"),
                "the tab tour runs before the checklist");
            Assert.That(cut.Markup, Does.Contain("Get everything running"),
                "the checklist sits behind the tour, ready when it ends");
        }, TimeSpan.FromSeconds(3));

        for (var i = 0; i < 5; i++)
            cut.Find(".tour-card .btn-modern.primary").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Next: the setup checklist"),
                "the last stop hands off to the dashboard checklist");
            Assert.That(cut.Markup, Does.Contain("Tour 6 of 6"));
        }, TimeSpan.FromSeconds(3));

        cut.Find(".tour-card .btn-modern.primary").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Not.Contain("tour-card"),
                "Done closes the tour");
            Assert.That(cut.Markup, Does.Contain("Get everything running"),
                "the checklist is now the next thing on screen");
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task DismissedFlag_ShowsDashboard_Directly()
    {
        var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity { Key = "intro_dismissed", Value = "true" });
            db.SaveChanges();
        }

        var cut = _ctx.Render<Home>();

        cut.WaitForState(() => cut.Markup.Contains("Dashboard"), TimeSpan.FromSeconds(3));
        Assert.That(cut.Markup, Does.Not.Contain("Get everything running"),
            "a dismissed intro must never come back");
    }

    [Test]
    public async Task DismissedIntro_WithNoPlaylists_OffersTheSetupGuide()
    {
        var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            db.Playlists.RemoveRange(db.Playlists);
            db.Tracks.RemoveRange(db.Tracks);
            db.SyncJobs.RemoveRange(db.SyncJobs);
            db.DownloaderSettings.Add(new DownloaderSettingEntity
                { Key = "intro_dismissed", Value = "true" });
            db.SaveChanges();
        }

        var cut = _ctx.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Start with a playlist"), TimeSpan.FromSeconds(3));
        Assert.That(cut.Markup, Does.Contain("Open the setup guide"));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Open the setup guide")).Click();

        cut.WaitForState(() => cut.Markup.Contains("Get everything running"), TimeSpan.FromSeconds(3));
        Assert.That(cut.Markup, Does.Contain("Download the music"),
            "the guide returns to the first incomplete step");
    }

    // ── Dashboard ──────────────────────────────────────────────────

    [Test]
    public async Task Dashboard_ShowsStatsConnectionsRunsAndPlaylistCards()
    {
        var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity { Key = "intro_dismissed", Value = "true" });
            db.SyncJobRuns.Add(new SyncJobRunEntity
            {
                SyncJobId = "j1", Timestamp = DateTime.UtcNow, Status = "Completed",
                Message = "Synced 5/5 tracks",
            });
            db.SaveChanges();
        }
        _collector.Log(LogLevel.Error, "MelodyBridge.Application.Services.DownloadManager", "plugin exploded");

        var cut = _ctx.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Recent sync runs"), TimeSpan.FromSeconds(3));

        Assert.That(cut.Markup, Does.Contain("Connections"), "the connection panel must exist");
        Assert.That(cut.Markup, Does.Contain("Spotify"), "every provider must be listed");
        Assert.That(cut.Markup, Does.Contain("Jellyfin"));
        Assert.That(cut.Markup, Does.Contain("FlareSolverr"));

        Assert.That(cut.Markup, Does.Contain("plugin exploded"),
            "recent errors must surface on the dashboard");
        Assert.That(cut.Markup, Does.Contain("Test Job"),
            "the latest sync run must show the job name");
        Assert.That(cut.Markup, Does.Contain("Synced 5/5 tracks"));

        Assert.That(cut.Markup, Does.Contain("Techno"),
            "the playlist card must render");
        Assert.That(cut.Markup, Does.Contain("playlist-card"),
            "cards reuse the playlist styling");
    }

    [Test]
    public async Task Dashboard_IdleTick_DoesNotQueryAndVersionBumpReloads()
    {
        var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity { Key = "intro_dismissed", Value = "true" });
            db.SaveChanges();
        }

        var cut = _ctx.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Recent sync runs"), TimeSpan.FromSeconds(3));
        var contextsAfterLoad = _dbFactory.Contexts;

        await cut.InvokeAsync(() => cut.Instance.RefreshStats());

        Assert.That(_dbFactory.Contexts, Is.EqualTo(contextsAfterLoad),
            "an idle tick must not re-run the stored statistics");

        using (var db = dbFactory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "p-new", Name = "New arrival", SourceUrl = "s:new", TrackCount = 3,
            });
            db.SaveChanges();
        }
        _changes.Bump();

        await cut.InvokeAsync(() => cut.Instance.RefreshStats());
        cut.Render();

        Assert.That(_dbFactory.Contexts, Is.GreaterThan(contextsAfterLoad),
            "a store change must make the next tick re-read");
        Assert.That(cut.Markup, Does.Contain("New arrival"));
    }

    [Test]
    public async Task Dashboard_LogLineWrittenDuringTheRead_TriggersAnotherTick()
    {
        var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity { Key = "intro_dismissed", Value = "true" });
            db.SaveChanges();
        }

        var collector = new SnapshotThenWriteCollector();
        _collectorForPage = collector;
        var cut = _ctx.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Recent sync runs"), TimeSpan.FromSeconds(3));

        // A line arrives after the load, and the next read writes another
        // one while it is taking its snapshot.
        collector.Log(LogLevel.Info, "Test", "before the tick");
        collector.WriteOnNextSnapshot = true;

        await cut.InvokeAsync(() => cut.Instance.RefreshStats());
        var contextsAfterFirstTick = _dbFactory.Contexts;

        await cut.InvokeAsync(() => cut.Instance.RefreshStats());

        Assert.That(_dbFactory.Contexts, Is.GreaterThan(contextsAfterFirstTick),
            "a line that landed during the read must make the next tick re-read");
    }

    [Test]
    public async Task Dashboard_ConnectionRows_NameTheAccountAndNeverGuessReady()
    {
        var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity { Key = "intro_dismissed", Value = "true" });
            db.SaveChanges();
        }
        var profiles = _ctx.Services.GetRequiredService<MediaServerProfileStore>();
        await profiles.SaveAsync(new MediaServerProfile
        {
            Id = "jf-1", Name = "Jellyfin Homelab", Kind = "Jellyfin",
            BaseUrl = "http://jellyfin:8096", ApiKey = "key", Username = "mila",
        });
        await profiles.SaveAsync(new MediaServerProfile
        {
            Id = "plex-1", Name = "Plex", Kind = "Plex",
            BaseUrl = "http://plex:32400", ApiKey = "token",
        });
        _jellyfinDirectory.Identity = new MediaServerIdentity("Basement", "mila");
        _plexDirectory.Answer = false;

        var cut = _ctx.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Recent sync runs"), TimeSpan.FromSeconds(3));

        // The connection rows show the last answer and redraw themselves
        // when the probe has one, so the rows are read again here.
        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll(".connection-row");
            var jellyfin = rows.Single(r => r.TextContent.Contains("Jellyfin Homelab"));
            var plex = rows.Single(r => r.TextContent.Contains("Plex"));
            var subsonic = rows.Single(r => r.TextContent.Contains("Subsonic"));

            Assert.Multiple(() =>
            {
                Assert.That(jellyfin.TextContent, Does.Contain("mila on Basement"),
                    "a connected server names the account and the server");
                Assert.That(jellyfin.TextContent, Does.Contain("connected"));
                Assert.That(plex.TextContent, Does.Contain("unreachable"),
                    "a stored profile that does not answer is not ready");
                Assert.That(subsonic.TextContent, Does.Contain("not configured"));
                Assert.That(subsonic.TextContent, Does.Contain("off"));
            });
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Dashboard_ShowsTheServerWhileTheProbeIsStillRunning()
    {
        var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity { Key = "intro_dismissed", Value = "true" });
            db.SaveChanges();
        }
        var profiles = _ctx.Services.GetRequiredService<MediaServerProfileStore>();
        await profiles.SaveAsync(new MediaServerProfile
        {
            Id = "jf-slow", Name = "Jellyfin Homelab", Kind = "Jellyfin",
            BaseUrl = "http://jellyfin:8096", ApiKey = "key",
        });
        // The server only answers when the gate is released: a dashboard
        // that waited for it would never render.
        var gate = new TaskCompletionSource();
        _jellyfinDirectory.Gate = gate;

        var cut = _ctx.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Recent sync runs"), TimeSpan.FromSeconds(3));

        var rows = cut.FindAll(".connection-row");
        var jellyfin = rows.Single(r => r.TextContent.Contains("Jellyfin Homelab"));

        Assert.Multiple(() =>
        {
            Assert.That(jellyfin.TextContent, Does.Contain("checking"),
                "a server that has not answered yet is checking, not ready");
            Assert.That(jellyfin.TextContent, Does.Not.Contain("connected"),
                "nothing is claimed before the server answers");
        });

        gate.SetResult();
    }

    [Test]
    public void Dashboard_PluginLoginCard_ListsAProblem()
    {
        var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity { Key = "intro_dismissed", Value = "true" });
            db.SaveChanges();
        }
        var monitor = _ctx.Services.GetRequiredService<MelodyBridge.Infrastructure.Services.PluginAuthMonitor>();
        monitor.Report("qobuz", "Qobuz", "The Qobuz token was rejected");

        var cut = _ctx.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Plugin logins"), TimeSpan.FromSeconds(3));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("The Qobuz token was rejected"));
            var fix = cut.FindAll("a").Single(link => link.TextContent.Trim() == "Fix");
            Assert.That(fix.GetAttribute("href"), Is.EqualTo("/plugins#plugin-qobuz"));
        });
    }

    [Test]
    public void FreshInstall_ShowsTheLosslessSourceCard()
    {
        var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            db.Playlists.RemoveRange(db.Playlists);
            db.Tracks.RemoveRange(db.Tracks);
            db.SyncJobs.RemoveRange(db.SyncJobs);
            db.DownloaderSettings.RemoveRange(db.DownloaderSettings);
            db.SaveChanges();
        }

        var cut = _ctx.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Connect a lossless source"), TimeSpan.FromSeconds(3));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Qobuz"));
            Assert.That(cut.Markup, Does.Contain("Deezer"));
            Assert.That(cut.Markup, Does.Contain("Tidal"));
            Assert.That(cut.Markup, Does.Contain("No account needed"));
            Assert.That(cut.FindAll("a[href='/plugins#plugin-qobuz']").Count, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task Dashboard_FlareSolverrAuto_ShowsEnabledRow()
    {
        var previous = MelodyBridge.Infrastructure.Cloudflare.FlareSolverrSolver.Url;
        MelodyBridge.Infrastructure.Cloudflare.FlareSolverrSolver.Url = "auto";
        try
        {
            var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
            using (var db = dbFactory.CreateDbContext())
            {
                db.DownloaderSettings.Add(new DownloaderSettingEntity { Key = "intro_dismissed", Value = "true" });
                db.SaveChanges();
            }

            var cut = _ctx.Render<Home>();
            cut.WaitForState(() => cut.Markup.Contains("Recent sync runs"), TimeSpan.FromSeconds(3));

            var row = cut.FindAll(".connection-row").Single(r => r.TextContent.Contains("FlareSolverr"));
            Assert.That(row.ClassList, Does.Contain("enabled"),
                "auto counts as configured, so the row is in its enabled state");
            var pill = row.QuerySelector(".pill");
            Assert.That(pill!.TextContent, Is.EqualTo("on"),
                "the pill shows on while the solver is configured");
            Assert.That(row.TextContent, Does.Contain("auto: looks for the container"),
                "auto gets a plain-language label instead of the raw value");
        }
        finally
        {
            // The solver URL is process-wide: restore it for the other tests.
            MelodyBridge.Infrastructure.Cloudflare.FlareSolverrSolver.Url = previous;
        }
    }

    [Test]
    public async Task Dashboard_EmptyDatabase_ShowsEmptyStates()
    {
        var dbFactory = _ctx.Services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        using (var db = dbFactory.CreateDbContext())
        {
            db.Playlists.RemoveRange(db.Playlists);
            db.Tracks.RemoveRange(db.Tracks);
            db.SyncJobs.RemoveRange(db.SyncJobs);
            db.DownloaderSettings.Add(new DownloaderSettingEntity { Key = "intro_dismissed", Value = "true" });
            db.SaveChanges();
        }

        var cut = _ctx.Render<Home>();
        cut.WaitForState(() => cut.Markup.Contains("Dashboard"), TimeSpan.FromSeconds(3));

        Assert.That(cut.Markup, Does.Contain("No playlists yet"),
            "the playlist panel must explain what to do first");
        Assert.That(cut.Markup, Does.Contain("No errors"),
            "a clean log stream must say so instead of an empty hole");
    }

    private sealed class ScriptedDirectory : IMediaServerDirectory
    {
        public ScriptedDirectory(string kind) => Kind = kind;

        public string Kind { get; }
        public bool Answer { get; set; } = true;
        public MediaServerIdentity? Identity { get; set; }
        /// <summary>A server that only answers when this is released.</summary>
        public TaskCompletionSource? Gate { get; set; }

        public async Task<bool> TestConnectionAsync(
            MediaServerConnection connection, CancellationToken ct = default)
        {
            if (Gate is not null) await Gate.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            return Answer;
        }

        public Task<MediaServerIdentity?> IdentifyAsync(
            MediaServerConnection connection, CancellationToken ct = default)
            => Task.FromResult(Identity);

        public Task<List<MediaServerUserOption>> GetUsersAsync(
            MediaServerConnection connection, CancellationToken ct = default)
            => Task.FromResult(new List<MediaServerUserOption>());
    }

    private class InMemFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InMemFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public int Contexts { get; private set; }

        public MelodyBridgeDbContext CreateDbContext()
        {
            Contexts++;
            return new MelodyBridgeDbContext(_options);
        }
    }
}
