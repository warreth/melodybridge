namespace MelodyBridge.Core;

/// <summary>
/// The fallback ladder for playlists that are not strict: when the
/// requested quality cannot be delivered, the manager walks down through
/// the qualities the enabled plugins actually declare. There is no fixed
/// tier table; every step comes from a plugin's own manifest. The cap of
/// the request is never exceeded, and the container only changes when the
/// playlist allows it.
/// </summary>
public static class QualityLadder
{
    /// <summary>
    /// The targets to try, best first, starting with the request itself.
    /// A target no enabled plugin can serve is dropped, and duplicates
    /// collapse. An empty result means nothing in the ladder can serve
    /// the request at all (the manager then reports the request's own
    /// exclusion reason).
    /// </summary>
    public static IReadOnlyList<DownloadQuality> Build(
        DownloadQuality requested, bool allowContainerChange, IReadOnlyList<IDownloader> enabled)
    {
        var targets = new List<DownloadQuality> { requested };

        // One step down inside the requested container: drop the floor, so
        // "mp3:192-320" becomes "the best mp3 up to 320 that a source still
        // has". A cap without a floor already accepts everything below it.
        if (requested.MinKbps is not null)
            targets.Add(new DownloadQuality(requested.Format, MinKbps: null, MaxKbps: requested.MaxKbps));

        if (allowContainerChange)
        {
            // One step down per other container the plugins declare: the
            // best level that container can reach, never above the
            // requested cap. The last one to be tried is therefore the
            // smallest thing the plugins can produce.
            foreach (var (container, anchor) in BestLossyByContainer(enabled))
            {
                if (container == requested.Format) continue;
                targets.Add(new DownloadQuality(container, MinKbps: null,
                    MaxKbps: PluginCapabilities.Lower(requested.MaxKbps, anchor)));
            }
        }

        return targets
            .Where(t => enabled.Any(p => p.Capabilities.CanServe(t)))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// The best lossy level of every container the enabled plugins
    /// declare: the highest bitrate each container can reach. Containers
    /// whose only entries are uncontrollable come after the concrete ones.
    /// </summary>
    private static List<(AudioFormat Container, int? Anchor)> BestLossyByContainer(
        IReadOnlyList<IDownloader> enabled) => enabled
        .SelectMany(p => p.Capabilities.Deliverables)
        .Where(d => !d.Lossless && d.Container != AudioFormat.Auto)
        .GroupBy(d => d.Container)
        .Select(g => (Container: g.Key, Anchor: g.Select(d => d.MaxKbps).Max()))
        .OrderBy(x => x.Anchor is null ? 1 : 0)
        .ThenByDescending(x => x.Anchor)
        .ToList();
}
