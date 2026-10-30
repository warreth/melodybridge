namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// The library file repair facade: adoption, legacy tag normalization and
/// duplicate cleanup. Kept in its own partial file so the main facade stays
/// the small delegation surface the split promised.
/// </summary>
public partial class PlaylistStore
{
    private readonly LibraryFileRepair _repair;

    /// <summary>Adopts orphan files and normalizes legacy tags for every playlist.</summary>
    public virtual Task<FileLinkRepairReport> RepairFileLinksAsync(CancellationToken ct = default)
        => _repair.RepairAsync(ct);

    /// <summary>Duplicate copies of the same track in playlist folders.</summary>
    public virtual Task<DuplicateFileReport> FindDuplicateFilesAsync(CancellationToken ct = default)
        => _repair.FindDuplicatesAsync(ct);

    /// <summary>Removes orphan copies and restores the plain name where possible.</summary>
    public virtual Task<DuplicateCleanupReport> RemoveDuplicateFilesAsync(
        IReadOnlyCollection<string> paths, CancellationToken ct = default)
        => _repair.CleanDuplicatesAsync(paths, ct);
}
