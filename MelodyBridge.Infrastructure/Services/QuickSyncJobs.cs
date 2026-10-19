using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.MediaServers;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// The simplified path from a saved playlist to a publishing job: one
/// Manual job per playlist, named after the playlist, pointing at the
/// media server profiles the user ticked. Everything else (schedules,
/// path remaps, M3U output, single-target edits) stays on the full Sync
/// jobs page; the jobs this creates open there like any other.
/// </summary>
public static class QuickSyncJobs
{
    /// <summary>
    /// Playlist ids that already have a sync job. The quick publish list
    /// hides those, so the simple flow can never create a second job for
    /// the same playlist.
    /// </summary>
    public static async Task<HashSet<string>> PlaylistsWithJobAsync(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = await db.SyncJobs
            .Where(j => j.SourceId != null)
            .Select(j => j.SourceId!)
            .ToListAsync(ct);
        return ids.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Creates one job per distinct playlist id: Manual schedule, the
    /// playlist as its source, and one target per ticked profile. Returns
    /// the created jobs, in playlist order.
    /// </summary>
    public static async Task<List<SyncJobEntity>> CreateAsync(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        IEnumerable<(string PlaylistId, string Name)> playlists,
        IReadOnlyList<MediaServerProfile> profiles,
        CancellationToken ct = default)
    {
        var created = new List<SyncJobEntity>();
        if (profiles.Count == 0) return created;

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var taken = await PlaylistsWithJobAsync(dbFactory, ct);

        foreach (var (playlistId, name) in playlists)
        {
            if (string.IsNullOrWhiteSpace(playlistId) || taken.Contains(playlistId))
                continue;

            var entity = new SyncJobEntity
            {
                Name = name,
                SourceId = playlistId,
                Schedule = nameof(SyncJobSchedule.Manual),
                CreatedAt = DateTime.UtcNow,
                LastRunStatus = "Pending",
            };
            SyncJobMapping.ApplyTargets(entity, profiles.Select(p =>
                new SyncTarget(MediaServerKinds.OutputTargetFor(p.Kind), ProfileId: p.Id)));
            db.SyncJobs.Add(entity);
            taken.Add(playlistId);
            created.Add(entity);
        }

        if (created.Count > 0) await db.SaveChangesAsync(ct);
        return created;
    }
}
