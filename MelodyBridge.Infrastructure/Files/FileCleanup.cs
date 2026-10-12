namespace MelodyBridge.Infrastructure.Files;

/// <summary>
/// Best-effort file deletion shared by the download plugins and the
/// manager: a rejected or half-written download must never keep the
/// library folder dirty, and cleanup itself must never throw.
/// </summary>
public static class FileCleanup
{
    /// <summary>Deletes the file when it exists; swallows every failure.</summary>
    public static void DeleteQuietly(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { File.Delete(path); } catch { /* best effort */ }
    }
}
