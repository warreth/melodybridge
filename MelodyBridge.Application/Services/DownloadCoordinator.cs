using System.Collections.Concurrent;
using System.Linq.Expressions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Application.Services;

/// <summary>State of one playlist download run.</summary>
public enum DownloadRunState
{
    Running,
    Paused,
    Cancelling,
    Finished,
}

/// <summary>One playlist download run, observable by the UI.</summary>
public record DownloadRun(
    string PlaylistId,
    string PlaylistName,
    int Total,
    int Done,
    int Failed,
    string? CurrentTrack,
    string? CurrentPlugin,
    DownloadRunState State,
    DateTime StartedAtUtc,
    int QueueLength = 0,
    DateTime? EtaUtc = null)
{
    public int Percent => Total == 0 ? 0 : Math.Min(100, (Done + Failed) * 100 / Total);

    /// <summary>Estimated finish from the average pace so far; null before
    /// the first track completes (no measured pace yet).</summary>
    public static DateTime? ComputeEta(DateTime startedAtUtc, int completed, int remaining)
    {
        if (completed <= 0 || remaining <= 0) return null;
        var elapsed = DateTime.UtcNow - startedAtUtc;
        var perTrack = elapsed / completed;
        return DateTime.UtcNow + perTrack * remaining;
    }
}

/// <summary>
/// Runs playlist downloads in the background with pause/resume/cancel and
/// a live snapshot any page can poll. One run per playlist.
///
/// Pause is cooperative: the runner waits on a gate between tracks, so the
/// in-flight track always finishes and no half files are left behind.
/// </summary>
public class DownloadCoordinator : IDownloadTrigger
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DownloadCoordinator> _logger;

    /// <summary>
    /// The run's fixed work list with an atomically shared cursor: every
    /// pending/failed track is attempted exactly once per run, so a
    /// permanently failing track can never spin the run forever. A taken
    /// item stays "remaining" until its worker marks it complete, so the
    /// visible queue and ETA count in-flight tracks too.
    /// </summary>
    private sealed class WorkQueue
    {
        private readonly List<(int Id, string Title)> _items;
        private readonly int[] _completed;
        private int _taken;

        public WorkQueue(List<(int Id, string Title)> items)
        {
            _items = items;
            _completed = new int[items.Count];
        }

        public int TotalCount => _items.Count;

        /// <summary>Atomically takes the next item; false when exhausted.</summary>
        public bool TryTake(out int index, out (int Id, string Title) item)
        {
            var i = Interlocked.Increment(ref _taken) - 1;
            if (i < _items.Count)
            {
                index = i;
                item = _items[i];
                return true;
            }
            index = -1;
            item = default;
            return false;
        }

        /// <summary>Marks a taken item as attempted (downloaded, failed or skipped).</summary>
        public void Complete(int index)
        {
            if (index >= 0 && index < _completed.Length)
                Volatile.Write(ref _completed[index], 1);
        }

        /// <summary>Titles not yet completed, in Position order: the visible queue.</summary>
        public List<string> RemainingTitles()
        {
            var titles = new List<string>();
            for (var i = 0; i < _items.Count; i++)
                if (Volatile.Read(ref _completed[i]) == 0)
                    titles.Add(_items[i].Title);
            return titles;
        }
    }

    private sealed class RunHandle
    {
        public Task Task = Task.CompletedTask;
        public CancellationTokenSource Cts = new();
        public ManualResetEventSlim Gate = new(true);
        public DownloadRun Snapshot = null!;
        /// <summary>Workers currently mid-track; keeps CurrentTrack in the snapshot honest with parallel workers.</summary>
        public int InFlight;
        /// <summary>Ordered titles of the run's remaining work (Position
        /// order), read by the UI.</summary>
        public List<string> QueueTitles = new();
        /// <summary>This run's work queue; replaced at every run start.</summary>
        public WorkQueue Work = new(new List<(int, string)>());
    }

    private readonly ConcurrentDictionary<string, RunHandle> _runs = new();

    /// <summary>
    /// The work predicate: never downloaded, pending or failed. Shared by
    /// the run's work list and the pending check the auto-sync scheduler
    /// asks, so both always agree on what "work" means.
    /// </summary>
    private static readonly Expression<Func<TrackEntity, bool>> PendingWork =
        t => t.DownloadStatus == null || t.DownloadStatus == "pending" || t.DownloadStatus == "failed";

    public DownloadCoordinator(
        IServiceProvider services,
        ILogger<DownloadCoordinator> logger)
    {
        _services = services;
        _logger = logger;
    }

    /// <summary>
    /// Scoped services cannot be captured by this singleton, so each run
    /// creates its own scope and resolves a fresh PlaylistStore from it.
    /// The caller disposes the scope when the run ends.
    /// </summary>
    private static PlaylistStore StoreFrom(IServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<PlaylistStore>();

    private async Task<MelodyBridgeDbContext> NewDbAsync(CancellationToken ct = default)
        => await _services.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>()
            .CreateDbContextAsync(ct);

    /// <summary>All live runs, newest first. Finished runs stay visible
    /// until the next run starts or the app restarts.</summary>
    public IReadOnlyList<DownloadRun> Snapshot()
        => _runs.Values.Select(h => h.Snapshot)
            .OrderByDescending(r => r.StartedAtUtc)
            .ToList();

    public DownloadRun? RunFor(string playlistId)
        => _runs.TryGetValue(playlistId, out var h) ? h.Snapshot : null;

    /// <summary>Ordered titles of the live run's remaining work (Position
    /// order) for this playlist: the visible download queue.</summary>
    public IReadOnlyList<string> QueueFor(string playlistId)
        => _runs.TryGetValue(playlistId, out var h) ? h.QueueTitles : Array.Empty<string>();

    public bool IsActive(string playlistId)
        => _runs.TryGetValue(playlistId, out var h)
            && h.Snapshot.State is DownloadRunState.Running or DownloadRunState.Paused or DownloadRunState.Cancelling;

    /// <summary>True when the playlist still has tracks that need a download.</summary>
    public async Task<bool> HasPendingWorkAsync(string playlistId, CancellationToken ct = default)
    {
        await using var db = await NewDbAsync(ct);
        return await db.Tracks.AsNoTracking()
            .Where(t => t.PlaylistEntityId == playlistId)
            .Where(PendingWork)
            .AnyAsync(ct);
    }

    /// <summary>Starts (or resumes) a run. No-ops while one is already active.</summary>
    public void Start(string playlistId)
    {
        var handle = _runs.GetOrAdd(playlistId, id =>
        {
            var h = new RunHandle();
            h.Snapshot = new DownloadRun(id, string.Empty, 0, 0, 0, null, null,
                DownloadRunState.Running, DateTime.UtcNow);
            h.Task = Task.Run(() => RunAsync(id, h));
            return h;
        });

        if (handle.Snapshot.State == DownloadRunState.Paused)
        {
            handle.Snapshot = handle.Snapshot with { State = DownloadRunState.Running };
            handle.Gate.Set();
            _logger.LogInformation("Download run for {Playlist} resumed", playlistId);
        }
        else if (handle.Snapshot.State == DownloadRunState.Finished)
        {
            handle.Cts.Dispose();
            handle.Cts = new CancellationTokenSource();
            handle.Gate.Dispose();
            handle.Gate = new ManualResetEventSlim(true);
            handle.Snapshot = new DownloadRun(playlistId, handle.Snapshot.PlaylistName,
                0, 0, 0, null, null, DownloadRunState.Running, DateTime.UtcNow);
            handle.Task = Task.Run(() => RunAsync(playlistId, handle));
        }
    }

    public void Pause(string playlistId)
    {
        if (_runs.TryGetValue(playlistId, out var h)
            && h.Snapshot.State == DownloadRunState.Running)
        {
            h.Gate.Reset();
            h.Snapshot = h.Snapshot with { State = DownloadRunState.Paused };
            _logger.LogInformation("Download run for {Playlist} paused", playlistId);
        }
    }

    /// <summary>Cooperative cancel: the in-flight track completes, then the run stops.</summary>
    public void Cancel(string playlistId)
    {
        if (_runs.TryGetValue(playlistId, out var h)
            && h.Snapshot.State is DownloadRunState.Running or DownloadRunState.Paused)
        {
            h.Snapshot = h.Snapshot with { State = DownloadRunState.Cancelling };
            h.Gate.Set(); // unblock the pause gate so cancellation proceeds
            h.Cts.Cancel();
            _logger.LogInformation("Download run for {Playlist} cancelled", playlistId);
        }
    }

    /// <summary>
    /// Max tracks downloaded in parallel per run. Read once per run from
    /// the download_max_concurrent setting (default 2); 1 keeps the old
    /// sequential behavior.
    /// </summary>
    private async Task<int> MaxConcurrentAsync(CancellationToken ct)
    {
        try
        {
            var settings = _services.GetRequiredService<SettingsStore>();
            var raw = await settings.GetAsync("download_max_concurrent", "2", ct);
            return Math.Clamp(int.TryParse(raw, out var n) ? n : 2, 1, 8);
        }
        catch
        {
            return 2; // settings unavailable (tests without the store): sane default
        }
    }

    private async Task RunAsync(string playlistId, RunHandle handle)
    {
        var ct = handle.Cts.Token;
        using var runScope = _services.CreateScope();
        var store = StoreFrom(runScope);
        try
        {
            var name = await LoadNameAsync(playlistId) ?? playlistId;
            var counts = await CountAsync(playlistId);
            handle.Snapshot = handle.Snapshot with
            {
                PlaylistName = name,
                Total = counts.total,
                Done = counts.done,
                Failed = counts.failed,
            };
            handle.Work = new WorkQueue(await LoadWorkItemsAsync(playlistId, ct));
            RefreshQueue(handle);

            var workers = await MaxConcurrentAsync(ct);
            _logger.LogInformation("Download run for {Playlist} starting with {Workers} workers over {Count} tracks",
                playlistId, workers, handle.Work.TotalCount);

            // Each worker claims the next work item from the shared cursor
            // (atomic) and downloads exactly that track: the store's claim
            // still guards against a concurrent single-track click. The
            // shared pause gate holds every worker between tracks.
            var tasks = Enumerable.Range(0, workers)
                .Select(_ => WorkerAsync(handle, store, ct))
                .ToArray();
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // expected on cancel
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Download run for {Playlist} crashed", playlistId);
            if (runScope.ServiceProvider.GetService<IBackgroundFailureSink>() is { } failures)
            {
                var name = handle.Snapshot.PlaylistName is { Length: > 0 } n ? n : playlistId;
                await failures.ReportAsync($"Download run for '{name}' crashed: {ex.Message}");
            }
        }
        finally
        {
            var finished = handle.Snapshot with
            {
                State = DownloadRunState.Finished,
                CurrentTrack = null,
                CurrentPlugin = null,
            };
            handle.Snapshot = finished;
            _logger.LogInformation(
                "Download run for {Playlist} finished: {Done} of {Total} done, {Failed} failed",
                playlistId, finished.Done, finished.Total, finished.Failed);
        }
    }

    /// <summary>One download worker loop: take the next work item, download it, repeat.</summary>
    private async Task WorkerAsync(RunHandle handle, PlaylistStore store, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            handle.Gate.Wait(ct); // cooperative pause between tracks
            if (ct.IsCancellationRequested) break;

            if (!handle.Work.TryTake(out var workIndex, out var work)) break; // work list exhausted
            var (trackId, title) = work;
            Interlocked.Increment(ref handle.InFlight);
            handle.Snapshot = handle.Snapshot with
            {
                CurrentTrack = title,
                State = DownloadRunState.Running,
            };

            try
            {
                await store.DownloadTrackAsync(trackId, ct);
            }
            catch (OperationCanceledException)
            {
                throw; // the run is cancelling: stop this worker
            }
            finally
            {
                // The item was attempted (or the run is ending): it leaves
                // the visible queue either way.
                handle.Work.Complete(workIndex);
                Interlocked.Decrement(ref handle.InFlight);
            }

            var counts = await CountAsync(handle.Snapshot.PlaylistId);
            var snapshot = handle.Snapshot with
            {
                Done = counts.done,
                Failed = counts.failed,
            };
            if (Volatile.Read(ref handle.InFlight) == 0)
                snapshot = snapshot with { CurrentTrack = null };
            handle.Snapshot = snapshot;

            // The queue and the ETA move as tracks complete: one refresh per
            // finished track keeps both honest without extra polling.
            RefreshQueue(handle);
        }
    }

    /// <summary>
    /// Recomputes the visible queue (the work items not yet taken, in
    /// Position order) and folds it into the snapshot: queue length plus
    /// an ETA derived from the average pace of the tracks completed so far.
    /// </summary>
    private static void RefreshQueue(RunHandle handle)
    {
        handle.QueueTitles = handle.Work.RemainingTitles();

        var s = handle.Snapshot;
        handle.Snapshot = s with
        {
            QueueLength = handle.QueueTitles.Count,
            EtaUtc = DownloadRun.ComputeEta(s.StartedAtUtc, s.Done + s.Failed, handle.QueueTitles.Count),
        };
    }

    private async Task<string?> LoadNameAsync(string playlistId)
    {
        await using var db = await NewDbAsync();
        return await db.Playlists.AsNoTracking()
            .Where(p => p.Id == playlistId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync();
    }

    /// <summary>Live (total, done, failed) counts for the playlist.</summary>
    private async Task<(int total, int done, int failed)> CountAsync(string playlistId)
    {
        await using var db = await NewDbAsync();
        var tracks = await db.Tracks.AsNoTracking()
            .Where(t => t.PlaylistEntityId == playlistId)
            .Select(t => t.DownloadStatus)
            .ToListAsync();
        return (tracks.Count,
            tracks.Count(s => s == "downloaded"),
            tracks.Count(s => s == "failed"));
    }

    /// <summary>
    /// This run's work list: every pending/failed track, in playlist
    /// Position order. Snapshot at run start so each track is attempted
    /// exactly once per run.
    /// </summary>
    private async Task<List<(int Id, string Title)>> LoadWorkItemsAsync(string playlistId, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);
        return await db.Tracks.AsNoTracking()
            .Where(t => t.PlaylistEntityId == playlistId)
            .Where(PendingWork)
            .OrderBy(t => t.Position)
            .Select(t => new ValueTuple<int, string>(t.Id, t.Title ?? ""))
            .ToListAsync(ct);
    }
}
