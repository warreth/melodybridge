using System.Collections.Concurrent;
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
public class DownloadCoordinator
{
    private readonly IServiceProvider _services;
    private readonly ILogger<DownloadCoordinator> _logger;

    private sealed class RunHandle
    {
        public Task Task = Task.CompletedTask;
        public CancellationTokenSource Cts = new();
        public ManualResetEventSlim Gate = new(true);
        public DownloadRun Snapshot = null!;
        /// <summary>Workers currently mid-track; keeps CurrentTrack in the snapshot honest with parallel workers.</summary>
        public int InFlight;
        /// <summary>Ordered pending/failed track titles for the live run (Position
        /// order); recomputed after each completed track, read by the UI.</summary>
        public List<string> QueueTitles = new();
    }

    private readonly ConcurrentDictionary<string, RunHandle> _runs = new();

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

    /// <summary>Ordered titles of the pending/failed tracks (Position order)
    /// of the live run for this playlist: the visible download queue.</summary>
    public IReadOnlyList<string> QueueFor(string playlistId)
        => _runs.TryGetValue(playlistId, out var h) ? h.QueueTitles : Array.Empty<string>();

    public bool IsActive(string playlistId)
        => _runs.TryGetValue(playlistId, out var h)
            && h.Snapshot.State is DownloadRunState.Running or DownloadRunState.Paused or DownloadRunState.Cancelling;

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
            await RefreshQueueAsync(playlistId, handle, ct);

            var workers = await MaxConcurrentAsync(ct);
            _logger.LogInformation("Download run for {Playlist} starting with {Workers} workers",
                playlistId, workers);

            // Each worker claims one pending track at a time (the store's
            // claim is atomic), so the workers never race on a track. The
            // shared pause gate holds every worker between tracks.
            var tasks = Enumerable.Range(0, workers)
                .Select(_ => WorkerAsync(playlistId, handle, store, ct))
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
        }
        finally
        {
            handle.Snapshot = handle.Snapshot with
            {
                State = DownloadRunState.Finished,
                CurrentTrack = null,
                CurrentPlugin = null,
            };
        }
    }

    /// <summary>One download worker loop: claim a track, download it, repeat.</summary>
    private async Task WorkerAsync(string playlistId, RunHandle handle, PlaylistStore store, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            handle.Gate.Wait(ct); // cooperative pause between tracks
            if (ct.IsCancellationRequested) break;

            var next = await NextPendingTitleAsync(playlistId, ct);
            if (next is null) break; // nothing left to download

            var inFlight = Interlocked.Increment(ref handle.InFlight);
            handle.Snapshot = handle.Snapshot with
            {
                CurrentTrack = next,
                State = DownloadRunState.Running,
            };

            try
            {
                await store.DownloadMissingAsync(playlistId, limit: 1, ct: ct);
            }
            finally
            {
                Interlocked.Decrement(ref handle.InFlight);
            }

            var counts = await CountAsync(playlistId);
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
            await RefreshQueueAsync(playlistId, handle, ct);
        }
    }

    /// <summary>
    /// Recomputes the visible queue (pending/failed titles, Position order)
    /// and folds it into the snapshot: queue length plus an ETA derived from
    /// the average pace of the tracks completed so far.
    /// </summary>
    private async Task RefreshQueueAsync(string playlistId, RunHandle handle, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);
        var titles = await db.Tracks.AsNoTracking()
            .Where(t => t.PlaylistEntityId == playlistId
                && (t.DownloadStatus == null || t.DownloadStatus == "pending"
                    || t.DownloadStatus == "failed" || t.DownloadStatus == "in_progress"))
            .OrderBy(t => t.Position)
            .Select(t => t.Title ?? "")
            .ToListAsync(ct);
        handle.QueueTitles = titles;

        var s = handle.Snapshot;
        handle.Snapshot = s with
        {
            QueueLength = titles.Count,
            EtaUtc = DownloadRun.ComputeEta(s.StartedAtUtc, s.Done + s.Failed, titles.Count),
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

    private async Task<string?> NextPendingTitleAsync(string playlistId, CancellationToken ct)
    {
        await using var db = await NewDbAsync(ct);
        return await db.Tracks.AsNoTracking()
            .Where(t => t.PlaylistEntityId == playlistId
                && (t.DownloadStatus == null || t.DownloadStatus == "pending" || t.DownloadStatus == "failed"))
            .OrderBy(t => t.Position)
            .Select(t => t.Title)
            .FirstOrDefaultAsync(ct);
    }
}
