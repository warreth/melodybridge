using MelodyBridge.Infrastructure.Data;

namespace MelodyBridge.Server.Components.Shared;

/// <summary>
/// One place that turns a track row into the quality, size and duration
/// strings the UI shows. Used by the playlist details page and the
/// library page so both display the same facts the same way.
/// </summary>
public static class TrackDisplay
{
    /// <summary>Bitrate + container, e.g. "320 kbps · flac", empty when nothing is known.</summary>
    public static string Quality(TrackEntity t)
    {
        var parts = new List<string>();
        if (t.Bitrate is > 0) parts.Add($"{t.Bitrate} kbps");
        if (t.SampleRateHz is > 0) parts.Add($"{t.SampleRateHz / 1000.0:0.#} kHz");
        if (!string.IsNullOrWhiteSpace(t.MediaType)) parts.Add(t.MediaType);
        return string.Join(" · ", parts);
    }

    /// <summary>Human file size ("3.4 MB"), empty when unknown.</summary>
    public static string Size(TrackEntity t)
    {
        if (t.FileSizeBytes is not > 0) return string.Empty;
        return t.FileSizeBytes >= 1024 * 1024
            ? $"{t.FileSizeBytes / (1024.0 * 1024.0):0.#} MB"
            : $"{t.FileSizeBytes / 1024.0:0.#} KB";
    }

    public static string Duration(long? ms)
        => ms is > 0
            ? TimeSpan.FromMilliseconds(ms.Value).ToString(@"mm\:ss")
            : "-";
}
