namespace MelodyBridge.Core;

/// <summary>
/// One download's private notes. The producing plugin is written here when
/// the download succeeds and is read by the caller's post-download steps.
/// Keeping it out of the manager's shared per-song memory is what keeps
/// two download runs of the same song (two playlists at once) from
/// stamping each other's rows with the other run's provider.
/// </summary>
public sealed class DownloadJournal
{
    /// <summary>The plugin that produced the file, or null while nothing
    /// has landed yet.</summary>
    public string? ProducingPluginId { get; set; }
}
