using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// Link metadata through the same yt-dlp binary the download plugins use.
/// It only reads facts; the download itself still goes through the plugin
/// waterfall, so a link that no enabled plugin can download fails there
/// with a plugin-level reason.
/// </summary>
public sealed class YtDlpUrlMetadataResolver : IUrlMetadataResolver
{
    public Task<UrlTrackMetadata?> ResolveAsync(string url, CancellationToken ct = default)
        => YtDlpProcess.ResolveTrackMetadataAsync(url, ct);
}
