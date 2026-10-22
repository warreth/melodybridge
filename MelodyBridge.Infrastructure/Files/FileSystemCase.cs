using System.Collections.Concurrent;

namespace MelodyBridge.Infrastructure.Files;

/// <summary>
/// What a filesystem does with the case of a name. Two spellings of one
/// name are the same file on a case-insensitive volume and two separate
/// files on a case-sensitive one. The volume decides, so the app asks
/// the volume: the probe writes a file with a known name and looks for
/// it under another spelling. One file means the volume folds case, two
/// files mean it does not. Windows folds case by default, but a folder
/// there can be case-sensitive, so Windows is asked as well.
/// </summary>
public static class FileSystemCase
{
    private static readonly ConcurrentDictionary<string, bool> Answers =
        new(StringComparer.Ordinal);

    /// <summary>
    /// True when two spellings of one name in this directory are the same
    /// file. The answer is cached per directory: a volume does not change
    /// its mind while the app runs.
    /// </summary>
    public static bool FoldsCase(string directory)
    {
        string key;
        try
        {
            key = Path.GetFullPath(directory);
        }
        catch
        {
            // A path that cannot be resolved cannot be judged. Assume the
            // volume folds case, so a name is never taken by accident.
            return true;
        }
        return Answers.GetOrAdd(key, Probe);
    }

    /// <summary>
    /// Asks the volume. The probe runs in the nearest directory that
    /// exists, because the folder that holds the music may not be there
    /// yet. A directory that cannot be written to cannot be probed, and
    /// then the app assumes the volume folds case.
    /// </summary>
    private static bool Probe(string directory)
    {
        var probeDirectory = NearestExistingDirectory(directory);
        if (probeDirectory is null) return true;

        // The name ends in a letter so the second spelling is always a
        // different name, even when the random part is all digits.
        var name = ".mb-case-probe-" + Guid.NewGuid().ToString("N") + "a";
        var path = Path.Combine(probeDirectory, name);
        var spelled = Path.Combine(probeDirectory, name.Substring(0, name.Length - 1) + "A");
        try
        {
            File.WriteAllText(path, "probe");
            return File.Exists(spelled);
        }
        catch
        {
            return true;
        }
        finally
        {
            try { File.Delete(path); } catch { /* best effort */ }
        }
    }

    /// <summary>The closest directory that exists, or null when none does.</summary>
    private static string? NearestExistingDirectory(string directory)
    {
        try
        {
            var current = new DirectoryInfo(Path.GetFullPath(directory));
            while (current is not null)
            {
                if (current.Exists) return current.FullName;
                current = current.Parent;
            }
        }
        catch
        {
            // fall through
        }
        return null;
    }
}
