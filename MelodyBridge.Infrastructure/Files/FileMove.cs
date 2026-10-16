namespace MelodyBridge.Infrastructure.Files;

/// <summary>
/// Moves a finished download out of a tool's folder into the playlist
/// folder: a name that never overwrites, and a copy fallback when the
/// two folders live on different filesystems (the CLI tools and MelodyBridge
/// often mount their folders separately).
/// </summary>
public static class FileMove
{
    /// <summary>A target path in the folder that never overwrites an existing file.</summary>
    public static string UniqueTarget(string outputDirectory, string basename)
    {
        var target = Path.Combine(outputDirectory, basename);
        if (!File.Exists(target)) return target;

        var name = Path.GetFileNameWithoutExtension(basename);
        var ext = Path.GetExtension(basename);
        for (var i = 2; ; i++)
        {
            target = Path.Combine(outputDirectory, $"{name} ({i}){ext}");
            if (!File.Exists(target)) return target;
        }
    }

    /// <summary>Moves across devices by copying and deleting.</summary>
    public static void MoveAcrossDevices(string source, string target)
    {
        try
        {
            File.Move(source, target);
        }
        catch (IOException)
        {
            File.Copy(source, target, overwrite: true);
            File.Delete(source);
        }
    }
}
