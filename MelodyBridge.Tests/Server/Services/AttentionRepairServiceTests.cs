using MelodyBridge.Core;
using MelodyBridge.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server.Services;

/// <summary>
/// The repair runner owns the work after the page hands it over. It must
/// survive navigation, report progress and ETA, and stop only when asked.
/// </summary>
[TestFixture]
public class AttentionRepairServiceTests
{
    private sealed class BlockingActions : IAttentionActions
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Cancelled { get; private set; }

        private async Task<AttentionBulkResult> RunAsync(
            IReadOnlyList<int> ids, IProgress<int>? progress, CancellationToken ct)
        {
            Started.TrySetResult();
            progress?.Report(Math.Min(1, ids.Count));
            try
            {
                await Release.Task.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                Cancelled = true;
                throw;
            }
            return new AttentionBulkResult(ids.Count, 0, new List<string>());
        }

        public Task<AttentionBulkResult> RetryAsync(
            IReadOnlyList<int> trackIds, bool avoidLastProvider,
            IProgress<int>? progress = null, CancellationToken ct = default)
            => RunAsync(trackIds, progress, ct);

        public Task<AttentionBulkResult> HardlinkAsync(
            IReadOnlyList<int> trackIds,
            IProgress<int>? progress = null, CancellationToken ct = default)
            => RunAsync(trackIds, progress, ct);

        public Task<AttentionBulkResult> ArchiveAsync(
            IReadOnlyList<int> trackIds,
            IProgress<int>? progress = null, CancellationToken ct = default)
            => RunAsync(trackIds, progress, ct);

        public Task<AttentionBulkResult> QualityReviewAsync(
            IReadOnlyList<int> trackIds, QualityReviewAction action,
            IProgress<int>? progress = null, CancellationToken ct = default)
            => RunAsync(trackIds, progress, ct);

        public Task<AttentionBulkResult> ReverifyAsync(
            IReadOnlyList<int> trackIds,
            IProgress<int>? progress = null, CancellationToken ct = default)
            => RunAsync(trackIds, progress, ct);

        public Task<AttentionBulkResult> DedupAsync(
            IReadOnlyList<int> trackIds, bool hardlinkExisting,
            IProgress<int>? progress = null, CancellationToken ct = default)
            => RunAsync(trackIds, progress, ct);
    }

    private static AttentionRepairService Service(IAttentionActions actions)
    {
        var services = new ServiceCollection();
        services.AddSingleton(actions);
        var provider = services.BuildServiceProvider();
        return new AttentionRepairService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AttentionRepairService>.Instance);
    }

    private static AttentionRepairPlan Plan(params int[] ids)
        => new(
            new[]
            {
                new AttentionRepairStep(AttentionFixAction.Retry, ids),
            },
            Array.Empty<AttentionRepairPlaylist>());

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.That(condition(), Is.True, "the repair did not reach the expected state");
    }

    [Test]
    public async Task Run_ReportsRunningProgressAndEta()
    {
        var actions = new BlockingActions();
        var service = Service(actions);

        Assert.That(service.Start(Plan(1, 2)), Is.True);
        await actions.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await WaitForAsync(() => service.Current is
        {
            State: AttentionRepairState.Running,
            Processed: 1,
            EtaUtc: not null,
        });
        Assert.That(service.Current!.Current, Is.EqualTo("Retrying downloads"));

        actions.Release.TrySetResult();
        await WaitForAsync(() => service.Current is { State: AttentionRepairState.Finished });
        Assert.Multiple(() =>
        {
            Assert.That(service.Current!.Done, Is.EqualTo(2));
            Assert.That(service.Current.Percent, Is.EqualTo(100));
            Assert.That(service.Current.Summary, Does.Contain("2 items handled"));
        });
    }

    [Test]
    public async Task Stop_CancelsTheOwnedWork()
    {
        var actions = new BlockingActions();
        var service = Service(actions);

        Assert.That(service.Start(Plan(1)), Is.True);
        await actions.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        service.Stop();
        await WaitForAsync(() => service.Current is { State: AttentionRepairState.Stopped });

        Assert.That(actions.Cancelled, Is.True);
        Assert.That(service.IsRunning, Is.False);
    }

    [Test]
    public async Task Start_WhileRunning_IsRejected()
    {
        var actions = new BlockingActions();
        var service = Service(actions);

        Assert.That(service.Start(Plan(1)), Is.True);
        await actions.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(service.Start(Plan(2)), Is.False,
            "two Fix all clicks must not run two repairs");

        service.Stop();
        await WaitForAsync(() => service.Current is { State: AttentionRepairState.Stopped });
    }

    [Test]
    public void Start_WithAnEmptyPlan_IsRejected()
    {
        var service = Service(new BlockingActions());
        Assert.That(service.Start(Plan()), Is.False);
    }
}
