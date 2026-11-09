using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Registers a folder as a manual scan location exactly once. Playlist
/// download folders and archive roots call this, so the Library page
/// always lists the folders the app writes to. A duplicate is matched
/// case-insensitively in memory: SQL string comparison is case-sensitive
/// on some providers.
/// </summary>
public static class ScanLocations
{
    /// <summary>
    /// Adds <paramref name="path"/> as a manual location (live monitoring
    /// off, no schedule) unless a location already covers it. The caller
    /// saves the context.
    /// </summary>
    public static async Task EnsureAsync(
        MelodyBridgeDbContext db, string? path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var trimmed = path.Trim();
        var locations = await db.ScanLocations.AsNoTracking()
            .Select(location => location.Path)
            .ToListAsync(ct);
        // A caller can register several roots in one context, and one of
        // them can repeat (for example a profile rooted at the default
        // archive). The database copy does not see pending adds yet.
        locations.AddRange(db.ChangeTracker.Entries<ScanLocationEntity>()
            .Where(entry => entry.State != EntityState.Deleted)
            .Select(entry => entry.Entity.Path));
        if (locations.Any(existing => string.Equals(
                existing, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        db.ScanLocations.Add(new ScanLocationEntity
        {
            Path = trimmed,
            LiveMonitoring = false,
            ScheduleCron = null,
            ScanIntervalHours = null,
        });
    }

    /// <summary>
    /// Drops the auto-registered location at <paramref name="path"/> when
    /// the archive root goes away, unless another archive profile still
    /// uses that exact folder. Without this the scanner would treat the
    /// leftover copies as main files and could adopt them.
    /// </summary>
    public static async Task RemoveRootAsync(
        MelodyBridgeDbContext db, string? path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var trimmed = path.Trim();
        var profileRoots = await db.ArchiveProfiles.AsNoTracking()
            .Select(profile => profile.RootPath)
            .ToListAsync(ct);
        if (profileRoots.Any(root => string.Equals(
                root, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var locations = await db.ScanLocations.ToListAsync(ct);
        db.ScanLocations.RemoveRange(locations.Where(location =>
            string.Equals(location.Path, trimmed, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// Registers every configured archive root: the default archive path,
    /// the named profiles and the legacy per-playlist overrides. Runs at
    /// startup so folders that were configured before auto-registration
    /// existed show up in the Library too. Reads the existing locations
    /// once, so a library with many playlists stays one query per table.
    /// </summary>
    public static async Task EnsureConfiguredRootsAsync(
        MelodyBridgeDbContext db, CancellationToken ct = default)
    {
        var roots = new List<string?>();
        roots.AddRange(await db.DownloaderSettings.AsNoTracking()
            .Where(setting => setting.Key == "archive_path")
            .Select(setting => setting.Value)
            .ToListAsync(ct));
        roots.AddRange(await db.ArchiveProfiles.AsNoTracking()
            .Where(profile => profile.RootPath != "")
            .Select(profile => profile.RootPath)
            .ToListAsync(ct));
        roots.AddRange(await db.Playlists.AsNoTracking()
            .Where(playlist => playlist.ArchiveDirectory != null
                && playlist.ArchiveDirectory != "")
            .Select(playlist => playlist.ArchiveDirectory)
            .ToListAsync(ct));

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        known.UnionWith(await db.ScanLocations.AsNoTracking()
            .Select(location => location.Path)
            .ToListAsync(ct));
        known.UnionWith(db.ChangeTracker.Entries<ScanLocationEntity>()
            .Where(entry => entry.State != EntityState.Deleted)
            .Select(entry => entry.Entity.Path));

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var trimmed = root.Trim();
            if (!known.Add(trimmed)) continue;
            db.ScanLocations.Add(new ScanLocationEntity
            {
                Path = trimmed,
                LiveMonitoring = false,
            });
        }
    }

    /// <summary>
    /// Follows an archive root that moved: the location at the old path is
    /// retargeted to the new one, keeping its schedule and monitoring
    /// choice. When the old path has no location, or more than one, the new
    /// path is simply added.
    /// </summary>
    public static async Task ReplaceRootAsync(
        MelodyBridgeDbContext db, string oldPath, string newPath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath))
            return;
        var oldTrimmed = oldPath.Trim();
        var newTrimmed = newPath.Trim();
        var locations = await db.ScanLocations.ToListAsync(ct);
        var matches = locations
            .Where(location => string.Equals(
                location.Path, oldTrimmed, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (locations.Any(location => string.Equals(
                location.Path, newTrimmed, StringComparison.OrdinalIgnoreCase)))
        {
            db.ScanLocations.RemoveRange(matches);
            return;
        }

        if (matches.Count == 1)
        {
            matches[0].Path = newTrimmed;
            return;
        }

        await EnsureAsync(db, newTrimmed, ct);
    }
}
