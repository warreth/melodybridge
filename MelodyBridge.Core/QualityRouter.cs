namespace MelodyBridge.Core;

/// <summary>
/// Quality-aware routing for the download waterfall: filters the enabled
/// plugins by what they can actually serve for the requested quality, then
/// ranks them so the most promising source is tried first. Pure — no I/O,
/// no availability checks; the manager still probes availability per run.
/// </summary>
public static class QualityRouter
{
    /// <summary>The routed waterfall: survivors in try-order plus why each excluded plugin is out.</summary>
    public record Plan(IReadOnlyList<IDownloader> Plugins, IReadOnlyList<(IDownloader plugin, string reason)> Excluded);

    public static Plan Route(DownloadQuality quality, IReadOnlyList<IDownloader> enabled)
    {
        var included = new List<IDownloader>();
        var excluded = new List<(IDownloader, string)>();

        foreach (var plugin in enabled)
        {
            if (plugin.Capabilities.CanServe(quality))
                included.Add(plugin);
            else
                excluded.Add((plugin, ExcludeReason(quality, plugin.Capabilities)));
        }

        return new Plan(Rank(quality, included), excluded);
    }

    /// <summary>A human-readable reason, so the debug log says why a plugin never ran.</summary>
    private static string ExcludeReason(DownloadQuality q, PluginCapabilities caps)
    {
        if (q.Format != AudioFormat.Auto && !caps.Containers.Contains(q.Format))
            return $"cannot serve {q.Format}";
        if (q.MaxKbps is { } cap && caps.MinKbps is { } min && min > cap)
            return $"cannot serve under {cap} kbps (min {min})";
        return $"cannot serve over {q.MinKbps} kbps (max {caps.MaxKbps})";
    }

    /// <summary>Stable ranking: user priority (incoming order) only moves when a rule says so.</summary>
    private static IReadOnlyList<IDownloader> Rank(DownloadQuality quality, List<IDownloader> plugins)
    {
        // Lossless path: whoever has FLAC first, lossy-only after.
        if (quality.Format == AudioFormat.Flac)
            return plugins
                .OrderBy(p => p.Capabilities.SupportsLossless ? 0 : 1)
                .ToList();

        // Small-file cap: small-file specialists first, lossless-capable
        // plugins last (they waste time on FLAC the band rejects).
        if (quality.MaxKbps is { } cap && cap <= 160)
            return plugins
                .OrderBy(p => SmallFileRank(p, cap))
                .ToList();

        // Auto / no filters: user order unchanged.
        return plugins;
    }

    private static int SmallFileRank(IDownloader p, int cap)
    {
        if (p.Capabilities.SupportsLossless) return 2;      // FLAC producer: wrong tool for a cap
        if (p.Capabilities.MaxKbps is { } max && max <= cap) return 0; // small-file specialist
        return 1;
    }
}
