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

/// <summary>
/// The manual source picker on the playlist page, against a real SQLite
/// store and a stub plugin that writes a real MP3: the per-row button
/// opens the dialog, every candidate is listed with its plugin and
/// confidence, and picking one downloads it and refreshes the row. This
/// is the repair path for a wrong automatic match, so it must work on a
/// downloaded row too.
/// </summary>
[TestFixture]
[Category("UI")]
public class SourcePickerUiTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;
    private string _dir = null!;

    /// <summary>A plugin with one hit; downloads real MP3 bytes.</summary>
    private sealed class PickPlugin : IDownloader
    {
        public string Id => "pick";
        public string Name => "Pick plugin";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;
        public string? HitTitle { get; set; } = "The Real Song";
        public int DownloadCalls { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
        {
            if (HitTitle is null) return Task.FromResult<DownloaderSearchHit?>(null);
            return Task.FromResult<DownloaderSearchHit?>(
                new DownloaderSearchHit(HitTitle, "The Artist", "https://pick.example/1", TimeSpan.FromSeconds(210),
                    MatchConfidence: MatchConfidence.Low));
        }

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            DownloadCalls++;
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"picked-{melodyId}.mp3");
            File.WriteAllBytes(path, TestAudio.MinimalMp3());
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }

    private PickPlugin _plugin = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-pick-ui-{Guid.NewGuid():N}.db");
        _dir = Path.Combine(Path.GetTempPath(), $"mb-pick-ui-{Guid.NewGuid():N}");
        var factory = new TestSqliteFactory(_dbPath);
        using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-pick",
                Name = "Picker UI test",
                SourceUrl = "stub:pick",
                TargetDirectory = _dir,
                PreferredFormat = "auto",
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "mel-ui-1",
                        Title = "The Real Song",
                        Artist = "The Artist",
                        Position = 0,
                        DurationMs = 210_000,
                        DownloadStatus = "pending",
                    },
                },
            });
            db.SaveChanges();
        }

        _plugin = new PickPlugin();
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);
        _ctx.Services.AddDownloadPages(factory, _plugin);
    }

    [TearDown]
    public void TearDown()
    {
        _ctx.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
        }
    }

    private IRenderedComponent<PlaylistDetails> RenderPage()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-pick"));
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("The Real Song")), TimeSpan.FromSeconds(5));
        return cut;
    }

    [Test]
    public void Picker_ListsEveryCandidate_AndPickingOneDownloadsIt()
    {
        var cut = RenderPage();

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Choose source").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Choose source"), "the dialog is open");
            Assert.That(cut.FindAll(".source-candidate"), Has.Count.EqualTo(1));
            Assert.That(cut.Markup, Does.Contain("The Real Song"));
            Assert.That(cut.Markup, Does.Contain("Pick plugin"), "the source names itself");
            Assert.That(cut.Markup, Does.Contain("unsure"),
                "a hit the automatic match refused is labelled, not hidden");
        }, TimeSpan.FromSeconds(5));

        cut.Find(".source-candidate button").Click();

        cut.WaitForAssertion(() =>
        {
            var row = cut.FindAll("tr").First(r => r.TextContent.Contains("The Real Song"));
            Assert.That(row.TextContent, Does.Contain("downloaded"), "the picked source landed");
            Assert.That(cut.FindAll(".source-candidate"), Is.Empty, "the dialog closes on success");
        }, TimeSpan.FromSeconds(10));
        Assert.That(_plugin.DownloadCalls, Is.EqualTo(1));
    }

    [Test]
    public void Picker_WithoutResults_SaysSo()
    {
        _plugin.HitTitle = null;
        var cut = RenderPage();

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Choose source").Click();

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("No plugin returned a result")),
            TimeSpan.FromSeconds(5));
    }

    [Test]
    public void Picker_IsOfferedOnADownloadedRow_ForRepairingAWrongMatch()
    {
        using (var db = new TestSqliteFactory(_dbPath).CreateDbContext())
        {
            var track = db.Tracks.Single();
            track.DownloadStatus = "downloaded";
            track.CurrentPath = Path.Combine(_dir, "wrong-song.mp3");
            db.SaveChanges();
        }
        var cut = RenderPage();

        // The repair flow starts from the row itself, whatever its status.
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Choose source").Click();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".source-candidate"), Has.Count.EqualTo(1)), TimeSpan.FromSeconds(5));

        cut.Find(".source-candidate button").Click();

        cut.WaitForAssertion(() =>
        {
            using var db = new TestSqliteFactory(_dbPath).CreateDbContext();
            var track = db.Tracks.AsNoTracking().Single();
            Assert.That(track.CurrentPath, Does.Contain("picked-mel-ui-1"),
                "the replacement takes the row over");
        }, TimeSpan.FromSeconds(10));
    }
}
