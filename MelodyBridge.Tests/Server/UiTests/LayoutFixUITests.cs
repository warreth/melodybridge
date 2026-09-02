using AngleSharp.Dom;
using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Layout;
using MelodyBridge.Server.Components.Pages;
using MelodyBridge.Server.Components.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The UI fixes from the layout pass: split quality picker,
/// library scoped to scanned files, dashboard tiles and connection
/// rows, and absolute sidebar links.
/// </summary>
[TestFixture]
[Category("UI")]
public class LayoutFixUITests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-fix-{Guid.NewGuid():N}.db");
        var factory = new TestSqliteFactory(_dbPath);
        using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-1",
                Name = "Techno Mix",
                SourceUrl = "https://example.com/pl",
                PreferredFormat = "opus",
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "m-play",
                        Title = "Playlist song",
                        Artist = "DJ",
                        Position = 0,
                        DownloadStatus = "downloaded",
                        CurrentPath = "/music/techno/dj.mp3",
                    },
                },
            });
            db.Tracks.Add(new TrackEntity
            {
                MelodyId = "m-lib",
                Title = "Scanner song",
                Artist = "Band",
                CurrentPath = "/own-music/band.flac",
                MediaType = "flac",
                Bitrate = 900,
                SampleRateHz = 44100,
                FileSizeBytes = 30_000_000,
            });
            // Skip the welcome intro so the dashboard itself renders.
            db.DownloaderSettings.Add(new DownloaderSettingEntity { Key = "intro_dismissed", Value = "true" });
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
        _ctx.Services.AddSingleton<MelodyBridge.Infrastructure.MediaServers.IJellyfinSettings>(
            new MelodyBridge.Infrastructure.MediaServers.ConfigJellyfinSettings(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));
        _ctx.Services.AddSingleton<ILibraryScanner>(new Moq.Mock<ILibraryScanner>().Object);
        _ctx.Services.AddSingleton(new MelodyBridge.Server.Services.DevPanelService());
        _ctx.Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        var collector = new MelodyBridge.Server.Services.LogCollector();
        _ctx.Services.AddSingleton<MelodyBridge.Core.Logging.ILogCollector>(collector);
        _ctx.Services.AddSingleton(new MelodyBridge.Server.Services.LogExporter(collector));
        // Settings injects the user directory; the default mock is enough.
        _ctx.Services.AddSingleton(new Moq.Mock<MelodyBridge.Core.IJellyfinUserDirectory>().Object);
        // Loose mode: the dashboard calls JS helpers (tour, spotlight,
        // file download) that are irrelevant to these assertions.
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

    // ── Quality picker ─────────────────────────────────────────────

    [Test]
    public void QualityPicker_SplitsContainerAndBitrate()
    {
        var cut = _ctx.Render<QualityPicker>(p => p.Add(p => p.Value, "mp3:320"));

        var selects = cut.FindAll("select");
        Assert.That(selects.Count, Is.EqualTo(3),
            "container, floor and ceiling are separate dropdowns");

        var container = selects[0];
        Assert.That(container.QuerySelector("option:checked")!.GetAttribute("value"), Is.EqualTo("mp3"));

        var ceiling = selects[2];
        Assert.That(ceiling.QuerySelector("option:checked")!.GetAttribute("value"), Is.EqualTo("320"));
    }

    [Test]
    public void QualityPicker_OffersBitrates_ForEveryCappedContainer()
    {
        foreach (var (container, expected) in new[]
                 {
                     ("mp3", "320"), ("opus", "256"), ("aac", "320"),
                 })
        {
            var cut = _ctx.Render<QualityPicker>(p => p.Add(p => p.Value, container));
            var bitrate = cut.FindAll("select")[1];
            Assert.That(bitrate.OuterHtml, Does.Contain($"value=\"{expected}\""),
                $"{container} must offer a {expected} kbps cap");
            Assert.That(bitrate.HasAttribute("disabled"), Is.False,
                $"{container} must let the user pick a bitrate");
        }
    }

    [Test]
    public void QualityPicker_LosslessContainers_HaveNoBitrateChoice()
    {
        foreach (var container in new[] { "auto", "flac" })
        {
            var cut = _ctx.Render<QualityPicker>(p => p.Add(p => p.Value, container));
            var bitrate = cut.FindAll("select")[1];
            Assert.That(bitrate.HasAttribute("disabled"), Is.True,
                $"{container} has no meaningful bitrate cap, the select locks");
        }
    }

    [Test]
    public void QualityPicker_ChangingContainer_EmitsCombinedValue()
    {
        string? emitted = null;
        var cut = _ctx.Render<QualityPicker>(p => p
            .Add(p => p.Value, "mp3:320")
            .Add(p => p.ValueChanged, v => emitted = v));

        cut.FindAll("select")[0].Change("opus");
        Assert.That(emitted, Is.EqualTo("opus"),
            "switching container resets the band and emits the bare format");

        cut.FindAll("select")[2].Change("160");
        Assert.That(emitted, Is.EqualTo("opus:160"),
            "picking a ceiling emits the combined value the store persists");
    }

    [Test]
    public void QualityPicker_ShowsGuidance_PerContainer()
    {
        var flac = _ctx.Render<QualityPicker>(p => p.Add(p => p.Value, "flac"));
        Assert.That(flac.Markup, Does.Contain("lossless"),
            "FLAC guidance mentions it is lossless");

        var opus = _ctx.Render<QualityPicker>(p => p.Add(p => p.Value, "opus"));
        Assert.That(opus.Markup, Does.Contain("128 kbps"),
            "Opus guidance names a sensible default");
    }

    [Test]
    public void SettingsQualityTab_UsesSplitPicker()
    {
        var cut = _ctx.Render<Settings>();
        cut.FindAll("button.tab-link")
            .Single(b => b.TextContent.Trim() == "Quality").Click();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Bitrate floor")), TimeSpan.FromSeconds(3));
        Assert.That(cut.Markup, Does.Contain("Container"));
        Assert.That(cut.Markup, Does.Contain("Bitrate ceiling"));
    }

    // ── Playlists page cards ───────────────────────────────────────

    [Test]
    public void PlaylistCard_OnlyCoverAndTitleNavigate()
    {
        var cut = _ctx.Render<Playlists>();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Techno Mix")), TimeSpan.FromSeconds(3));

        var card = cut.Find(".playlist-card");
        Assert.That(card.TagName, Is.EqualTo("DIV"),
            "the card itself is a plain div, not one giant link");

        var links = card.QuerySelectorAll("a").Select(a => a.GetAttribute("href")).ToList();
        Assert.That(links, Is.EqualTo(new[] { "/playlists/pl-1", "/playlists/pl-1" }),
            "only the cover and the title link to the playlist");

        Assert.That(cut.Markup, Does.Not.Contain(">CSV<"),
            "the CSV export lives on the playlist page, not on the cards");
    }

    // ── Dashboard ──────────────────────────────────────────────────

    [Test]
    public void Dashboard_ConnectionRows_UseDedicatedLayout()
    {
        var cut = _ctx.Render<Home>();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Connections")), TimeSpan.FromSeconds(3));

        var rows = cut.FindAll(".connection-row");
        Assert.That(rows.Count, Is.EqualTo(4),
            "Spotify, YouTube, Jellyfin and FlareSolverr each get their own row");
        foreach (var row in rows)
        {
            var status = row.QuerySelector(".pill");
            Assert.That(status, Is.Not.Null,
                "every connection row carries its status pill");
        }
    }

    [Test]
    public void Dashboard_PlaylistTiles_AreSmallLinkedCards()
    {
        var cut = _ctx.Render<Home>();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Techno Mix")), TimeSpan.FromSeconds(3));

        var tile = cut.Find(".playlist-grid .playlist-card");
        Assert.That(tile.TagName, Is.EqualTo("DIV"));
        Assert.That(tile.QuerySelectorAll("a").Select(a => a.GetAttribute("href")),
            Is.EqualTo(new[] { "/playlists/pl-1", "/playlists/pl-1" }),
            "dashboard tiles link via cover and title only");
    }

    // ── Sidebar ────────────────────────────────────────────────────

    [Test]
    public void NavMenu_LinksAreRooted()
    {
        var nav = _ctx.Render<NavMenu>();

        var hrefs = nav.FindAll(".nav-link").Select(l => l.GetAttribute("href")).ToList();
        // The dashboard link uses the empty string for the app root;
        // every other link must be rooted.
        foreach (var href in hrefs.Where(h => !string.IsNullOrEmpty(h)))
        {
            Assert.That(href!.StartsWith('/'), Is.True,
                $"nav links must be rooted so they resolve on subpages too, got {href}");
        }
    }
}
