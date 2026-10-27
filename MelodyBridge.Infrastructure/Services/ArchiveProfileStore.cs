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

        row.Name = name;
        row.RootPath = root;
        row.Format = format;
        row.Enabled = profile.Enabled;
        await db.SaveChangesAsync(ct);
        return row;
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
