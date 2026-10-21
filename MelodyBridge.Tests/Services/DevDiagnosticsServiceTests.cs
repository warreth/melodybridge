using MelodyBridge.Core;
using MelodyBridge.Core.Logging;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Moq;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// Dev diagnostics against a real SQLite file and real plugin doubles:
/// counts come from stored rows, file sizes from the database on disk,
/// plugin claims from the plugin itself, and the report redacts secrets
/// the same way the Logs page does.
/// </summary>
[TestFixture]
public class DevDiagnosticsServiceTests
{
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;
    private SettingsStore _settings = null!;
    private LogCollector _logs = null!;
    private Mock<IDownloaderRegistry> _registry = null!;
    private DevDiagnosticsService _service = null!;

    [SetUp]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-diagnostics-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        using (var db = _factory.CreateDbContext())
            db.Database.EnsureCreated();

        _settings = new SettingsStore(_factory);
        _logs = new LogCollector();
        _registry = new Mock<IDownloaderRegistry>();
        _registry.Setup(r => r.GetAll()).Returns(Array.Empty<IDownloader>());
        _registry.Setup(r => r.IsEnabled(It.IsAny<string>())).Returns(true);

        var host = new Mock<IHostEnvironment>();
        host.SetupGet(h => h.EnvironmentName).Returns("Development");
        host.SetupGet(h => h.ApplicationName).Returns("MelodyBridge");

        _service = new DevDiagnosticsService(
            _factory, _settings, _registry.Object, _logs, host.Object);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task GetDatabaseCountsAsync_ReadsTheStoredRows()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "diag-pl",
                Name = "Diagnostics",
                SourceUrl = "https://example.com/diag",
                TargetDirectory = "/tmp",
            });
            db.Tracks.AddRange(
                new TrackEntity
                {
                    MelodyId = "diag-1", Title = "One", Position = 0,
                    DownloadStatus = "downloaded", PlaylistEntityId = "diag-pl",
                },
                new TrackEntity
                {
                    MelodyId = "diag-2", Title = "Two", Position = 1,
                    DownloadStatus = "downloaded", PlaylistEntityId = "diag-pl",
                },
                new TrackEntity
                {
                    MelodyId = "diag-3", Title = "Three", Position = 2,
                    DownloadStatus = "failed", PlaylistEntityId = "diag-pl",
                });
            db.SyncJobs.Add(new SyncJobEntity { Name = "Nightly" });
            db.ScanLocations.Add(new ScanLocationEntity { Path = "/music" });
            db.DownloaderSettings.Add(new DownloaderSettingEntity
            {
                ProviderId = "app", Key = "music_path", Value = "/music",
            });
            await db.SaveChangesAsync();
        }

        var counts = await _service.GetDatabaseCountsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(counts.Playlists, Is.EqualTo(1));
            Assert.That(counts.Tracks, Is.EqualTo(3));
            Assert.That(counts.Downloaded, Is.EqualTo(2));
            Assert.That(counts.Failed, Is.EqualTo(1));
            Assert.That(counts.SyncJobs, Is.EqualTo(1));
            Assert.That(counts.ScanLocations, Is.EqualTo(1));
            Assert.That(counts.Settings, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task GetSystemFactsAsync_ReadsTheRealFileAndStoredPaths()
    {
        await _settings.SetAsync("music_path", "/srv/music");
        await _settings.SetAsync("archive_path", "/srv/archive");

        var facts = await _service.GetSystemFactsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(facts.AppVersion, Is.Not.Empty);
            Assert.That(facts.Runtime, Is.Not.Empty);
            Assert.That(facts.OperatingSystem, Is.Not.Empty);
            Assert.That(facts.EnvironmentName, Is.EqualTo("Development"));
            Assert.That(facts.DatabasePath, Is.EqualTo(Path.GetFullPath(_dbPath)));
            Assert.That(facts.DatabaseBytes, Is.GreaterThan(0), "the SQLite file is on disk");
            Assert.That(facts.MusicPath, Is.EqualTo("/srv/music"));
            Assert.That(facts.ArchivePath, Is.EqualTo("/srv/archive"));
            Assert.That(facts.Uptime, Is.GreaterThanOrEqualTo(TimeSpan.Zero));
        });
    }

    [Test]
    public async Task ProbePluginsAsync_ReportsReadyUnavailableAndThrowingPlugins()
    {
        var ready = new ProbePlugin("ready", "Ready plugin", true);
        var unavailable = new ProbePlugin("unavailable", "Unavailable plugin", false, "missing API key");
        var throwing = new ProbePlugin("throwing", "Throwing plugin", false, error: new InvalidOperationException("socket refused"));
        _registry.Setup(r => r.GetAll()).Returns(new IDownloader[] { ready, unavailable, throwing });
        _registry.Setup(r => r.IsEnabled("unavailable")).Returns(false);
        _registry.Setup(r => r.IsEnabled("throwing")).Returns(true);

        var health = await _service.ProbePluginsAsync();

        Assert.That(health, Has.Count.EqualTo(3));
        var readyHealth = health.Single(h => h.Id == "ready");
        var unavailableHealth = health.Single(h => h.Id == "unavailable");
        var throwingHealth = health.Single(h => h.Id == "throwing");
        Assert.Multiple(() =>
        {
            Assert.That(readyHealth.Available, Is.True);
            Assert.That(readyHealth.Enabled, Is.True);
            Assert.That(readyHealth.Reason, Is.Null);

            Assert.That(unavailableHealth.Available, Is.False);
            Assert.That(unavailableHealth.Enabled, Is.False);
            Assert.That(unavailableHealth.Reason, Is.EqualTo("missing API key"));

            Assert.That(throwingHealth.Available, Is.False);
            Assert.That(throwingHealth.Reason, Does.Contain("socket refused"));
            Assert.That(health.All(h => h.ElapsedMs >= 0), Is.True);
        });
    }

    [Test]
    public async Task BuildReport_IncludesLiveFactsAndRedactsSecrets()
    {
        _logs.Log(LogLevel.Warn, "Downloads",
            "callback failed at https://example.com/cb?access_token=topsecret",
            detail: "retry with https://example.com/cb?api_key=secondsecret");
        var facts = await _service.GetSystemFactsAsync();
        var counts = await _service.GetDatabaseCountsAsync();
        var health = new[]
        {
            new DevPluginHealth("ready", "Ready plugin", true, true, null, 12),
            new DevPluginHealth("nope", "Off plugin", false, false, "not configured", 3),
        };

        var report = _service.BuildReport(facts, counts, health);

        Assert.Multiple(() =>
        {
            Assert.That(report, Does.Contain("MelodyBridge diagnostics"));
            Assert.That(report, Does.Contain(Path.GetFullPath(_dbPath)));
            Assert.That(report, Does.Contain("Tracks: 0 (0 downloaded, 0 failed)"));
            Assert.That(report, Does.Contain("Ready plugin"));
            Assert.That(report, Does.Contain("unavailable: not configured"));
            Assert.That(report, Does.Contain("access_token=***"));
            Assert.That(report, Does.Contain("api_key=***"));
            Assert.That(report, Does.Not.Contain("topsecret"));
            Assert.That(report, Does.Not.Contain("secondsecret"));
        });
    }

    private sealed class ProbePlugin : IDownloader
    {
        private readonly bool _available;
        private readonly Exception? _error;

        public ProbePlugin(string id, string name, bool available,
            string? reason = null, Exception? error = null)
        {
            Id = id;
            Name = name;
            _available = available;
            _error = error;
            LastUnavailableReason = reason;
        }

        public string Id { get; }
        public string Name { get; }
        public string? LastUnavailableReason { get; }

        public PluginCapabilities Capabilities => new(
        [
            new QualityDeliverable(AudioFormat.Mp3, Lossless: false, 64, 320),
        ]);

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
            => _error is null
                ? Task.FromResult(_available)
                : Task.FromException<bool>(_error);

        public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(null);

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
            => Task.FromResult(new DownloaderDownloadResult(false, null, "not used"));
    }
}
