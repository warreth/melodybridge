using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Server.Services;

public enum AttentionRepairState
{
    Running,
    Finished,
    Stopped,
    Failed,
}

/// <summary>One group of track ids handled by the same repair.</summary>
public sealed record AttentionRepairStep(
    AttentionFixAction Action, IReadOnlyList<int> TrackIds);

/// <summary>A playlist refresh that is part of the same repair run.</summary>
public sealed record AttentionRepairPlaylist(string Name, string SourceUrl);

/// <summary>The complete plan a Fix all click hands to the background job.</summary>
public sealed record AttentionRepairPlan(
    IReadOnlyList<AttentionRepairStep> Steps,
    IReadOnlyList<AttentionRepairPlaylist> Playlists)
{
    public int Total => Steps.Sum(step => step.TrackIds.Count) + Playlists.Count;
}

/// <summary>The live state of the current or last repair run.</summary>
public sealed record AttentionRepairRun(
    int Total,
    int Done,
    int Failed,
    int Processed,
    string Current,
    AttentionRepairState State,
    DateTime StartedAtUtc,
    DateTime? FinishedAtUtc,
    DateTime? EtaUtc,
    string? Summary,
    IReadOnlyList<string> Messages)
{
    public int Percent => Total <= 0
        ? 0
        : Math.Clamp(100 * Processed / Total, 0, 100);

    public static DateTime? ComputeEta(DateTime startedAtUtc, int processed, int remaining)
    {
        if (processed <= 0 || remaining <= 0) return null;
        var elapsed = DateTime.UtcNow - startedAtUtc;
        return DateTime.UtcNow + elapsed / processed * remaining;
    }
}

public interface IAttentionRepairRunner
{
    event Action? Changed;

    AttentionRepairRun? Current { get; }

    bool IsRunning { get; }

    bool Start(AttentionRepairPlan plan);

    void Stop();
}

/// <summary>
/// Runs one Needs attention repair in the background. The job owns its own
/// scope and cancellation, so leaving the page never stops it. Downloads and
/// the attention page read the same snapshot and show the ETA.
/// </summary>
public sealed class AttentionRepairService : IAttentionRepairRunner, IDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AttentionRepairService> _logger;
    private readonly object _gate = new();
    private AttentionRepairRun? _current;
    private CancellationTokenSource? _cancellation;
    private Task? _task;

    public AttentionRepairService(
        IServiceScopeFactory scopes,
        ILogger<AttentionRepairService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public event Action? Changed;

    public AttentionRepairRun? Current
    {
        get { lock (_gate) return _current; }
    }

    public bool IsRunning => Current is { State: AttentionRepairState.Running };

    public bool Start(AttentionRepairPlan plan)
    {
        if (plan.Total <= 0) return false;

        lock (_gate)
        {
            if (_current is { State: AttentionRepairState.Running }) return false;
            _current = new AttentionRepairRun(
                plan.Total, 0, 0, 0, "Starting", AttentionRepairState.Running,
                DateTime.UtcNow, null, null, null, Array.Empty<string>());
            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _task = Task.Run(() => RunAsync(plan, token));
        }

        Changed?.Invoke();
        return true;
    }

    public void Stop()
    {
        lock (_gate) _cancellation?.Cancel();
    }

    private async Task RunAsync(AttentionRepairPlan plan, CancellationToken ct)
    {
        var done = 0;
        var failed = 0;
        var processed = 0;
        var messages = new List<string>();
        try
        {
            using var scope = _scopes.CreateScope();
            var actions = scope.ServiceProvider.GetRequiredService<IAttentionActions>();
            PlaylistStore? store = null;

            foreach (var step in plan.Steps)
            {
                ct.ThrowIfCancellationRequested();
                var label = Label(step.Action);
                Update(run => run with
                {
                    Current = label,
                    EtaUtc = AttentionRepairRun.ComputeEta(
                        run.StartedAtUtc, processed, run.Total - processed),
                });

                var stepOffset = processed;
                var progress = new InlineProgress(value =>
                {
                    var nextProcessed = Math.Clamp(stepOffset + value, 0, plan.Total);
                    Update(run => run with
                    {
                        Processed = nextProcessed,
                        EtaUtc = AttentionRepairRun.ComputeEta(
                            run.StartedAtUtc, nextProcessed, run.Total - nextProcessed),
                    });
                });

                var result = await RunStepAsync(actions, step, progress, ct);
                done += result.Done;
                failed += result.Failed;
                messages.AddRange(result.Messages);
                processed = stepOffset + step.TrackIds.Count;
                Update(run => run with
                {
                    Done = done,
                    Failed = failed,
                    Processed = processed,
                    EtaUtc = AttentionRepairRun.ComputeEta(
                        run.StartedAtUtc, processed, run.Total - processed),
                });
            }

            if (plan.Playlists.Count > 0)
                store = scope.ServiceProvider.GetRequiredService<PlaylistStore>();

            foreach (var playlist in plan.Playlists)
            {
                ct.ThrowIfCancellationRequested();
                Update(run => run with { Current = "Re-syncing " + playlist.Name });
                try
                {
                    await store!.AddOrRefreshAsync(playlist.SourceUrl, ct: ct);
                    done++;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    messages.Add($"{playlist.Name}: {ex.Message}");
                }
                processed++;
                Update(run => run with
                {
                    Done = done,
                    Failed = failed,
                    Processed = processed,
                    EtaUtc = AttentionRepairRun.ComputeEta(
                        run.StartedAtUtc, processed, run.Total - processed),
                });
            }

            Finish(AttentionRepairState.Finished, done, failed, processed, messages, null);
        }
        catch (OperationCanceledException)
        {
            Finish(AttentionRepairState.Stopped, done, failed, processed, messages, "The action was stopped.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Needs attention repair failed");
            Finish(AttentionRepairState.Failed, done, failed, processed, messages, ex.Message);
        }
    }

    private static Task<AttentionBulkResult> RunStepAsync(
        IAttentionActions actions, AttentionRepairStep step,
        IProgress<int> progress, CancellationToken ct)
        => step.Action switch
        {
            AttentionFixAction.Retry => actions.RetryAsync(step.TrackIds, false, progress, ct),
            AttentionFixAction.Keep => actions.QualityReviewAsync(
                step.TrackIds, QualityReviewAction.Keep, progress, ct),
            AttentionFixAction.RetryReview => actions.QualityReviewAsync(
                step.TrackIds, QualityReviewAction.Retry, progress, ct),
            AttentionFixAction.Reverify => actions.ReverifyAsync(step.TrackIds, progress, ct),
            AttentionFixAction.Hardlink => actions.DedupAsync(step.TrackIds, true, progress, ct),
            AttentionFixAction.Archive => actions.ArchiveAsync(step.TrackIds, progress, ct),
            _ => Task.FromResult(new AttentionBulkResult(0, 0, new List<string>())),
        };

    private void Update(Func<AttentionRepairRun, AttentionRepairRun> change)
    {
        lock (_gate)
        {
            if (_current is not { State: AttentionRepairState.Running }) return;
            _current = change(_current);
        }
        Changed?.Invoke();
    }

    private void Finish(
        AttentionRepairState state, int done, int failed, int processed,
        IReadOnlyList<string> messages, string? summary)
    {
        lock (_gate)
        {
            if (_current is null) return;
            _current = _current with
            {
                State = state,
                Done = done,
                Failed = failed,
                Processed = processed,
                Current = "",
                FinishedAtUtc = DateTime.UtcNow,
                EtaUtc = null,
                Summary = summary ?? (failed == 0
                    ? $"{done} item{(done == 1 ? "" : "s")} handled."
                    : $"{done} handled, {failed} still need attention."),
                Messages = messages.ToList(),
            };
            _cancellation?.Dispose();
            _cancellation = null;
            _task = null;
        }
        Changed?.Invoke();
    }

    private static string Label(AttentionFixAction action) => action switch
    {
        AttentionFixAction.Retry => "Retrying downloads",
        AttentionFixAction.Keep => "Keeping reviewed files",
        AttentionFixAction.RetryReview => "Retrying quality reviews",
        AttentionFixAction.Reverify => "Rechecking unverified files",
        AttentionFixAction.Hardlink => "Hardlinking duplicates",
        AttentionFixAction.Archive => "Rebuilding archive copies",
        _ => "Fixing attention items",
    };

    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    public void Dispose() => Stop();
}
