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
/// PlaylistDetails page quality UI: the preset selector reflects the
/// stored quality string, real measured facts appear per track, and the
/// inflation warning pill shows for flagged files.
/// </summary>
[TestFixture]
[Category("UI")]
public class PlaylistDetailsQualityTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-q-{Guid.NewGuid():N}.db");
        var factory = new TestSqliteFactory(_dbPath);
        using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-1",
                Name = "Quality UI test",
                SourceUrl = "https://example.com/pl",
                TargetDirectory = "/tmp",
                PreferredFormat = "mp3:192",
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "m1",
                        Title = "Fine track",
                        Artist = "Artist",
                        Position = 0,
                        DownloadStatus = "downloaded",
                        Bitrate = 320,
                        SampleRateHz = 44100,
                        FileSizeBytes = 9_000_000,
                        MediaType = "mp3",
                        CurrentPath = "/tmp/fine-track.mp3",
                    },
                    new()
                    {
                        MelodyId = "m2",
                        Title = "Doubtful track",
                        Artist = "Artist",
                        Position = 1,
                        DownloadStatus = "downloaded",
                        Warning = "bitrate looks inflated: spectral ceiling 12.2 kHz: matches a 128 kbps source",
                    },
                },
            });
            db.SaveChanges();
        }
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);
        _ctx.Services.AddDownloadPages(factory);
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
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
        }
    }

    [Test]
    public void QualitySelector_ShowsStoredPreset()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-1"));

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Audio quality")), TimeSpan.FromSeconds(3));
        var select = cut.Find("select");
        Assert.That(select.OuterHtml, Does.Contain("mp3:192"),
            "the stored cap preset must be present");
    }

    [Test]
    public void TrackTable_ShowsWarningPill_ForFlaggedTrack()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-1"));

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Doubtful track")), TimeSpan.FromSeconds(3));

        var warned = cut.FindAll("span.pill.warn");
        Assert.That(warned.Count, Is.EqualTo(1),
            "exactly the flagged track shows the warning pill");
        Assert.That(warned[0].GetAttribute("title"), Does.Contain("inflated"),
            "hovering the pill explains the doubt");
    }

    [Test]
    public void TrackTable_ShowsRealQuality_ForDownloadedTrack()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-1"));

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Fine track")), TimeSpan.FromSeconds(3));

        Assert.That(cut.Markup, Does.Contain("320 kbps"),
            "the measured bitrate must appear next to the track");
        Assert.That(cut.Markup, Does.Contain("44.1 kHz"),
            "the real sample rate must appear next to the track");
    }

    [Test]
    public void TrackTable_ShowsInflatedSummaryHint()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-1"));

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Fine track")), TimeSpan.FromSeconds(3));

        Assert.That(cut.Markup, Does.Contain("look inflated"),
            "the playlist summary explains how to fix inflated files");
    }

    [Test]
    public void DownloadButtons_ReachTheCoordinator()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-1"));

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Download missing")), TimeSpan.FromSeconds(3));

        var coordinator = _ctx.Services.GetRequiredService<Application.Services.DownloadCoordinator>();
        var buttons = cut.FindAll("button").Where(b => b.TextContent.Contains("Download missing"));
        Assert.That(buttons.Count(), Is.EqualTo(1));
        buttons.First().Click();

        cut.WaitForAssertion(() =>
            Assert.That(coordinator.IsActive("pl-1"), Is.True),
            TimeSpan.FromSeconds(3));
    }
}
