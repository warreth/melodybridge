using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Server.Services;

/// <summary>The result of one bulk attention action.</summary>
public sealed record AttentionBulkResult(int Done, int Failed, List<string> Messages)
{
    public string Summary => Failed == 0
        ? $"{Done} track{(Done == 1 ? "" : "s")} handled."
        : $"{Done} handled, {Failed} still need attention.";
}

/// <summary>The page-facing contract, so the UI can be exercised with a slow fake.</summary>
public interface IAttentionActions
{
    Task<AttentionBulkResult> RetryAsync(
        IReadOnlyList<int> trackIds, bool avoidLastProvider,
        IProgress<int>? progress = null, CancellationToken ct = default);

    Task<AttentionBulkResult> HardlinkAsync(
        IReadOnlyList<int> trackIds,
        IProgress<int>? progress = null, CancellationToken ct = default);

    Task<AttentionBulkResult> ArchiveAsync(
        IReadOnlyList<int> trackIds,
        IProgress<int>? progress = null, CancellationToken ct = default);

    Task<AttentionBulkResult> QualityReviewAsync(
        IReadOnlyList<int> trackIds, QualityReviewAction action,
        IProgress<int>? progress = null, CancellationToken ct = default);

    Task<AttentionBulkResult> ReverifyAsync(
        IReadOnlyList<int> trackIds,
        IProgress<int>? progress = null, CancellationToken ct = default);

    Task<AttentionBulkResult> DedupAsync(
        IReadOnlyList<int> trackIds, bool hardlinkExisting,
        IProgress<int>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// Bulk actions for the global attention page. Every action is the same
/// operation the single-track row uses, run one track at a time so one
/// failure never cancels the rest and the result can say what failed.
/// </summary>
public sealed class AttentionActions : IAttentionActions
{
    private readonly PlaylistStore _store;
    private readonly ArchiveCopyService _archives;
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly ILogger<AttentionActions> _logger;

    public AttentionActions(
        PlaylistStore store,
        ArchiveCopyService archives,
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        ILogger<AttentionActions> logger)
    {
        _store = store;
        _archives = archives;
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<AttentionBulkResult> RetryAsync(
        IReadOnlyList<int> trackIds, bool avoidLastProvider,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var done = 0;
        var failed = 0;
        var messages = new List<string>();
        foreach (var id in trackIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var status = await _store.DownloadTrackAsync(id, ct, avoidLastProvider);
                if (status == "downloaded") done++;
                else
                {
                    failed++;
                    messages.Add($"Track {id}: {status}");
                }
            }
            catch (Exception ex)
            {
                failed++;
                messages.Add($"Track {id}: {ex.Message}");
            }
            progress?.Report(done + failed);
        }
        return new AttentionBulkResult(done, failed, messages);
    }

    public async Task<AttentionBulkResult> HardlinkAsync(
        IReadOnlyList<int> trackIds,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var done = 0;
        var failed = 0;
        var messages = new List<string>();
        foreach (var id in trackIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (await _store.LinkTrackAsync(id, ct)) done++;
                else
                {
                    failed++;
                    messages.Add($"Track {id}: no matching file to link.");
                }
            }
            catch (Exception ex)
            {
                failed++;
                messages.Add($"Track {id}: {ex.Message}");
            }
            progress?.Report(done + failed);
        }
        return new AttentionBulkResult(done, failed, messages);
    }

    public async Task<AttentionBulkResult> ArchiveAsync(
        IReadOnlyList<int> trackIds,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var done = 0;
        var failed = 0;
        var messages = new List<string>();
        foreach (var id in trackIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (ok, reason) = await _store.RetryArchiveAsync(id, ct);
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var track = await db.Tracks.FirstOrDefaultAsync(t => t.Id == id, ct);
                if (track is not null)
                    await _archives.ArchiveExtraCopiesAsync(track, db, save: true, ct: ct);
                if (ok) done++;
                else
                {
                    failed++;
                    messages.Add($"Track {id}: {reason ?? "the archive copy could not be written"}");
                }
            }
            catch (Exception ex)
            {
                failed++;
                messages.Add($"Track {id}: {ex.Message}");
            }
            progress?.Report(done + failed);
        }
        return new AttentionBulkResult(done, failed, messages);
    }

    public async Task<AttentionBulkResult> QualityReviewAsync(
        IReadOnlyList<int> trackIds, QualityReviewAction action,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var done = 0;
        var failed = 0;
        var messages = new List<string>();
        foreach (var id in trackIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var resolved = await _store.ResolveQualityReviewAsync(id, action, ct);
                if (!resolved)
                {
                    failed++;
                    messages.Add($"Track {id}: the review could not be resolved.");
                }
                else if (action == QualityReviewAction.Retry)
                {
                    var status = await _store.DownloadTrackAsync(id, ct);
                    if (status == "downloaded") done++;
                    else
                    {
                        failed++;
                        messages.Add($"Track {id}: {status}");
                    }
                }
                else done++;
            }
            catch (Exception ex)
            {
                failed++;
                messages.Add($"Track {id}: {ex.Message}");
            }
            progress?.Report(done + failed);
        }
        return new AttentionBulkResult(done, failed, messages);
    }

    public async Task<AttentionBulkResult> ReverifyAsync(
        IReadOnlyList<int> trackIds,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var done = 0;
        var failed = 0;
        var messages = new List<string>();
        foreach (var id in trackIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (ok, reason) = await _store.ReverifyAsync(id, ct);
                if (ok) done++;
                else
                {
                    failed++;
                    messages.Add($"Track {id}: {reason ?? "the file could not be verified"}");
                }
            }
            catch (Exception ex)
            {
                failed++;
                messages.Add($"Track {id}: {ex.Message}");
            }
            progress?.Report(done + failed);
        }
        return new AttentionBulkResult(done, failed, messages);
    }

    public async Task<AttentionBulkResult> DedupAsync(
        IReadOnlyList<int> trackIds, bool hardlinkExisting,
        IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var done = 0;
        var failed = 0;
        var messages = new List<string>();
        foreach (var id in trackIds)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var resolved = await _store.ResolveReviewAsync(id, hardlinkExisting, ct);
                if (!resolved)
                {
                    failed++;
                    messages.Add($"Track {id}: the other file is no longer available.");
                }
                else if (!hardlinkExisting)
                {
                    var status = await _store.DownloadTrackAsync(id, ct);
                    if (status == "downloaded") done++;
                    else
                    {
                        failed++;
                        messages.Add($"Track {id}: {status}");
                    }
                }
                else done++;
            }
            catch (Exception ex)
            {
                failed++;
                messages.Add($"Track {id}: {ex.Message}");
                _logger.LogDebug(ex, "Attention dedup action failed for {Track}", id);
            }
            progress?.Report(done + failed);
        }
        return new AttentionBulkResult(done, failed, messages);
    }
}
