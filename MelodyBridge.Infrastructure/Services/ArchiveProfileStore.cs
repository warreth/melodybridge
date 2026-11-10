using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Named extra archive destinations. The original single archive stays
/// the default (archive_path + archive_format); these are additional
/// mirrors, each with its own root and format.
/// </summary>
public sealed class ArchiveProfileStore
{
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;

    public ArchiveProfileStore(IDbContextFactory<MelodyBridgeDbContext> dbFactory)
        => _dbFactory = dbFactory;

    public async Task<IReadOnlyList<ArchiveProfileEntity>> GetAllAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ArchiveProfiles.AsNoTracking()
            .OrderBy(profile => profile.Priority)
            .ThenBy(profile => profile.Name)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ArchiveProfileEntity>> GetEnabledAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.ArchiveProfiles.AsNoTracking()
            .Where(profile => profile.Enabled)
            .OrderBy(profile => profile.Priority)
            .ThenBy(profile => profile.Name)
            .ToListAsync(ct);
    }

    public async Task<ArchiveProfileEntity> SaveAsync(
        ArchiveProfileEntity profile, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var name = profile.Name.Trim();
        if (name.Length == 0) name = "Archive";
        var root = profile.RootPath.Trim();
        if (root.Length == 0)
            throw new InvalidOperationException("Choose a folder for this archive.");
        var format = ArchiveCopier.NormalizeFormat(profile.Format);
        var spec = ArchiveCopier.ParseFormat(format);
        if (spec.Container.Length == 0)
            throw new InvalidOperationException("Choose a container for this archive.");
        if (spec.BitrateKbps is < 32 or > 512)
            throw new InvalidOperationException("A bitrate between 32 and 512 kbps is supported.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = string.IsNullOrWhiteSpace(profile.Id)
            ? null
            : await db.ArchiveProfiles.FirstOrDefaultAsync(p => p.Id == profile.Id, ct);

        if (row is null)
        {
            row = new ArchiveProfileEntity
            {
                Id = string.IsNullOrWhiteSpace(profile.Id)
                    ? Guid.NewGuid().ToString("N")
                    : profile.Id,
                CreatedAt = DateTime.UtcNow,
                Priority = await db.ArchiveProfiles.AnyAsync(ct)
                    ? await db.ArchiveProfiles.MaxAsync(p => p.Priority, ct) + 1
                    : 0,
            };
            db.ArchiveProfiles.Add(row);
        }

        var candidates = await db.ArchiveProfiles.AsNoTracking()
            .Where(other => other.Id != row.Id && other.RootPath == root)
            .Select(other => other.Format)
            .ToListAsync(ct);
        var duplicate = candidates.Any(other => ArchiveCopier.ExtensionsMayCollide(other, format));
        if (duplicate)
            throw new InvalidOperationException(
                "Another archive profile already uses this folder and format.");

        var previousRoot = row.RootPath;
        row.Name = name;
        row.RootPath = root;
        row.Format = format;
        row.Enabled = profile.Enabled;
        await RegisterScanLocationAsync(db, row.Id, previousRoot, root, ct);
        await db.SaveChangesAsync(ct);
        return row;
    }

    /// <summary>
    /// The Library page lists the folders the app writes to. A new archive
    /// root becomes a scan location; an edited one takes its location
    /// along, unless another profile or the default archive still uses the
    /// old root.
    /// </summary>
    private static async Task RegisterScanLocationAsync(
        MelodyBridgeDbContext db, string profileId, string previousRoot, string root,
        CancellationToken ct)
    {
        var moved = previousRoot.Length > 0
            && !string.Equals(
                previousRoot, root,
                OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        if (!moved)
        {
            await ScanLocations.EnsureAsync(db, root, ct);
            return;
        }

        var shared = await db.ArchiveProfiles.AsNoTracking()
                .AnyAsync(other => other.Id != profileId && other.RootPath == previousRoot, ct)
            || await db.DownloaderSettings.AsNoTracking()
                .AnyAsync(setting => setting.Key == "archive_path"
                    && setting.Value == previousRoot, ct);
        if (shared)
            await ScanLocations.EnsureAsync(db, root, ct);
        else
            await ScanLocations.ReplaceRootAsync(db, previousRoot, root, ct);
    }

    /// <summary>
    /// Removes the profile and its copy rows. Files stay on disk on
    /// purpose: deleting a configuration must not delete a library.
    /// </summary>
    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var profile = await db.ArchiveProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null) return;

        var copies = await db.TrackArchiveCopies
            .Where(copy => copy.ProfileId == id)
            .ToListAsync(ct);
        db.TrackArchiveCopies.RemoveRange(copies);

        // The auto-registered scan location must not outlive the archive
        // root: without the profile its copies would look like main files
        // and could be adopted by the scanner. A root another profile or
        // the default archive still uses keeps its location.
        var root = profile.RootPath;
        var shared = await db.ArchiveProfiles.AsNoTracking().AnyAsync(
                other => other.Id != id && other.RootPath == root, ct)
            || await db.DownloaderSettings.AsNoTracking().AnyAsync(
                setting => setting.Key == "archive_path" && setting.Value == root, ct);
        if (!shared)
        {
            var locations = await db.ScanLocations.ToListAsync(ct);
            db.ScanLocations.RemoveRange(locations.Where(location =>
                string.Equals(location.Path, root, StringComparison.OrdinalIgnoreCase)));
        }

        // A publish profile that used this archive falls back to the
        // primary file instead of failing every run.
        var linkedPublishProfiles = await db.SyncRuleProfiles
            .Where(publish => publish.SourceArchiveProfileId == id)
            .ToListAsync(ct);
        foreach (var publish in linkedPublishProfiles)
            publish.SourceArchiveProfileId = null;

        db.ArchiveProfiles.Remove(profile);
        await db.SaveChangesAsync(ct);
    }

    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var profile = await db.ArchiveProfiles.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (profile is null) return;
        profile.Enabled = enabled;
        await db.SaveChangesAsync(ct);
    }
}
