namespace MelodyBridge.Core;

/// <summary>
/// What a downloader plugin can honestly produce: container formats and a
/// bitrate band. Null bounds mean unbounded; the manifest is a routing
/// input for <see cref="QualityRouter"/>, never a promise about a specific
/// file (the post-download measurement still decides).
/// </summary>
public record PluginCapabilities(
    IReadOnlyList<AudioFormat> Containers,
    int? MinKbps,
    int? MaxKbps,
    bool SupportsLossless,
    bool SupportsLossy)
{
    /// <summary>Unknown source / test stub: anything might work.</summary>
    public static PluginCapabilities Any { get; } =
        new([AudioFormat.Auto], null, null, SupportsLossless: true, SupportsLossy: true);

    /// <summary>
    /// True when this plugin could produce a file for the request: format
    /// Auto always passes, explicit formats must be served, and the
    /// requested band must overlap the plugin's honest band. Null plugin
    /// bounds mean unbounded.
    /// </summary>
    public bool CanServe(DownloadQuality q)
    {
        if (q.Format != AudioFormat.Auto && !Containers.Contains(q.Format))
            return false;

        // FLAC and other lossless containers ignore the band: bitrate
        // bounds make no sense there (DownloadQuality semantics).
        if (q.Format is AudioFormat.Flac)
            return true;

        if (q.MaxKbps is { } cap && MinKbps is { } min && min > cap)
            return false;
        if (q.MinKbps is { } floor && MaxKbps is { } max && max < floor)
            return false;
        return true;
    }
}
