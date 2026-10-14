using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server.Services;

/// <summary>
/// The failure sink routes one serious background failure to the toast
/// bus and to ntfy, each with its own gate: the failure toast toggle and
/// the "failures" ntfy category. A failing topic must never escape.
/// </summary>
[TestFixture]
public class BackgroundFailureSinkTests
{
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;
    private SettingsStore _settings = null!;

    [SetUp]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-sink-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        _settings = new SettingsStore(_factory);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
        }
    }

    /// <summary>Records pushes instead of hitting a topic.</summary>
    private sealed class RecordingNtfy : NtfyNotifier
    {
        public List<(string Category, string Message, string Level)> Sent { get; } = new();

        public RecordingNtfy() : base(null!, null!, NullLogger<NtfyNotifier>.Instance) { }

        public override Task NotifyAsync(string category, string message, string level, CancellationToken ct = default)
        {
            Sent.Add((category, message, level));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingNtfy : NtfyNotifier
    {
        public ThrowingNtfy() : base(null!, null!, NullLogger<NtfyNotifier>.Instance) { }

        public override Task NotifyAsync(string category, string message, string level, CancellationToken ct = default)
            => throw new InvalidOperationException("topic down");
    }

    private static NotificationService NewBus()
        => new(autoDismissAfter: TimeSpan.FromHours(1));

    [Test]
    public async Task Report_ToastsAndPushesByDefault()
    {
        var bus = NewBus();
        var ntfy = new RecordingNtfy();
        var sink = new BackgroundFailureSink(_settings, bus, ntfy, NullLogger<BackgroundFailureSink>.Instance);

        await sink.ReportAsync("Auto-sync failed for 'Mix': boom");

        Assert.Multiple(() =>
        {
            Assert.That(bus.Snapshot().Select(n => n.Message),
                Does.Contain("Auto-sync failed for 'Mix': boom"), "the toast shows the failure");
            Assert.That(ntfy.Sent, Has.Count.EqualTo(1));
            Assert.That(ntfy.Sent[0].Category, Is.EqualTo(NotificationCategories.Failures));
            Assert.That(ntfy.Sent[0].Level, Is.EqualTo("warn"));
        });
    }

    [Test]
    public async Task Report_FailureToggleOff_SkipsTheToast_ButNotNtfy()
    {
        await _settings.SetAsync("notify_failures", "false");
        var bus = NewBus();
        var ntfy = new RecordingNtfy();
        var sink = new BackgroundFailureSink(_settings, bus, ntfy, NullLogger<BackgroundFailureSink>.Instance);

        await sink.ReportAsync("Scheduled scan failed for '/music': boom");

        Assert.Multiple(() =>
        {
            Assert.That(bus.Snapshot(), Is.Empty, "the toast toggle gates the toast");
            Assert.That(ntfy.Sent, Has.Count.EqualTo(1),
                "ntfy categories are independent of the toast toggles");
        });
    }

    [Test]
    public async Task Report_AFailingTopic_NeverThrows()
    {
        var bus = NewBus();
        var sink = new BackgroundFailureSink(
            _settings, bus, new ThrowingNtfy(), NullLogger<BackgroundFailureSink>.Instance);

        Assert.DoesNotThrowAsync(() => sink.ReportAsync("Download run crashed: boom"));
    }
}
