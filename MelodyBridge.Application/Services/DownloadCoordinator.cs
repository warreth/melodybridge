using System.Collections.Concurrent;
using System.Linq.Expressions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Application.Services;

/// <summary>One queued item of a running download: the row id (the
/// queue may hold the same title twice) and the title for display.</summary>
public sealed record QueuedItem(int Id, string Title);

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
    /// <summary>
    /// Progress from the work queue: rows that are no longer in the queue
    /// are done, whether they became downloaded, failed, needs-review or
    /// skipped. Adding failures to the done count read like a full bar
    /// while the last track was still downloading, and it drifted away
    /// from the page that counts the queue honestly.
    /// </summary>
    public int Percent
    {
        get
        {
            if (Total <= 0) return 0;
            var remaining = Math.Max(0, Math.Min(QueueLength, Total));
            return Math.Clamp(100 * (Total - remaining) / Total, 0, 100);
        }
    }

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
public class DownloadCoordinator : IDownloadCoordinator
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DownloadCoordinator> _logger;
    private readonly ITrackClaimRegistry _claims;

    /// <summary>
    /// The run's fixed work list with an atomically shared cursor: every
    /// pending/failed track is attempted exactly once per run, so a
    /// permanently failing track can never spin the run forever. A taken
    /// item stays "remaining" until its worker marks it complete, so the
    /// visible queue and ETA count in-flight tracks too.
    /// </summary>
    private sealed class WorkQueue
    {
        private readonly List<(int Id, string Title, string? Status)> _items;
        private readonly int[] _completed;
        private readonly object _cacheLock = new();
        private int _taken;
        private int _remaining;
        private int _version;
        private List<QueuedItem>? _cachedWork;
        private int _cachedVersion = -1;

        public WorkQueue(List<(int Id, string Title, string? Status)> items)
        {
            _items = items;
            _completed = new int[items.Count];
            _remaining = items.Count;
        }

        public int TotalCount => _items.Count;

        /// <summary>Taken and completed items removed, in-flight ones still counted.</summary>
        public int RemainingCount => Volatile.Read(ref _remaining);

        /// <summary>Atomically takes the next item; false when exhausted.</summary>
        public bool TryTake(out int index, out (int Id, string Title, string? Status) item)
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

        /// <summary>
        /// Marks a taken item as attempted (downloaded, failed or skipped).
        /// Idempotent, so a worker that leaves through a cancellation path
        /// cannot decrement the queue twice.
        /// </summary>
        public void Complete(int index)
        {
            if (index < 0 || index >= _completed.Length) return;
            if (Interlocked.Exchange(ref _completed[index], 1) == 0)
            {
                Interlocked.Decrement(ref _remaining);
                Interlocked.Increment(ref _version);
            }
        }

        /// <summary>
        /// Queued work not yet completed, in Position order: the visible
        /// queue, keyed by row (a playlist may list the same title twice)
        /// with the title for immediate display. The list is cached until
        /// another item completes; a run with a thousand tracks must not
        /// rebuild and allocate it after every single finished track.
        /// </summary>
        public IReadOnlyList<QueuedItem> RemainingWork()
        {
            var version = Volatile.Read(ref _version);
            lock (_cacheLock)
            {
                if (_cachedWork is { } cached && _cachedVersion == version)
                    return cached;

                var work = new List<QueuedItem>(_remaining);
                for (var i = 0; i < _items.Count; i++)
                    if (Volatile.Read(ref _completed[i]) == 0)
                        work.Add(new QueuedItem(_items[i].Id, _items[i].Title));
                _cachedWork = work;
                _cachedVersion = version;
                return work;
            }
        }

        /// <summary>Queue titles not yet completed, in Position order: the
        /// visible queue for callers that show words only.</summary>
        public IReadOnlyList<string> RemainingTitles()
            => [.. RemainingWork().Select(item => item.Title)];
    }

    private sealed class RunHandle
    {
        public Task Task = Task.CompletedTask;
        public CancellationTokenSource Cts = new();
        public ManualResetEventSlim Gate = new(true);
        public DownloadRun Snapshot = null!;
        /// <summary>Guards the snapshot and the counters that feed it.</summary>
        public readonly object Sync = new();
        /// <summary>Workers currently mid-track; keeps CurrentTrack in the snapshot honest with parallel workers.</summary>
        public int InFlight;
        /// <summary>This run's best-known progress. Reconciled with the
        /// database when the run ends.</summary>
        public int Done;
        public int Failed;
        /// <summary>This run's work queue; replaced at every run start.</summary>
        public WorkQueue Work = new(new List<(int, string, string?)>());
    }

    private readonly ConcurrentDictionary<string, RunHandle> _runs = new();
    private readonly object _startLock = new();

    /// <summary>
    /// The work predicate: never downloaded, pending or failed. Shared by
    /// the run's work list and the pending check the auto-sync scheduler
    /// asks, so both always agree on what "work" means.
    /// </summary>
    private static readonly Expression<Func<TrackEntity, bool>> PendingWork =
        t => t.DownloadStatus == null || t.DownloadStatus == "pending" || t.DownloadStatus == "failed";

    public DownloadCoordinator(
        IServiceProvider services,
        ILogger<DownloadCoordinator> logger,
        ITrackClaimRegistry? claims = null)
    {
        _services = services;
        _logger = logger;
        // The claims that are live in this process. A run that starts
        // while a single-track download is running must leave that claim
        // alone: the download is alive, not a leftover from a crash.
        _claims = claims ?? new LiveTrackClaims();
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

    /// <summary>Ordered work of the live run's remaining queue (Position
    /// order) for this playlist: row id and title, keyed by row, so a
    /// playlist that lists the same title twice label each of its rows
    /// instead of both sitting in the first slot.</summary>
    public IReadOnlyList<QueuedItem> QueueFor(string playlistId)
        => _runs.TryGetValue(playlistId, out var h) ? h.Work.RemainingWork() : Array.Empty<QueuedItem>();

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
        // The dictionary factory must have no side effects: when two
        // callers raced, GetOrAdd could start a run and then discard it,
        // leaving an untracked worker behind. One lock around the small
        // state transition is simpler and exact.
        lock (_startLock)
        {
            if (!_runs.TryGetValue(playlistId, out var handle))
            {
                handle = new RunHandle();
                lock (handle.Sync)
                {
                    handle.Snapshot = new DownloadRun(playlistId, string.Empty, 0, 0, 0, null, null,
                        DownloadRunState.Running, DateTime.UtcNow);
                }
                _runs[playlistId] = handle;
                handle.Task = Task.Run(() => RunAsync(playlistId, handle));
                return;
            }

            if (handle.Snapshot.State == DownloadRunState.Paused)
            {
                lock (handle.Sync)
                {
                    handle.Snapshot = handle.Snapshot with { State = DownloadRunState.Running };
                }
                handle.Gate.Set();
                _logger.LogInformation("Download run for {Playlist} resumed", playlistId);
            }
            else if (handle.Snapshot.State == DownloadRunState.Finished)
            {
                var name = handle.Snapshot.PlaylistName;
                handle.Cts.Dispose();
                handle.Cts = new CancellationTokenSource();
                handle.Gate.Dispose();
                handle.Gate = new ManualResetEventSlim(true);
                lock (handle.Sync)
                {
                    handle.InFlight = 0;
                    handle.Done = 0;
                    handle.Failed = 0;
                    handle.Work = new WorkQueue(new List<(int, string, string?)>());
                    handle.Snapshot = new DownloadRun(playlistId, name,
                        0, 0, 0, null, null, DownloadRunState.Running, DateTime.UtcNow);
                }
                handle.Task = Task.Run(() => RunAsync(playlistId, handle));
            }
        }
    }

    public void Pause(string playlistId)
    {
        if (!_runs.TryGetValue(playlistId, out var h)) return;
        lock (h.Sync)
        {
            if (h.Snapshot.State != DownloadRunState.Running) return;
            h.Gate.Reset();
            h.Snapshot = h.Snapshot with { State = DownloadRunState.Paused };
        }
        _logger.LogInformation("Download run for {Playlist} paused", playlistId);
    }

    /// <summary>Cooperative cancel: the in-flight track completes, then the run stops.</summary>
    public void Cancel(string playlistId)
    {
        if (!_runs.TryGetValue(playlistId, out var h)) return;
        lock (h.Sync)
        {
            if (h.Snapshot.State is not (DownloadRunState.Running or DownloadRunState.Paused)) return;
            h.Snapshot = h.Snapshot with { State = DownloadRunState.Cancelling };
        }
        h.Gate.Set(); // unblock the pause gate so cancellation proceeds
        h.Cts.Cancel();
        _logger.LogInformation("Download run for {Playlist} cancelled", playlistId);
    }

    /// <summary>
    /// A new run may be started after an earlier run died without clearing
    /// its claim. Those rows are stale and go back to pending. Without
    /// this, a redownload can find no claimable work and appear to do
    /// nothing until the app restarts.
    /// A claim that is live in this process is not stale: a single-track
    /// download or a worker of an earlier run holds it right now. Only the
    /// claims nobody holds are recovered, so starting a batch never steals
    /// a track from a download that is still running.
    /// </summary>
    private async Task<int> ResetStaleClaimsAsync(string playlistId)
    {
        await using var db = await NewDbAsync(CancellationToken.None);
        var claimed = await SqliteDatabase.RetryBusyAsync(() => db.Tracks.AsNoTracking()
            .Where(t => t.PlaylistEntityId == playlistId && t.DownloadStatus == "in_progress")
            .Select(t => t.Id)
            .ToListAsync());
        var stale = claimed.Where(id => !_claims.IsLive(id)).ToList();
        if (stale.Count == 0) return 0;

        return await SqliteDatabase.RetryBusyAsync(() => db.Tracks
            .Where(t => stale.Contains(t.Id) && t.DownloadStatus == "in_progress")
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.DownloadStatus, "pending"), CancellationToken.None));
    }

    private async Task MarkTrackFailedAsync(int trackId, string error, CancellationToken ct)
    {
        try
        {
            await using var db = await NewDbAsync(CancellationToken.None);
            var reason = error.Length > 1000 ? error[..1000] : error;
            await SqliteDatabase.RetryBusyAsync(() => db.Tracks
                .Where(t => t.Id == trackId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.DownloadStatus, "failed")
                    .SetProperty(t => t.DownloadError, reason), CancellationToken.None));
        }
        catch (Exception ex)
        {
            // The row is already unusable; never let bookkeeping kill the
            // worker that is trying to keep the run alive.
            _logger.LogWarning(ex, "Could not record the failed status for track {TrackId}", trackId);
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
            // A previous run may have died between the claim and the status
            // write. Start is only called when no run is active, so any
            // in-progress row here is a stale claim, not live work.
            var recovered = await ResetStaleClaimsAsync(playlistId);
            if (recovered > 0)
                _logger.LogInformation(
                    "Recovered {Count} stale download claim(s) for {Playlist}", recovered, playlistId);

            var name = await LoadNameAsync(playlistId) ?? playlistId;
            var counts = await CountAsync(playlistId);
            var items = await LoadWorkItemsAsync(playlistId, ct);
            WorkQueue work;
            lock (handle.Sync)
            {
                handle.Done = counts.done;
                handle.Failed = counts.failed;
                handle.Snapshot = handle.Snapshot with
                {
                    PlaylistName = name,
                    Total = counts.total,
                    Done = counts.done,
                    Failed = counts.failed,
                    CurrentTrack = null,
                    CurrentPlugin = null,
                    State = DownloadRunState.Running,
                };
                handle.Work = work = new WorkQueue(items);
            }
            RefreshQueue(handle);

            var workers = await MaxConcurrentAsync(ct);
            // The limit is global: every run configures the same slot pool,
            // so two playlists together stay at this number.
            _services.GetService<DownloadThrottle>()?.Configure(workers);
            _logger.LogInformation("Download run for {Playlist} starting with {Workers} workers over {Count} tracks",
                playlistId, workers, work.TotalCount);

            // Each worker claims the next work item from the shared cursor
            // (atomic) and downloads exactly that track: the store's claim
            // still guards against a concurrent single-track click. The
            // shared pause gate holds every worker between tracks.
            var tasks = Enumerable.Range(0, workers)
                .Select(_ => WorkerAsync(handle, store, ct))
                .ToArray();
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
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
            // One exact read at the end corrects the incremental counters
            // (a skipped row, a concurrent single-track click, needs-review).
            try
            {
                var counts = await CountAsync(playlistId);
                lock (handle.Sync)
                {
                    handle.Done = counts.done;
                    handle.Failed = counts.failed;
                    handle.Snapshot = handle.Snapshot with
                    {
                        Done = counts.done,
                        Failed = counts.failed,
                        Total = counts.total,
                    };
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read final counts for {Playlist}", playlistId);
            }

            DownloadRun finished;
            lock (handle.Sync)
            {
                finished = handle.Snapshot with
                {
                    State = DownloadRunState.Finished,
                    CurrentTrack = null,
                    CurrentPlugin = null,
                    QueueLength = handle.Work.RemainingCount,
                };
                handle.Snapshot = finished;
            }
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
            var (trackId, title, priorStatus) = work;
            lock (handle.Sync)
            {
                handle.InFlight++;
                handle.Snapshot = handle.Snapshot with
                {
                    CurrentTrack = title,
                    State = DownloadRunState.Running,
                };
            }

            var result = "skipped";
            try
            {
                result = await DownloadWithRetryAsync(store, trackId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // the run is cancelling: stop this worker
            }
            catch (Exception ex)
            {
                // DownloadTrackAsync can throw before it ever claims the
                // row (settings, folder, database). One track must not take
                // the whole run down; record it and move on.
                _logger.LogError(ex, "Download worker could not attempt track {TrackId}", trackId);
                await MarkTrackFailedAsync(trackId, ex.Message, ct);
                result = "failed";
            }
            finally
            {
                // The item was attempted (or the run is ending): it leaves
                // the visible queue either way.
                handle.Work.Complete(workIndex);
                lock (handle.Sync)
                {
                    handle.InFlight--;
                    if (handle.InFlight == 0)
                        handle.Snapshot = handle.Snapshot with { CurrentTrack = null };
                }
            }

            RecordResult(handle, priorStatus, result);
            // The queue and the ETA move as tracks complete: one refresh per
            // finished track keeps both honest without extra polling.
            RefreshQueue(handle);
        }
    }

    /// <summary>
    /// Runs one track with a single retry for a transient failure. A
    /// cancellation that did not come from the run itself (a plugin
    /// timeout, a provider closing its request) is a track failure, not a
    /// reason to stop the worker.
    /// </summary>
    private async Task<string> DownloadWithRetryAsync(
        PlaylistStore store, int trackId, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await store.DownloadTrackAsync(trackId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // cooperative run cancel
            }
            catch (OperationCanceledException ex)
            {
                if (attempt == 0)
                {
                    _logger.LogWarning(
                        "Track {TrackId} download was cancelled by a provider; retrying once", trackId);
                    await Task.Delay(250, ct);
                    continue;
                }
                _logger.LogWarning(ex,
                    "Track {TrackId} download was cancelled by a provider twice; marking it failed", trackId);
                await MarkTrackFailedAsync(trackId, "the provider cancelled the download", ct);
                return "failed";
            }
            catch (Exception ex) when (attempt == 0)
            {
                _logger.LogWarning(ex,
                    "Track {TrackId} download attempt failed; retrying once", trackId);
                await Task.Delay(500, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Track {TrackId} download failed after a retry; marking it failed", trackId);
                await MarkTrackFailedAsync(trackId, ex.Message, ct);
                return "failed";
            }
        }
    }

    /// <summary>
    /// Folds one finished track into the live counters without re-reading
    /// the whole playlist. The final count in RunAsync is the exact truth.
    /// </summary>
    private static void RecordResult(RunHandle handle, string? priorStatus, string result)
    {
        lock (handle.Sync)
        {
            var wasFailed = priorStatus == "failed";
            switch (result)
            {
                case "downloaded":
                    handle.Done++;
                    if (wasFailed) handle.Failed = Math.Max(0, handle.Failed - 1);
                    break;
                case "failed":
                    if (!wasFailed) handle.Failed++;
                    break;
                case "needs-review" or "skipped" or "not-found":
                    // No longer a failed row: it is a review, or a row a
                    // refresh removed from under the run.
                    if (wasFailed) handle.Failed = Math.Max(0, handle.Failed - 1);
                    break;
            }
            handle.Snapshot = handle.Snapshot with
            {
                Done = handle.Done,
                Failed = handle.Failed,
            };
        }
    }

    /// <summary>
    /// Folds the visible queue length and an ETA into the snapshot. The
    /// title list itself is built lazily, only when a page asks for it.
    /// The ETA is paced by this run's own progress: the playlist's total
    /// download count says nothing about how fast this run moves, and
    /// mixing the two collapses the estimate on a playlist that was
    /// already downloaded.
    /// </summary>
    private static void RefreshQueue(RunHandle handle)
    {
        var remaining = handle.Work.RemainingCount;
        var finished = handle.Work.TotalCount - remaining;
        lock (handle.Sync)
        {
            var s = handle.Snapshot;
            handle.Snapshot = s with
            {
                Done = handle.Done,
                Failed = handle.Failed,
                QueueLength = remaining,
                EtaUtc = DownloadRun.ComputeEta(s.StartedAtUtc, finished, remaining),
            };
        }
    }

    private async Task<string?> LoadNameAsync(string playlistId)
    {
        await using var db = await NewDbAsync();
        return await db.Playlists.AsNoTracking()
            .Where(p => p.Id == playlistId)
            .Select(p => p.Name)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Live (total, done, failed) counts for the playlist. The grouped
    /// query returns one row per status, not one row per track: a page
    /// refresh or a final count on a 3000-track playlist stays cheap.
    /// </summary>
    private async Task<(int total, int done, int failed)> CountAsync(string playlistId)
    {
        await using var db = await NewDbAsync();
        var groups = await db.Tracks.AsNoTracking()
            .Where(t => t.PlaylistEntityId == playlistId)
            .GroupBy(t => t.DownloadStatus)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync();
        return (groups.Sum(g => g.Count),
            groups.Where(g => g.Status == "downloaded").Sum(g => g.Count),
            groups.Where(g => g.Status == "failed").Sum(g => g.Count));
    }

    /// <summary>
    /// This run's work list: every pending/failed track, in playlist
    /// Position order, with the status it had at the start. Snapshot at run
    /// start so each track is attempted exactly once per run.
    /// </summary>
    private async Task<List<(int Id, string Title, string? Status)>> LoadWorkItemsAsync(
        string playlistId, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);
        return await db.Tracks.AsNoTracking()
            .Where(t => t.PlaylistEntityId == playlistId)
            .Where(PendingWork)
            .OrderBy(t => t.Position)
            .Select(t => new ValueTuple<int, string, string?>(t.Id, t.Title ?? "", t.DownloadStatus))
            .ToListAsync(ct);
    }
}
