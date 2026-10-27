using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>One subfolder shown by the picker. Only names and paths, never files.</summary>
public sealed record FolderEntry(string Name, string Path, bool IsLink);

/// <summary>A named jump target such as Music, Playlists or Archive.</summary>
public sealed record FolderShortcut(string Label, string Path);

/// <summary>One directory listing for the folder picker.</summary>
public sealed record FolderListing(
    string Path,
    string? Parent,
    IReadOnlyList<FolderEntry> Entries,
    IReadOnlyList<string> Files,
    IReadOnlyList<FolderShortcut> Shortcuts,
    string? Error)
{
    public bool CanSelect => Error is null && Path.Length > 0;
}

/// <summary>
/// Server-side directory browser for the shared folder picker. The app is
/// self-hosted and already writes to any path typed in a text field, so
/// this adds no new reach; it does keep the SQLite state directory out of
/// the list so a user cannot accidentally hand a media path to the folder
/// that holds the database.
/// </summary>
public sealed class FolderBrowser
{
    private const int MaxEntries = 500;
    private readonly IDbContextFactory<MelodyBridgeDbContext> _dbFactory;
    private string? _blockedRoot;
    private bool _blockedLoaded;

    public FolderBrowser(IDbContextFactory<MelodyBridgeDbContext> dbFactory)
        => _dbFactory = dbFactory;

    /// <summary>Lists the folder, its subfolders and the shortcut rail.</summary>
    public async Task<FolderListing> BrowseAsync(string? path, CancellationToken ct = default)
    {
        var shortcuts = await ShortcutsAsync(ct);
        var start = string.IsNullOrWhiteSpace(path)
            ? shortcuts.FirstOrDefault()?.Path ?? RootPath()
            : path.Trim();

        string full;
        try
        {
            full = Path.GetFullPath(start);
            if (!Directory.Exists(full) && File.Exists(full))
                full = Path.GetDirectoryName(full) ?? RootPath();
        }
        catch (Exception ex)
        {
            return Error(shortcuts, path ?? "", "That path could not be read: " + ex.Message);
        }

        if (!Directory.Exists(full))
        {
            // A folder that does not exist yet is where the user wants to
            // go; show its nearest existing parent instead of a dead end.
            var parent = NearestExistingParent(full);
            return parent is null
                ? Error(shortcuts, full, "That folder does not exist.")
                : await BrowseAsync(parent, ct);
        }

        if (await IsBlockedAsync(full, ct))
            return Error(shortcuts, full,
                "This is the application state folder. Pick a music, playlist or archive folder instead.");

        var entries = new List<FolderEntry>();
        var files = new List<string>();
        try
        {
            var info = new DirectoryInfo(full);
            foreach (var child in info.EnumerateDirectories("*", new EnumerationOptions
                     {
                         IgnoreInaccessible = true,
                         RecurseSubdirectories = false,
                         AttributesToSkip = FileAttributes.System,
                     })
                     .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Take(MaxEntries))
            {
                var isLink = child.Attributes.HasFlag(FileAttributes.ReparsePoint);
                if (isLink)
                {
                    // A symlink inside an allowed folder must not become a
                    // shortcut into the state directory.
                    try
                    {
                        var target = child.ResolveLinkTarget(returnFinalTarget: true);
                        if (target is not null && await IsBlockedAsync(target.FullName, ct)) continue;
                    }
                    catch
                    {
                        // A broken link is still shown: it is a folder the
                        // user may want to see, and opening it will report
                        // the error.
                    }
                }

                if (await IsBlockedAsync(child.FullName, ct)) continue;
                entries.Add(new FolderEntry(child.Name, child.FullName, isLink));
            }

            // The picker selects folders, but seeing the files helps a
            // user orient. Only names are exposed, never sizes or bytes.
            foreach (var file in info.EnumerateFiles("*", new EnumerationOptions
                     {
                         IgnoreInaccessible = true,
                         RecurseSubdirectories = false,
                         AttributesToSkip = FileAttributes.System,
                     })
                     .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                     .Take(MaxEntries))
            {
                files.Add(file.Name);
            }
        }
        catch (Exception ex)
        {
            return new FolderListing(full, ParentOf(full), Array.Empty<FolderEntry>(),
                Array.Empty<string>(), shortcuts,
                "This folder could not be listed: " + ex.Message);
        }

        return new FolderListing(full, ParentOf(full), entries, files, shortcuts, null);
    }

    /// <summary>Creates one folder under an existing parent.</summary>
    public async Task<FolderListing> CreateAsync(
        string parent, string name, CancellationToken ct = default)
    {
        var clean = (name ?? string.Empty).Trim();
        if (clean.Length == 0)
            return await BrowseAsync(parent, ct) with { Error = "Type a folder name first." };
        if (clean is "." or ".."
            || clean.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || clean.Contains(Path.DirectorySeparatorChar)
            || clean.Contains(Path.AltDirectorySeparatorChar))
            return await BrowseAsync(parent, ct) with { Error = "That folder name is not valid." };

        string fullParent;
        try
        {
            fullParent = Path.GetFullPath(parent);
        }
        catch
        {
            return await BrowseAsync(parent, ct) with { Error = "The parent folder is not valid." };
        }

        if (!Directory.Exists(fullParent))
            return await BrowseAsync(fullParent, ct) with { Error = "The parent folder does not exist." };
        if (await IsBlockedAsync(fullParent, ct))
            return await BrowseAsync(fullParent, ct) with { Error = "The application state folder is not available." };

        var target = Path.Combine(fullParent, clean);
        try
        {
            Directory.CreateDirectory(target);
        }
        catch (Exception ex)
        {
            return await BrowseAsync(fullParent, ct) with
            {
                Error = "The folder could not be created: " + ex.Message,
            };
        }

        return await BrowseAsync(target, ct);
    }

    private async Task<IReadOnlyList<FolderShortcut>> ShortcutsAsync(CancellationToken ct)
    {
        var shortcuts = new List<FolderShortcut>();
        void Add(string label, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = Path.GetFullPath(path.Trim()); }
            catch { return; }
            if (!Directory.Exists(full)) return;
            if (shortcuts.Any(s => string.Equals(s.Path, full, PathComparison))) return;
            shortcuts.Add(new FolderShortcut(label, full));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add("Home", home);
        Add("Music", await SettingAsync("music_path", ct));
        Add("Playlists", await SettingAsync("playlist_path", ct));
        Add("Archive", await SettingAsync("archive_path", ct));
        Add("slskd downloads", await SettingAsync("plugin:slskd:downloads_path", ct));
        Add("deemix downloads", await SettingAsync("plugin:deemix:downloads_path", ct));
        Add("Tidal app folder", await SettingAsync("plugin:tidal:app_path", ct));

        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var scanPaths = await db.ScanLocations.AsNoTracking()
                .Select(location => location.Path)
                .ToListAsync(ct);
            for (var i = 0; i < scanPaths.Count; i++)
                Add($"Scan location {i + 1}", scanPaths[i]);

            foreach (var profile in await db.ArchiveProfiles.AsNoTracking()
                         .OrderBy(profile => profile.Priority)
                         .Select(profile => new { profile.Name, profile.RootPath })
                         .ToListAsync(ct))
            {
                Add(profile.Name, profile.RootPath);
            }
        }

        Add("Root", RootPath());
        if (OperatingSystem.IsWindows())
        {
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
                Add(drive.Name.TrimEnd('\\'), drive.RootDirectory.FullName);
        }

        return shortcuts;
    }

    private async Task<string?> SettingAsync(string key, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.DownloaderSettings.AsNoTracking()
            .Where(row => row.Key == key)
            .Select(row => row.Value)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<bool> IsBlockedAsync(string path, CancellationToken ct)
    {
        if (!_blockedLoaded)
        {
            _blockedRoot = await FindStateDirectoryAsync(ct);
            _blockedLoaded = true;
        }
        if (string.IsNullOrWhiteSpace(_blockedRoot)) return false;

        string full;
        try { full = Path.GetFullPath(path); }
        catch { return true; }
        return IsUnder(full, _blockedRoot);
    }

    private async Task<string?> FindStateDirectoryAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var connection = db.Database.GetConnectionString();
            var source = (connection ?? string.Empty)
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(part => part.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
                ?.Split('=', 2).LastOrDefault();
            source ??= Environment.GetEnvironmentVariable("MELODYBRIDGE_DB") ?? "melodybridge.db";
            if (source.Contains(":memory:", StringComparison.OrdinalIgnoreCase)) return null;
            var full = Path.GetFullPath(source);
            return Path.GetDirectoryName(full);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUnder(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static string? ParentOf(string path)
    {
        try
        {
            var parent = Directory.GetParent(path)?.FullName;
            return parent is null || path.Equals(RootPath(), PathComparison) ? null : parent;
        }
        catch
        {
            return null;
        }
    }

    private static string? NearestExistingParent(string path)
    {
        var current = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        while (!string.IsNullOrWhiteSpace(current) && !Directory.Exists(current))
            current = Path.GetDirectoryName(current);
        return current;
    }

    private static string RootPath()
        => OperatingSystem.IsWindows()
            ? Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\"
            : "/";

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static FolderListing Error(
        IReadOnlyList<FolderShortcut> shortcuts, string path, string message)
        => new(path, ParentOf(path), Array.Empty<FolderEntry>(),
            Array.Empty<string>(), shortcuts, message);
}
