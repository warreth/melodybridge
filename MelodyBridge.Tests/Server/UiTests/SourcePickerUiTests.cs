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
        /// <summary>When set, the search waits for it: races can be staged.</summary>
        public TaskCompletionSource? SearchGate { get; set; }
        /// <summary>Gates per requested title, for two overlapping searches.</summary>
        public Dictionary<string, TaskCompletionSource> GatesByTitle { get; } = new(StringComparer.Ordinal);

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public async Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
        {
            await WaitForGateAsync(query);
            if (HitTitle is null) return null;
            return new DownloaderSearchHit(
                $"Hit for {query.Title}", "The Artist", $"https://pick.example/{query.Title}",
                TimeSpan.FromSeconds(210), MatchConfidence: MatchConfidence.Low);
        }

        public async Task<IReadOnlyList<DownloaderSearchHit>> SearchAllAsync(
            TrackQuery query, CancellationToken ct = default)
        {
            await WaitForGateAsync(query);
            if (HitTitle is null) return Array.Empty<DownloaderSearchHit>();
            return new[]
            {
                new DownloaderSearchHit(
                    $"Hit for {query.Title}", "The Artist", $"https://pick.example/{query.Title}",
                    TimeSpan.FromSeconds(210), MatchConfidence: MatchConfidence.Low),
            };
        }

        private async Task WaitForGateAsync(TrackQuery query)
        {
            if (GatesByTitle.TryGetValue(query.Title ?? "", out var gate))
            {
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));
                return;
            }
            if (SearchGate is { } searchGate)
                await searchGate.Task.WaitAsync(TimeSpan.FromSeconds(10));
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
    public async Task Picker_IgnoresAStaleSearch_AfterClosingAndOpeningAnotherTrack()
    {
        // A second track so two pickers can be opened in sequence.
        using (var db = new TestSqliteFactory(_dbPath).CreateDbContext())
        {
            db.Tracks.Add(new TrackEntity
            {
                PlaylistEntityId = "pl-pick",
                MelodyId = "mel-ui-2",
                Title = "Second Song",
                Artist = "The Artist",
                Position = 1,
                DurationMs = 200_000,
                DownloadStatus = "pending",
            });
            db.SaveChanges();
        }

        // The first track's search hangs; the second one's completes first.
        var slow = new TaskCompletionSource();
        var fast = new TaskCompletionSource();
        _plugin.GatesByTitle["The Real Song"] = slow;
        _plugin.GatesByTitle["Second Song"] = fast;

        var cut = RenderPage();
        const string pickerButton = "button[title='Search every plugin by hand and pick the source yourself']";

        cut.FindAll("tr").First(r => r.TextContent.Contains("The Real Song"))
            .QuerySelector(pickerButton)!.Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Close").Click();
        cut.FindAll("tr").First(r => r.TextContent.Contains("Second Song"))
            .QuerySelector(pickerButton)!.Click();

        fast.SetResult();
        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Hit for Second Song"), "the second search lands");
            Assert.That(cut.Markup, Does.Not.Contain("Hit for The Real Song"));
        }, TimeSpan.FromSeconds(5));

        // The stale first search finishes last: it must not overwrite the
        // open dialog, or its candidate would be downloaded for this track.
        slow.SetResult();
        await Task.Delay(200);

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Hit for Second Song"),
                "the open dialog keeps its own candidates");
            Assert.That(cut.Markup, Does.Not.Contain("Hit for The Real Song"),
                "a late result for another track is dropped");
        });
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
