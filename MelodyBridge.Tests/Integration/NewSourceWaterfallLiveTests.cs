using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.FlacMusic;
using MelodyBridge.Infrastructure.Rhythmax;
using MelodyBridge.Infrastructure.Tagging;
using MelodyBridge.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// The new sources through the real composition, not the plugin classes in
/// isolation: the registry, the search router, the download manager, the
/// quality gate and the MELODY_ID write all have to line up. The other
/// plugins are disabled so the test cannot silently pass on an older source.
/// </summary>
[TestFixture]
[Category("Live")]
public class NewSourceWaterfallLiveTests
{
    private string _outDir = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _outDir = Path.Combine(Path.GetTempPath(), $"mb-newsource-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outDir);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try { Directory.Delete(_outDir, recursive: true); } catch { /* best effort */ }
    }

    private static async Task OnlyAsync(AppDownloadHost host, string id)
    {
        foreach (var plugin in host.Registry.GetAll())
            await host.Registry.SetEnabledAsync(plugin.Id, plugin.Id == id);
    }

    [Test]
    public async Task Rhythmax_DownloadsThroughTheTidalProvider()
    {
        using var host = AppDownloadHost.Create();
        var client = host.Services.GetRequiredService<RhythmaxClient>();
        await OnlyAsync(host, "rhythmax");
        var melody = "mb-live-rhythmax-" + Guid.NewGuid().ToString("N")[..6];

        // The public account pool flips between FULL and DEAD within
        // seconds. Each attempt asks for the current state and then runs
        // immediately; the health check inside the manager can still lose
        // the race, so a miss gets a fresh window.
        string? path = null;
        string? lastFailure = null;
        string? lastNotReady = null;
        var sawReady = false;
        for (var attempt = 0; attempt < 8 && path is null; attempt++)
        {
            var (ready, reason) = await client.CheckHealthAsync();
            if (!ready)
            {
                lastNotReady = reason ?? "Rhythmax is not ready";
                await Task.Delay(TimeSpan.FromSeconds(5));
                continue;
            }

            sawReady = true;
            path = await host.DownloadManager.DownloadTrackAsync(
                new TrackQuery("AC/DC", "Back In Black",
                    new DownloadQuality(AudioFormat.Flac),
                    TimeSpan.FromSeconds(255), "AUAP08000046"),
                _outDir, melody);
            if (path is null)
            {
                lastFailure = host.DownloadManager.LastFailure(melody);
                await Task.Delay(TimeSpan.FromSeconds(5));
            }
        }

        if (path is null)
        {
            // Only an explicitly unavailable public pool is a skip. Once
            // the health check says FULL, a search or download failure is
            // the code's problem and must fail the test.
            if (!sawReady)
                Assert.Ignore(lastNotReady ?? "Rhythmax was not available");
            if (IsExternalPacing(lastFailure))
                Assert.Ignore(lastFailure);
            Assert.Fail(lastFailure ?? "Rhythmax was ready but produced no file");
        }

        Assert.That(AudioSignature.DetectContainer(path!), Is.EqualTo(".flac"));
        Assert.That(TaglibHelper.ReadMelodyId(path!), Is.EqualTo(melody));
        Assert.That(BitrateProbe.MeasureKbps(path!), Is.InRange(700, 1400));
        Assert.That(host.DownloadManager.LastPluginId(melody), Is.EqualTo("rhythmax"));
    }

    private static bool IsExternalPacing(string? failure)
        => failure is not null
           && (failure.Contains("no working Tidal account", StringComparison.OrdinalIgnoreCase)
               || failure.Contains("only preview", StringComparison.OrdinalIgnoreCase)
               || failure.Contains("playback slot", StringComparison.OrdinalIgnoreCase)
               || failure.Contains("429", StringComparison.Ordinal));

    [Test]
    public async Task FlacMusic_DownloadsThroughItsOwnTitleSearch()
    {
        using var host = AppDownloadHost.Create();
        var site = host.Services.GetRequiredService<FlacMusicClient>();
        var (ready, reason) = await site.CheckHealthAsync();
        if (!ready) Assert.Ignore(reason ?? "flac.music.hi.cn is not ready");

        await OnlyAsync(host, "flacmusic");
        var melody = "mb-live-flacmusic-" + Guid.NewGuid().ToString("N")[..6];
        var path = await host.DownloadManager.DownloadTrackAsync(
            new TrackQuery("AC/DC", "Back In Black",
                new DownloadQuality(AudioFormat.Flac),
                TimeSpan.FromSeconds(255)),
            _outDir, melody);

        Assert.That(path, Is.Not.Null,
            "the title-search waterfall produced nothing: " + host.DownloadManager.LastFailure(melody));
        Assert.That(AudioSignature.DetectContainer(path!), Is.EqualTo(".flac"));
        Assert.That(TaglibHelper.ReadMelodyId(path!), Is.EqualTo(melody));
        Assert.That(BitrateProbe.MeasureKbps(path!), Is.InRange(800, 1300));
        Assert.That(host.DownloadManager.LastPluginId(melody), Is.EqualTo("flacmusic"));
    }

    [Test]
    public async Task NewSources_AppearInTheManualPickerWithTheirOwnUrls()
    {
        using var host = AppDownloadHost.Create();
        await OnlyAsync(host, "flacmusic");

        var outcome = await host.DownloadManager.SearchCandidatesDetailedAsync(
            new TrackQuery("AC/DC", "Back In Black",
                new DownloadQuality(AudioFormat.Flac), TimeSpan.FromSeconds(255)));

        Assert.That(outcome.Candidates, Is.Not.Empty,
            "the fallback source must be searchable by hand too");
        Assert.That(outcome.Candidates.All(c => c.PluginId == "flacmusic"), Is.True);
        Assert.That(outcome.Candidates.All(c => c.SourceUrl.Contains("/track/")), Is.True);
    }
}
