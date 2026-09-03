using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Scanning;

/// <summary>
/// Reconciles playlist tracks with files on disk after a restart: a track
/// whose file still exists (at CurrentPath or anywhere under the playlist's
/// TargetDirectory, matched by MELODY_ID tag) goes back to "downloaded"
/// with fresh audio facts. Tracks whose file vanished flip to "pending" so
/// the next download run picks them up again.
/// </summary>
public class LibraryReconciler
{
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private readonly ILogger<LibraryReconciler> _logger;

    public LibraryReconciler(
        IDbContextFactory<MelodyBridgeDbContext> dbFactory,
        ILogger<LibraryReconciler> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    /// <summary>Run once at startup. Returns (relinked, markedPending).</summary>
    public async Task<(int relinked, int lost)> ReconcileAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var playlists = await db.Playlists
            .Include(p => p.Tracks)
            .ToListAsync(ct);

        var relinked = 0;
        var lost = 0;

        foreach (var playlist in playlists)
        {
            var dir = playlist.TargetDirectory;
            var filesByMelodyId = dir is null
                ? new Dictionary<string, string>()
                : IndexDirectory(dir);

            foreach (var track in playlist.Tracks)
            {
                // Fast path: the remembered path still exists.
                if (track.CurrentPath is not null && File.Exists(track.CurrentPath))
                {
                    if (track.DownloadStatus != "downloaded")
                    {
                        track.DownloadStatus = "downloaded";
                        track.DownloadError = null;
                        relinked++;
                    }
                    continue;
                }

                // Slow path: find the file by its MELODY_ID tag under the
                // playlist folder (files move, ids do not).
                if (track.MelodyId is not null
                    && filesByMelodyId.TryGetValue(track.MelodyId, out var found))
                {
                    track.CurrentPath = found;
                    track.DownloadStatus = "downloaded";
                    track.DownloadError = null;
                    track.LastSeenAt = DateTime.UtcNow;
                    relinked++;
                    continue;
                }

                // Nothing on disk: schedule a re-download.
                if (track.DownloadStatus == "downloaded")
                {
                    track.DownloadStatus = "pending";
                    track.CurrentPath = null;
                    track.Warning = "file missing on disk, will re-download";
                    lost++;
                }
            }
        }

        if (relinked > 0 || lost > 0)
            await db.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Startup reconciliation: {Relinked} tracks relinked, {Lost} missing files queued for re-download",
            relinked, lost);
        return (relinked, lost);
    }

    /// <summary>MELODY_ID tag -> path, one entry per audio file in the tree.</summary>
    private static Dictionary<string, string> IndexDirectory(string dir)
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        var extensions = new[] { ".mp3", ".flac", ".ogg", ".opus", ".m4a", ".aac", ".wav", ".webm" };
        try
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            {
                if (!extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    continue;
                var id = TaglibHelper.ReadMelodyId(file);
                if (!string.IsNullOrWhiteSpace(id))
                    index.TryAdd(id, file);
            }
        }
        catch (Exception)
        {
            // Unreadable folder: leave the index partial rather than failing the run.
        }
        return index;
    }
}
