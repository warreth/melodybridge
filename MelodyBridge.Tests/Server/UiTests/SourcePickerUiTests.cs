using Bunit;
using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using Microsoft.AspNetCore.Components;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Pages;
using MelodyBridge.Server.Services;
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
        public string Id { get; init; } = "pick";
        public string Name { get; init; } = "Pick plugin";
        public PluginCapabilities Capabilities { get; set; } = PluginCapabilities.Any;
        public string? HitTitle { get; set; } = "The Real Song";
        /// <summary>Optional multi-candidate result for filter tests.</summary>
        public List<DownloaderSearchHit>? Hits { get; set; }
        public int DownloadCalls { get; private set; }
        /// <summary>When set, the search waits for it: races can be staged.</summary>
        public TaskCompletionSource? SearchGate { get; set; }
        /// <summary>When set, the download waits for it: progress is staged.</summary>
        public TaskCompletionSource? DownloadGate { get; set; }
        /// <summary>When true every download reports a dead source link.</summary>
        public bool FailDownload { get; set; }
        /// <summary>Gates per requested title, for two overlapping searches.</summary>
        public Dictionary<string, TaskCompletionSource> GatesByTitle { get; } = new(StringComparer.Ordinal);
        /// <summary>What the exact-quality probe reports; null = cannot tell.</summary>
        public CandidateAudioQuality? ProbeResult { get; set; }
        /// <summary>Per-hit facts the catalog already knows, like Qobuz.</summary>
        public int? HitBitDepth { get; set; }
        public int? HitSampleRate { get; set; }
        /// <summary>The quality the last download was asked for.</summary>
        public DownloadQuality? LastQuality { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<CandidateAudioQuality?> ProbeQualityAsync(
            string sourceUrl, CancellationToken ct = default)
            => Task.FromResult(ProbeResult);

        public async Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
        {
            await WaitForGateAsync(query);
            if (HitTitle is null) return null;
            return new DownloaderSearchHit(
                $"Hit for {query.Title}", "The Artist", $"https://pick.example/{Id}/{query.Title}",
                TimeSpan.FromSeconds(210), MatchConfidence: MatchConfidence.Low,
                BitDepth: HitBitDepth, SampleRateHz: HitSampleRate);
        }

        public async Task<IReadOnlyList<DownloaderSearchHit>> SearchAllAsync(
            TrackQuery query, CancellationToken ct = default)
        {
            await WaitForGateAsync(query);
            if (Hits is not null) return Hits;
            if (HitTitle is null) return Array.Empty<DownloaderSearchHit>();
            return new[]
            {
                new DownloaderSearchHit(
                    $"Hit for {query.Title}", "The Artist", $"https://pick.example/{Id}/{query.Title}",
                    TimeSpan.FromSeconds(210), MatchConfidence: MatchConfidence.Low,
                    BitDepth: HitBitDepth, SampleRateHz: HitSampleRate),
            };
        }

        private async Task WaitForGateAsync(TrackQuery query)
        {
            if (GatesByTitle.TryGetValue(query.Title ?? "", out var gate))
            {
                // The gate belongs to the test: it always opens, and the
                // 10-second default would break the grid timing before the
                // test sees the panel state it wants.
                await gate.Task;
                return;
            }
            if (SearchGate is { } searchGate)
                await searchGate.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public async Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            DownloadCalls++;
            LastQuality = quality;
            if (DownloadGate is { } gate)
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (FailDownload)
                return new DownloaderDownloadResult(false, null, "link expired");
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"picked-{melodyId}.mp3");
            File.WriteAllBytes(path, TestAudio.MinimalMp3());
            return new DownloaderDownloadResult(true, path, null);
        }
    }

    private PickPlugin _plugin = null!;
    private PickPlugin _other = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _ctx.Services.AddPluginAuthMonitor();
        _ctx.Services.AddSingleton<IDownloaderRegistry>(new EmptyRegistry());
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
                        // 5:00 while the stub hit is 3:30, so the picker
                        // shows a length comparison.
                        DurationMs = 300_000,
                        DownloadStatus = "pending",
                    },
                },
            });
            db.SaveChanges();
        }

        _plugin = new PickPlugin();
        // A silent second source, available for the filter test without
        // changing what every other test sees.
        _other = new PickPlugin { Id = "other", Name = "Other plugin", HitTitle = null };
        _factory = factory;
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);
        _ctx.Services.AddDownloadPages(factory, _plugin, _other);
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

    private static async Task OpenPickerAsync(IRenderedComponent<PlaylistDetails> cut)
    {
        cut.WaitForAssertion(() => Assert.That(
            cut.FindAll("button").Any(b => b.TextContent.Trim() == "Choose source"),
            Is.True), TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => cut.FindAll("button")
            .Single(b => b.TextContent.Trim() == "Choose source").Click());
    }

    private IRenderedComponent<PlaylistDetails> RenderPage()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-pick"));
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("The Real Song")), TimeSpan.FromSeconds(5));
        return cut;
    }

    [Test]
    public void BulkRetrySummary_NamesEveryUnresolvedCategory()
    {
        var summary = PlaylistDetails.BulkRetrySummary(4, 1,
            new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["failed"] = 1,
                ["needs-review"] = 1,
                ["claimed-by-another-run"] = 1,
            });

        Assert.Multiple(() =>
        {
            Assert.That(summary, Does.Contain("Retried 4 failed tracks"));
            Assert.That(summary, Does.Contain("1 downloaded"));
            Assert.That(summary, Does.Contain("1 still failed"));
            Assert.That(summary, Does.Contain("1 for review"));
            Assert.That(summary, Does.Contain("1 already downloading"));
        });
    }

    [Test]
    public async Task Picker_ListsEveryCandidate_AndPickingOneDownloadsIt()
    {
        var cut = RenderPage();

        await OpenPickerAsync(cut);

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Choose source"), "the dialog is open");
            Assert.That(cut.FindAll(".source-candidate"), Has.Count.EqualTo(1));
            Assert.That(cut.Markup, Does.Contain("The Real Song"));
            Assert.That(cut.Markup, Does.Contain("Pick plugin"), "the source names itself");
            Assert.That(cut.Markup, Does.Contain("unsure"),
                "a hit the automatic match refused is labelled, not hidden");
        }, TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Find(".source-candidate .btn-modern.primary").Click());

        cut.WaitForAssertion(() =>
        {
            var row = cut.FindAll("tr").First(r => r.TextContent.Contains("The Real Song"));
            Assert.That(row.TextContent, Does.Contain("downloaded"), "the picked source landed");
            Assert.That(cut.FindAll(".source-candidate"), Is.Empty, "the dialog closes on success");
        }, TimeSpan.FromSeconds(10));
        Assert.That(_plugin.DownloadCalls, Is.EqualTo(1));
    }

    private sealed class DownPlugin : IDownloader
    {
        public string Id => "down";
        public string Name => "Down plugin";
        public string Description => "test";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;
        public string? LastUnavailableReason => "The service did not answer";
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(null);
        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
            => Task.FromResult(new DownloaderDownloadResult(false, null, "down"));
    }

    /// <summary>Reports a dead login while the picker's search is running.</summary>
    private sealed class LateAuthPlugin : IDownloader, IPluginAuthStatusProvider
    {
        public string Id => "qobuz";
        public string Name => "Qobuz";
        public string Description => "test";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;
        public PluginAuthStatus AuthStatus { get; private set; } = PluginAuthStatus.Valid;
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(null);
        public Task<IReadOnlyList<DownloaderSearchHit>> SearchAllAsync(
            TrackQuery query, CancellationToken ct = default)
        {
            AuthStatus = new PluginAuthStatus(
                PluginAuthState.Expired, "The token expired during the search");
            return Task.FromResult<IReadOnlyList<DownloaderSearchHit>>(
                Array.Empty<DownloaderSearchHit>());
        }
        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
            => Task.FromResult(new DownloaderDownloadResult(false, null, "expired"));
    }

    [Test]
    public async Task Picker_ExplainsAnEnabledPluginThatWasNotSearched()
    {
        _ctx.Services.AddSingleton<IDownloaderRegistry>(
            new ListDownloaderRegistry(new DownPlugin()));

        var cut = RenderPage();
        await OpenPickerAsync(cut);

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Not searched right now"));
            Assert.That(cut.Markup, Does.Contain("Down plugin"));
            Assert.That(cut.Markup, Does.Contain("The service did not answer"));
            Assert.That(
                cut.FindAll("button[title='Why is this missing? Open the plugin card']"),
                Is.Not.Empty);
        }, TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut
            .Find("button[title='Why is this missing? Open the plugin card']").Click());
        Assert.That(_ctx.Services.GetRequiredService<NavigationManager>().Uri,
            Does.EndWith("/plugins#plugin-down"));
    }

    [Test]
    public async Task Picker_ExplainsAPluginWhoseLoginNeedsAttention()
    {
        var monitor = _ctx.Services.GetRequiredService<PluginAuthMonitor>();
        monitor.Report("qobuz", "Qobuz", "The Qobuz token expired");

        var cut = RenderPage();
        await OpenPickerAsync(cut);

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Some plugins are not searched"));
            Assert.That(cut.Markup, Does.Contain("Qobuz"));
            Assert.That(cut.Markup, Does.Contain("The Qobuz token expired"));
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Picker_SeesALoginThatExpiresDuringTheSearch()
    {
        var late = new LateAuthPlugin();
        var registry = new ListDownloaderRegistry(late);
        var manager = new DownloadManager(
            registry, NullLogger<DownloadManager>.Instance);
        var store = new PlaylistStore(
            _factory, Array.Empty<ISourceProvider>(), manager,
            NullLogger<PlaylistStore>.Instance, changes: new DataChangeTracker());
        _ctx.Services.AddSingleton<IDownloaderRegistry>(registry);
        _ctx.Services.AddSingleton(store);

        var cut = RenderPage();
        await OpenPickerAsync(cut);

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Some plugins are not searched"));
            Assert.That(cut.Markup, Does.Contain("Qobuz"));
            Assert.That(cut.Markup, Does.Contain("The token expired during the search"));
        }, TimeSpan.FromSeconds(5));
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
    public async Task Picker_WithoutResults_SaysSo()
    {
        _plugin.HitTitle = null;
        var cut = RenderPage();

        await OpenPickerAsync(cut);

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("No plugin returned a result")),
            TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Picker_ConfidenceFilter_NarrowsTheList()
    {
        _plugin.Hits = new List<DownloaderSearchHit>
        {
            new("Sure hit", "The Artist", "https://pick.example/sure",
                TimeSpan.FromSeconds(210), MatchConfidence: MatchConfidence.High),
            new("Maybe hit", "The Artist", "https://pick.example/maybe",
                TimeSpan.FromSeconds(211), MatchConfidence: MatchConfidence.Low),
        };
        var cut = RenderPage();
        await OpenPickerAsync(cut);
        cut.WaitForAssertion(() => Assert.That(cut.FindAll(".source-candidate").Count, Is.EqualTo(2)));

        var confidence = cut.Find("select[aria-label='Filter by match confidence']");
        confidence.Change("high");

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll(".source-candidate").Count, Is.EqualTo(1));
            Assert.That(cut.Find(".source-candidate").TextContent, Does.Contain("Sure hit"));
            Assert.That(cut.Find(".source-filter-count").TextContent.Trim(), Is.EqualTo("1 of 2"));
        });
    }

    [Test]
    public async Task Picker_CanServeFilter_HidesSourcesOutsideTheTarget()
    {
        using (var db = new TestSqliteFactory(_dbPath).CreateDbContext())
        {
            var playlist = db.Playlists.Single();
            playlist.PreferredFormat = "preset:lossless";
            db.SaveChanges();
        }
        _plugin.Capabilities = PluginCapabilities.Lossy(AudioFormat.Mp3, 128, 320);

        var cut = RenderPage();
        await OpenPickerAsync(cut);
        cut.WaitForAssertion(() => Assert.That(cut.FindAll(".source-candidate").Count, Is.EqualTo(1)));

        var checkbox = cut.Find(".source-filter-check input");
        checkbox.Change(true);

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll(".source-candidate").Count, Is.EqualTo(0));
            Assert.That(cut.Markup, Does.Contain("No candidate matches these filters"));
        });
    }

    [Test]
    public async Task Picker_PluginFilter_KeepsOnlyThatSource()
    {
        _other.Hits = new List<DownloaderSearchHit>
        {
            new("Other hit", "The Artist", "https://pick.example/other/other-hit",
                TimeSpan.FromSeconds(210), MatchConfidence: MatchConfidence.High),
        };

        var cut = RenderPage();
        await OpenPickerAsync(cut);
        cut.WaitForAssertion(() => Assert.That(cut.FindAll(".source-candidate").Count,
            Is.EqualTo(2)));

        cut.Find("select[aria-label='Filter by plugin']").Change("other");

        Assert.Multiple(() =>
        {
            Assert.That(cut.FindAll(".source-candidate").Count, Is.EqualTo(1));
            Assert.That(cut.Find(".source-candidate").TextContent, Does.Contain("Other hit"));
            Assert.That(cut.Find(".source-candidate").TextContent, Does.Not.Contain("Hit for The Real Song"));
        });
    }

    [Test]
    public async Task Picker_CopySourceUrl_CopiesTheCandidateLink()
    {
        _ctx.JSInterop.Setup<bool>("melody.copyText", _ => true).SetResult(true);
        var cut = RenderPage();
        await OpenPickerAsync(cut);
        cut.WaitForAssertion(() => Assert.That(cut.FindAll(".source-candidate").Count, Is.EqualTo(1)));

        await cut.InvokeAsync(() => cut.Find("button[title='Copy source URL']").Click());

        Assert.That(_ctx.JSInterop.Invocations.Any(invocation =>
            invocation.Identifier == "melody.copyText"
            && invocation.Arguments.Any(argument => argument?.ToString()?.Contains("pick.example") == true)),
            Is.True);
    }

    [Test]
    public async Task Picker_OpenSettingsAndFixActions_NavigateToThePluginCard()
    {
        var cut = RenderPage();
        await OpenPickerAsync(cut);
        cut.WaitForAssertion(() => Assert.That(cut.FindAll(".source-candidate").Count, Is.EqualTo(1)));

        await cut.InvokeAsync(() => cut.Find("button[title='Open Pick plugin settings']").Click());

        Assert.That(_ctx.Services.GetRequiredService<NavigationManager>().Uri,
            Does.EndWith("/plugins#plugin-pick"));
    }

    [Test]
    public async Task Picker_EmptyStateOffersAFixButtonForALoginProblem()
    {
        _plugin.HitTitle = null;
        _ctx.Services.GetRequiredService<PluginAuthMonitor>()
            .Report("qobuz", "Qobuz", "The Qobuz token was rejected");
        var cut = RenderPage();
        await OpenPickerAsync(cut);

        cut.WaitForAssertion(() => Assert.That(cut.FindAll(".source-empty-actions button").Count,
            Is.EqualTo(1)), TimeSpan.FromSeconds(5));
        await cut.InvokeAsync(() => cut.FindAll(".source-empty-actions button").Single().Click());

        Assert.That(_ctx.Services.GetRequiredService<NavigationManager>().Uri,
            Does.EndWith("/plugins#plugin-qobuz"));
    }

    [Test]
    public async Task Picker_RemembersTheConfidenceFilterAfterCloseAndReopen()
    {
        _plugin.Hits = new List<DownloaderSearchHit>
        {
            new("Sure hit", "The Artist", "https://pick.example/sure",
                TimeSpan.FromSeconds(210), MatchConfidence: MatchConfidence.High),
            new("Maybe hit", "The Artist", "https://pick.example/maybe",
                TimeSpan.FromSeconds(211), MatchConfidence: MatchConfidence.Low),
        };
        var cut = RenderPage();
        await OpenPickerAsync(cut);
        cut.WaitForAssertion(() => Assert.That(cut.FindAll(".source-candidate").Count, Is.EqualTo(2)));

        cut.Find("select[aria-label='Filter by match confidence']").Change("high");
        cut.FindAll("button").Single(button => button.TextContent.Trim() == "Close").Click();

        await OpenPickerAsync(cut);
        var confidence = cut.Find("select[aria-label='Filter by match confidence']");
        Assert.That(confidence.GetAttribute("value"), Is.EqualTo("high"));
    }

    [Test]
    public async Task Playlist_RetryAllFailed_DownloadsEveryFailedTrack()
    {
        using (var db = new TestSqliteFactory(_dbPath).CreateDbContext())
        {
            for (var i = 0; i < 2; i++)
            {
                db.Tracks.Add(new TrackEntity
                {
                    PlaylistEntityId = "pl-pick",
                    MelodyId = $"mel-failed-{i}",
                    Title = $"Failed Song {i}",
                    Artist = "The Artist",
                    Position = 10 + i,
                    DurationMs = 200_000,
                    DownloadStatus = "failed",
                });
            }
            db.SaveChanges();
        }

        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Retry failed (2)")),
            TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.FindAll("button")
            .Single(button => button.TextContent.Contains("Retry failed")).Click());

        cut.WaitForAssertion(() =>
        {
            var rows = cut.FindAll("tr").Where(row => row.TextContent.Contains("Failed Song")).ToList();
            Assert.That(rows, Has.Count.EqualTo(2));
            Assert.That(rows.All(row => row.TextContent.Contains("downloaded")), Is.True);
        }, TimeSpan.FromSeconds(15));
        Assert.That(_plugin.DownloadCalls, Is.EqualTo(2));
    }

    [Test]
    public async Task Playlist_RetryDeepLink_DownloadsTheTrackOnLoad()
    {
        int retryId;
        using (var db = new TestSqliteFactory(_dbPath).CreateDbContext())
        {
            var track = new TrackEntity
            {
                PlaylistEntityId = "pl-pick",
                MelodyId = "mel-retry-link",
                Title = "Retry Me",
                Artist = "The Artist",
                Position = 20,
                DurationMs = 200_000,
                DownloadStatus = "failed",
            };
            db.Tracks.Add(track);
            db.SaveChanges();
            retryId = track.Id;
        }

        _ctx.Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"/playlists/pl-pick?retryTrack={retryId}");
        var cut = _ctx.Render<PlaylistDetails>(p => p
            .Add(page => page.PlaylistId, "pl-pick"));

        cut.WaitForAssertion(() =>
        {
            var row = cut.FindAll("tr").Single(r => r.TextContent.Contains("Retry Me"));
            Assert.That(row.TextContent, Does.Contain("downloaded"));
        }, TimeSpan.FromSeconds(10));

        Assert.That(_ctx.Services.GetRequiredService<NavigationManager>().Uri,
            Does.Not.Contain("retryTrack"),
            "the one-shot retry query is consumed before the attempt");
    }

    [Test]
    public async Task Playlist_RetryDeepLink_ForADownloadedTrack_IsConsumedWithoutDownloadingAgain()
    {
        int trackId;
        using (var db = new TestSqliteFactory(_dbPath).CreateDbContext())
        {
            var track = new TrackEntity
            {
                PlaylistEntityId = "pl-pick",
                MelodyId = "mel-retry-downloaded",
                Title = "Already Here",
                Artist = "The Artist",
                Position = 21,
                DurationMs = 200_000,
                DownloadStatus = "downloaded",
            };
            db.Tracks.Add(track);
            db.SaveChanges();
            trackId = track.Id;
        }

        _ctx.Services.GetRequiredService<NavigationManager>()
            .NavigateTo($"/playlists/pl-pick?retryTrack={trackId}");
        var cut = _ctx.Render<PlaylistDetails>(p => p
            .Add(page => page.PlaylistId, "pl-pick"));

        cut.WaitForAssertion(() =>
        {
            var row = cut.FindAll("tr").Single(r => r.TextContent.Contains("Already Here"));
            Assert.That(row.TextContent, Does.Contain("downloaded"));
        }, TimeSpan.FromSeconds(10));

        Assert.Multiple(() =>
        {
            Assert.That(_plugin.DownloadCalls, Is.EqualTo(0),
                "a downloaded row is not fetched again");
            Assert.That(_ctx.Services.GetRequiredService<NavigationManager>().Uri,
                Does.Not.Contain("retryTrack"),
                "a refresh cannot replay the old retry");
        });
    }

    [Test]
    public async Task Playlist_RetryAllFailed_CountsADeletedRowAsNotRetried()
    {
        int trackId;
        using (var db = new TestSqliteFactory(_dbPath).CreateDbContext())
        {
            var track = new TrackEntity
            {
                PlaylistEntityId = "pl-pick",
                MelodyId = "mel-deleted-retry",
                Title = "Gone Before Retry",
                Artist = "The Artist",
                Position = 30,
                DurationMs = 200_000,
                DownloadStatus = "failed",
            };
            db.Tracks.Add(track);
            db.SaveChanges();
            trackId = track.Id;
        }

        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Retry failed (1)")),
            TimeSpan.FromSeconds(5));

        using (var db = new TestSqliteFactory(_dbPath).CreateDbContext())
        {
            db.Tracks.Remove(db.Tracks.Single(t => t.Id == trackId));
            db.SaveChanges();
        }

        await cut.InvokeAsync(() => cut.FindAll("button")
            .Single(button => button.TextContent.Contains("Retry failed")).Click());

        var notifications = _ctx.Services.GetRequiredService<NotificationService>();
        cut.WaitForAssertion(() =>
        {
            var final = notifications.History().First(notification =>
                notification.Message.StartsWith("Retried ", StringComparison.Ordinal));
            Assert.That(final.Message, Does.Contain("not found"),
                "a deleted row is named, not counted as a download");
            Assert.That(final.Message, Does.Not.Contain("all downloaded"));
        }, TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task Playlist_RetryAllFailed_CountsARowOwnedByAnotherRunAsDownloading()
    {
        int trackId;
        using (var db = new TestSqliteFactory(_dbPath).CreateDbContext())
        {
            var track = new TrackEntity
            {
                PlaylistEntityId = "pl-pick",
                MelodyId = "mel-claimed-retry",
                Title = "Already Running",
                Artist = "The Artist",
                Position = 31,
                DurationMs = 200_000,
                DownloadStatus = "failed",
            };
            db.Tracks.Add(track);
            db.SaveChanges();
            trackId = track.Id;
        }

        var cut = RenderPage();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Retry failed (1)")),
            TimeSpan.FromSeconds(5));

        using (var db = new TestSqliteFactory(_dbPath).CreateDbContext())
        {
            db.Tracks.Single(t => t.Id == trackId).DownloadStatus = "in_progress";
            db.SaveChanges();
        }

        await cut.InvokeAsync(() => cut.FindAll("button")
            .Single(button => button.TextContent.Contains("Retry failed")).Click());

        var notifications = _ctx.Services.GetRequiredService<NotificationService>();
        cut.WaitForAssertion(() =>
        {
            var final = notifications.History().First(notification =>
                notification.Message.StartsWith("Retried ", StringComparison.Ordinal));
            Assert.That(final.Message, Does.Contain("already downloading"));
            Assert.That(final.Message, Does.Not.Contain("all downloaded"));
        }, TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task Picker_IsOfferedOnADownloadedRow_ForRepairingAWrongMatch()
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
        await OpenPickerAsync(cut);
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".source-candidate"), Has.Count.EqualTo(1)), TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Find(".source-candidate .btn-modern.primary").Click());

        cut.WaitForAssertion(() =>
        {
            using var db = new TestSqliteFactory(_dbPath).CreateDbContext();
            var track = db.Tracks.AsNoTracking().Single();
            Assert.That(track.CurrentPath, Is.Not.Null);
            Assert.That(File.Exists(track.CurrentPath), Is.True,
                "the replacement file exists on disk");
            Assert.That(Path.GetFileName(track.CurrentPath), Is.Not.EqualTo("wrong-song.mp3"),
                "the replacement takes the row over");
        }, TimeSpan.FromSeconds(10));
    }
    [Test]
    public async Task Picker_ShowsTheOriginalLength_AndHowEachHitDiffers()
    {
        var cut = RenderPage();

        await OpenPickerAsync(cut);

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Original 05:00"),
                "the playlist's own length is visible for comparison");
            Assert.That(cut.Markup, Does.Contain("-1:30"),
                "the 3:30 hit is 1:30 shorter than the original");
        }, TimeSpan.FromSeconds(5));
    }

    /// <summary>Sets the seeded playlist's quality before a render.</summary>
    private void SetPreferredFormat(string format)
    {
        using var db = _factory.CreateDbContext();
        db.Playlists.Single(p => p.Id == "pl-pick").PreferredFormat = format;
        db.SaveChanges();
    }

    [Test]
    public async Task Picker_ShowsTheExactSourceQuality_AndFlagsALossyPickBelowTheFlacTarget()
    {
        SetPreferredFormat("preset:lossless");
        _plugin.Capabilities = PluginCapabilities.Lossy(AudioFormat.Aac, 128, 320);
        _plugin.ProbeResult = new CandidateAudioQuality(
            "mp4a.40.2", 160, 44100, null, Lossless: false);

        var cut = RenderPage();
        await OpenPickerAsync(cut);

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Target quality"));
            Assert.That(cut.Markup, Does.Contain("Lossless"),
                "the dialog names the playlist's target");
            Assert.That(cut.Markup, Does.Contain("AAC · 44.1 kHz · 160 kbps"),
                "the row shows the exact source facts, never a guess");
            Assert.That(cut.FindAll(".source-candidate .pill")
                    .Any(p => p.TextContent.Contains("saved as-is")),
                Is.True,
                "a lossy pick cannot satisfy the FLAC target and says so");
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Picker_FlagsAMatchingLosslessSource()
    {
        SetPreferredFormat("preset:lossless");
        _plugin.Capabilities = PluginCapabilities.LosslessSources();
        _plugin.ProbeResult = new CandidateAudioQuality("flac", null, 44100, 16, Lossless: true);

        var cut = RenderPage();
        await OpenPickerAsync(cut);

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("FLAC · 16-bit · 44.1 kHz"),
                "lossless facts carry bit depth and sample rate");
            Assert.That(cut.FindAll(".source-candidate .pill")
                    .Any(p => p.TextContent.Contains("matches target")),
                Is.True,
                "a real lossless source is marked as matching");
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Picker_DoesNotCallAnUnknownBitrateAMatch_OnACappedTarget()
    {
        SetPreferredFormat("preset:saver");
        _plugin.Capabilities = PluginCapabilities.LosslessSources();
        _plugin.ProbeResult = new CandidateAudioQuality("flac", null, 44100, 16, Lossless: true);

        var cut = RenderPage();
        await OpenPickerAsync(cut);

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.FindAll(".source-candidate .pill")
                    .Any(p => p.TextContent.Contains("matches target")),
                Is.False,
                "an unknown bitrate must not be called a match for a capped target");
            var savedAsIs = cut.FindAll(".source-candidate .pill")
                .Single(p => p.TextContent.Contains("saved as-is"));
            Assert.That(savedAsIs.GetAttribute("title"), Does.Contain("keeps its source quality"));
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Picker_SaysAPickWillBeConverted_WhenThePluginCanServeAnExplicitTarget()
    {
        SetPreferredFormat("mp3:192-320");
        _plugin.Capabilities = PluginCapabilities.Lossy(AudioFormat.Mp3, 64, 320);
        _plugin.ProbeResult = new CandidateAudioQuality("opus", 128, 48000, null, Lossless: false);

        var cut = RenderPage();
        await OpenPickerAsync(cut);

        cut.WaitForAssertion(() =>
        {
            var offTarget = cut.FindAll(".source-candidate .pill")
                .Single(p => p.TextContent.Contains("off target"));
            Assert.That(offTarget.GetAttribute("title"), Does.Contain("converted to"),
                "a plugin that can serve the container converts the pick, it does not save the source as-is");
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Picker_PrefersTheTracksOwnFactsOverTheConfiguredTier()
    {
        SetPreferredFormat("auto");
        _plugin.Capabilities = PluginCapabilities.LosslessSources();
        _plugin.HitBitDepth = 16;
        _plugin.HitSampleRate = 44100;
        _plugin.ProbeResult = new CandidateAudioQuality(
            "flac", null, 96000, 24, Lossless: true, Note: "up to your configured Qobuz tier");

        var cut = RenderPage();
        await OpenPickerAsync(cut);

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("FLAC · 16-bit · 44.1 kHz"),
                "a CD-only track shows its own maximum, not the tier");
            Assert.That(cut.Markup, Does.Not.Contain("up to your configured Qobuz tier"),
                "the tier note is dropped when the track facts are exact");
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Picker_ClampsTheTrackMaximumToTheConfiguredTier()
    {
        SetPreferredFormat("auto");
        _plugin.Capabilities = PluginCapabilities.LosslessSources();
        _plugin.HitBitDepth = 24;
        _plugin.HitSampleRate = 96000;
        _plugin.ProbeResult = new CandidateAudioQuality(
            "flac", null, 44100, 16, Lossless: true, Note: "up to your configured Qobuz tier");

        var cut = RenderPage();
        await OpenPickerAsync(cut);

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("FLAC · 16-bit · 44.1 kHz"),
                "the download is capped by the configured tier");
            Assert.That(cut.Markup, Does.Not.Contain("24-bit"),
                "the track maximum must not promise more than the tier delivers");
            Assert.That(cut.Markup, Does.Not.Contain("96 kHz"));
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Picker_ShowsProgressWhileAPickDownloads()
    {
        var gate = new TaskCompletionSource();
        _plugin.DownloadGate = gate;
        var cut = RenderPage();

        await OpenPickerAsync(cut);
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".source-candidate"), Has.Count.EqualTo(1)), TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Find(".source-candidate .btn-modern.primary").Click());
        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Downloading…"),
                "the pick shows progress instead of looking frozen");
            Assert.That(cut.FindAll(".source-progress"), Has.Count.EqualTo(1));
        }, TimeSpan.FromSeconds(5));

        gate.SetResult();
        cut.WaitForAssertion(() =>
        {
            var row = cut.FindAll("tr").First(r => r.TextContent.Contains("The Real Song"));
            Assert.That(row.TextContent, Does.Contain("downloaded"));
            Assert.That(cut.FindAll(".source-candidate"), Is.Empty, "the dialog closes on success");
        }, TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task Picker_KeepsTheDialogOpen_AndNamesTheFailingSource()
    {
        _plugin.FailDownload = true;
        var cut = RenderPage();

        await OpenPickerAsync(cut);
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".source-candidate"), Has.Count.EqualTo(1)), TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Find(".source-candidate .btn-modern.primary").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.FindAll(".source-candidate"), Has.Count.EqualTo(1),
                "a dead link keeps the dialog open for another pick");
            Assert.That(cut.Markup, Does.Contain("This source failed:"),
                "the failing row says so inline");
            Assert.That(cut.Markup, Does.Contain("link expired"));
        }, TimeSpan.FromSeconds(10));
    }

    [Test]
    public void HardlinkButton_AsksForConfirmationBeforeLinking()
    {
        using (var db = new TestSqliteFactory(_dbPath).CreateDbContext())
        {
            var row = db.Tracks.Single();
            row.DownloadStatus = "needs-review";
            row.Warning = "dedup conflict: playlist wants flac, any bitrate, existing file is opus, different from this playlist's target. Choose hardlink existing or download new.";
            db.SaveChanges();
        }

        var cut = RenderPage();
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Hardlink existing").Click();

        cut.WaitForAssertion(() =>
        {
            var dialog = cut.FindAll(".confirm-modal").Single();
            Assert.That(dialog.TextContent, Does.Contain("same bytes on disk"),
                "the dialog explains what a hardlink does before it runs");
            Assert.That(dialog.TextContent, Does.Not.Contain("dedup conflict:"),
                "the raw warning label is not repeated inside the dialog");
        }, TimeSpan.FromSeconds(5));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Cancel").Click();
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".confirm-modal"), Is.Empty),
            TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Picker_DisposingThePageWhileAPickRuns_DoesNotThrowOnCompletion()
    {
        var gate = new TaskCompletionSource();
        _plugin.DownloadGate = gate;
        var cut = RenderPage();

        await OpenPickerAsync(cut);
        cut.WaitForAssertion(() =>
            Assert.That(cut.FindAll(".source-candidate"), Has.Count.EqualTo(1)), TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Find(".source-candidate .btn-modern.primary").Click());
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Downloading…")), TimeSpan.FromSeconds(5));

        // Navigating away disposes the page while the pick is still
        // running; its completion must not throw out of the continuation.
        cut.Instance.Dispose();
        gate.SetResult();

        var unhandled = _ctx.Renderer.UnhandledException;
        var first = await Task.WhenAny(unhandled, Task.Delay(1500));
        Assert.That(first, Is.Not.SameAs(unhandled),
            unhandled.IsCompleted
                ? $"a pick completing after navigation threw: {unhandled.Result}"
                : "a pick completing after navigation must not throw");
    }

    [Test]
    public void LikedSongsSourceMarker_DoesNotRenderAnOpenSourceLink()
    {
        using (var db = new TestSqliteFactory(_dbPath).CreateDbContext())
        {
            db.Playlists.Single(p => p.Id == "pl-pick").SourceUrl = "spotify:liked";
            db.SaveChanges();
        }

        var cut = RenderPage();

        Assert.That(cut.Markup, Does.Not.Contain("Open source"),
            "a liked-songs marker is not a web link to open");
    }

}
