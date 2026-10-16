using System.Diagnostics;
using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.ApplicationServices;

/// <summary>
/// The waterfall's health behavior: open circuits are skipped without a
/// call, degraded providers run behind healthy ones, real outcomes feed
/// the breaker, and the time budgets (per plugin, per track) keep a
/// hanging source from holding the run. The settings come from a real
/// SQLite file, the audio files from ffmpeg: no fakes for either.
/// </summary>
[TestFixture]
public class DownloadManagerHealthTests
{
    /// <summary>Plugin double with scripted behavior and call counters.</summary>
    private sealed class ScriptedDownloader : IDownloader
    {
        private readonly string? _file;
        private readonly DownloaderSearchHit? _hit;

        public ScriptedDownloader(string id, string? file = null)
        {
            Id = id;
            _file = file;
            _hit = new DownloaderSearchHit("Song", "Artist", $"https://{id}.example/1", TimeSpan.FromSeconds(200));
        }

        public int SearchCalls;
        public int DownloadCalls;
        public Func<CancellationToken, Task<DownloaderSearchHit?>>? SearchBehavior;
        public bool DownloadFails;

        public string Id { get; }
        public string Name => Id;
        public string Description => "";
        public PluginCapabilities Capabilities { get; } = PluginCapabilities.Lossy(AudioFormat.Mp3);
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
        {
            Interlocked.Increment(ref SearchCalls);
            return SearchBehavior is not null ? SearchBehavior(ct) : Task.FromResult(_hit);
        }

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            Interlocked.Increment(ref DownloadCalls);
            if (DownloadFails || _file is null)
                return Task.FromResult(new DownloaderDownloadResult(false, null, "stub fails"));

            var path = Path.Combine(outputDirectory, $"{Id}-{Path.GetFileName(_file)}");
            File.Copy(_file, path, overwrite: true);
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }

    private string _dir = null!;
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mb-health-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-health-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
    }

    /// <summary>A real MP3 (2 seconds of silence) so the audio gates see real bytes.</summary>
    private string MakeRealMp3()
    {
        var path = Path.Combine(_dir, "src.mp3");
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-hide_banner -loglevel error -f lavfi -i anullsrc=r=44100:cl=stereo -t 2 -c:a libmp3lame -b:a 192k \"{path}\"",
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        proc.StandardError.ReadToEnd();
        proc.WaitForExit(10000);
        return path;
    }

    /// <summary>A search that never answers: only a budget can end it.</summary>
    private static async Task<DownloaderSearchHit?> Hang(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return null;
    }

    private static SettingsStore NewSettings(TestSqliteFactory factory) => new(factory);

    private static DownloadManager Manager(
        ProviderHealth health, SettingsStore? settings, params IDownloader[] plugins)
        => new(new ListDownloaderRegistry(plugins),
            NullLogger<DownloadManager>.Instance, health, settings);

    [Test]
    public async Task OpenCircuit_IsSkippedWithoutACall_NextProviderWins()
    {
        var mp3 = MakeRealMp3();
        var health = new ProviderHealth();
        for (var i = 0; i < 3; i++) health.RecordFailure("dead");

        var dead = new ScriptedDownloader("dead", mp3);
        var good = new ScriptedDownloader("good", mp3);
        var manager = Manager(health, settings: null, dead, good);

        var path = await manager.DownloadTrackAsync(
            new TrackQuery("Artist", "Song", DownloadQuality.Any), _dir, "m-open");

        Assert.Multiple(() =>
        {
            Assert.That(path, Is.Not.Null, manager.LastFailure("m-open"));
            Assert.That(dead.SearchCalls, Is.EqualTo(0), "the open circuit is skipped without a call");
            Assert.That(good.SearchCalls, Is.EqualTo(1));
            Assert.That(health.Score("good"), Is.EqualTo(1.0), "the success was recorded");
        });
    }

    [Test]
    public async Task DegradedProvider_RunsBehindHealthyOnes()
    {
        var mp3 = MakeRealMp3();
        var health = new ProviderHealth();
        // Not consecutive (the circuit stays closed), but mostly failing.
        health.RecordFailure("shaky");
        health.RecordSuccess("shaky");
        health.RecordFailure("shaky");
        health.RecordFailure("shaky");

        var shaky = new ScriptedDownloader("shaky", mp3);
        var solid = new ScriptedDownloader("solid", mp3);
        var manager = Manager(health, settings: null, shaky, solid); // shaky has the higher priority

        var path = await manager.DownloadTrackAsync(
            new TrackQuery("Artist", "Song", DownloadQuality.Any), _dir, "m-degraded");

        Assert.Multiple(() =>
        {
            Assert.That(path, Is.Not.Null, manager.LastFailure("m-degraded"));
            Assert.That(health.ShouldSkip("shaky"), Is.False, "degraded is not open");
            Assert.That(solid.SearchCalls, Is.EqualTo(1), "the healthy provider ran first");
            Assert.That(shaky.SearchCalls, Is.EqualTo(0), "and already won the track");
        });
    }

    [Test]
    public async Task FailedDownloads_OpenTheCircuit_ThroughRealOutcomes()
    {
        var mp3 = MakeRealMp3();
        var health = new ProviderHealth();
        var flaky = new ScriptedDownloader("flaky", mp3) { DownloadFails = true };
        var manager = Manager(health, settings: null, flaky);

        for (var i = 0; i < 3; i++)
            Assert.That(await manager.DownloadTrackAsync(
                new TrackQuery("Artist", "Song", DownloadQuality.Any), _dir, $"m-fail-{i}"), Is.Null);

        var callsAfterThree = flaky.SearchCalls;
        Assert.That(await manager.DownloadTrackAsync(
            new TrackQuery("Artist", "Song", DownloadQuality.Any), _dir, "m-fail-3"), Is.Null);

        Assert.Multiple(() =>
        {
            Assert.That(health.ShouldSkip("flaky"), Is.True, "three failed downloads opened the circuit");
            Assert.That(flaky.SearchCalls, Is.EqualTo(callsAfterThree),
                "the fourth run never called the provider");
        });
    }

    [Test]
    public async Task PluginBudget_MovesOnToTheNextProvider()
    {
        var mp3 = MakeRealMp3();
        var settings = NewSettings(_factory);
        await settings.SetAsync("download_plugin_budget_seconds", "1");
        await settings.SetAsync("download_track_budget_seconds", "60");

        var health = new ProviderHealth();
        var hanging = new ScriptedDownloader("hanging") { SearchBehavior = Hang };
        var good = new ScriptedDownloader("good", mp3);
        var manager = Manager(health, settings, hanging, good);

        var sw = Stopwatch.StartNew();
        var path = await manager.DownloadTrackAsync(
            new TrackQuery("Artist", "Song", DownloadQuality.Any), _dir, "m-plugin-budget");
        sw.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(path, Is.Not.Null, manager.LastFailure("m-plugin-budget"));
            Assert.That(good.SearchCalls, Is.EqualTo(1), "the track moved on");
            Assert.That(health.Score("hanging"), Is.LessThan(1.0), "the expiry counts as a failure");
            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(30)),
                "the plugin budget ended the hang");
        });
    }

    [Test]
    public async Task TrackBudget_FailsTheTrackInsteadOfHanging()
    {
        var settings = NewSettings(_factory);
        await settings.SetAsync("download_plugin_budget_seconds", "3600");
        await settings.SetAsync("download_track_budget_seconds", "1");

        var hanging = new ScriptedDownloader("hanging") { SearchBehavior = Hang };
        var manager = Manager(new ProviderHealth(), settings, hanging);

        var sw = Stopwatch.StartNew();
        var path = await manager.DownloadTrackAsync(
            new TrackQuery("Artist", "Song", DownloadQuality.Any), _dir, "m-track-budget");
        sw.Stop();

        Assert.Multiple(() =>
        {
            Assert.That(path, Is.Null);
            Assert.That(manager.LastFailure("m-track-budget"), Does.Contain("budget"));
            Assert.That(sw.Elapsed, Is.LessThan(TimeSpan.FromSeconds(30)),
                "the track budget ended the hang");
        });
    }

    [Test]
    public async Task Budgets_ComeFromSettings_DefaultedAndClamped()
    {
        var defaults = Manager(new ProviderHealth(), NewSettings(_factory));
        var (plugin, track) = await defaults.BudgetsAsync(default);
        Assert.Multiple(() =>
        {
            Assert.That(plugin, Is.EqualTo(DownloadManager.DefaultPluginBudget));
            Assert.That(track, Is.EqualTo(DownloadManager.DefaultTrackBudget));
        });

        var settings = NewSettings(_factory);
        await settings.SetAsync("download_plugin_budget_seconds", "120");
        await settings.SetAsync("download_track_budget_seconds", "600");
        (plugin, track) = await Manager(new ProviderHealth(), settings).BudgetsAsync(default);
        Assert.Multiple(() =>
        {
            Assert.That(plugin.TotalSeconds, Is.EqualTo(120));
            Assert.That(track.TotalSeconds, Is.EqualTo(600));
        });

        await settings.SetAsync("download_plugin_budget_seconds", "0");
        await settings.SetAsync("download_track_budget_seconds", "999999");
        (plugin, track) = await Manager(new ProviderHealth(), settings).BudgetsAsync(default);
        Assert.Multiple(() =>
        {
            Assert.That(plugin.TotalSeconds, Is.EqualTo(1), "clamped up to a minimum");
            Assert.That(track.TotalSeconds, Is.EqualTo(14400), "clamped down to a maximum");
        });
    }

    [Test]
    public async Task WithoutASettingsStore_TheDefaultsApply()
    {
        var manager = Manager(new ProviderHealth(), settings: null);
        var (plugin, track) = await manager.BudgetsAsync(default);
        Assert.Multiple(() =>
        {
            Assert.That(plugin, Is.EqualTo(DownloadManager.DefaultPluginBudget));
            Assert.That(track, Is.EqualTo(DownloadManager.DefaultTrackBudget));
        });
    }
}
