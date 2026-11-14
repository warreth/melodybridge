using MelodyBridge.Server.Components.Shared;

namespace MelodyBridge.Server.Components.Library;

/// <summary>One music folder as the page shows it: already measured and
/// already worded, so no layout has to touch the database or the disk.</summary>
public sealed record LibraryFolder(
    int Id,
    string Path,
    int Files,
    string Size,
    string Schedule,
    string LastScan,
    bool Missing,
    bool Live);

/// <summary>One place archive copies are written: the default archive or a
/// named profile. The editing dialogs live outside the layouts, so every
/// layout only has to render this.</summary>
public sealed record LibraryDestination(
    string Id,
    string Name,
    string Path,
    string Format,
    string Copies,
    string? SizeOnDisk,
    bool Missing,
    bool Enabled,
    bool IsDefault,
    string Summary);

/// <summary>A scan location that holds archive copies but is not written by
/// the default archive or a profile, for example a playlist's own folder.</summary>
public sealed record LibraryLooseArchive(
    int Id,
    string Path,
    int Files,
    string Size,
    string Owner,
    string LastScan,
    bool Missing);

/// <summary>
/// Everything the Library page knows, in the shape the layouts read. Built
/// once per load by <see cref="Library.razor"/>; a layout never queries.
/// </summary>
public sealed class LibraryViewData
{
    public IReadOnlyList<LibraryFolder> Folders { get; init; } = [];
    public IReadOnlyList<LibraryDestination> Destinations { get; init; } = [];
    public IReadOnlyList<LibraryLooseArchive> LooseArchives { get; init; } = [];

    public int FileCount { get; init; }
    public long Bytes { get; init; }
    public int ArchiveFiles { get; init; }
    public long ArchiveBytes { get; init; }

    /// <summary>Folders that hold tracks, which is what a reader calls "my library".</summary>
    public int MusicFolderCount { get; init; }
    public int ArchiveLocationCount { get; init; }
    public string LastScanText { get; init; } = "never";

    /// <summary>True while files without a stored size are still measured.</summary>
    public bool Measuring { get; init; }

    public int MissingFolders => Folders.Count(folder => folder.Missing);
    public int MissingDestinations => Destinations.Count(destination => destination.Missing);
    public int MissingCount => MissingFolders + MissingDestinations + LooseArchives.Count(a => a.Missing);

    public string SizeText => TrackDisplay.Size(Bytes) is { Length: > 0 } text ? text : "0 B";
    public string ArchiveSizeText => TrackDisplay.Size(ArchiveBytes) is { Length: > 0 } text ? text : "0 B";
}
