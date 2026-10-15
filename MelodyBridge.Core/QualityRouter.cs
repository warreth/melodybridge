namespace MelodyBridge.Core;

/// <summary>
/// Quality-aware routing for the download waterfall: filters the enabled
/// plugins by what they can actually serve for the requested quality and
/// orders the survivors, purely from each plugin's declared manifest.
/// Pure, no I/O, no availability checks; the manager still probes
/// availability per run. The Plugins page order is the base order: with
/// no constraints ("No filter") it is exactly the order, and with
/// constraints the plugins that control the requested quality run first.
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

    /// <summary>
    /// The configured order is the order. The only reordering rule is
    /// generic: when the request has constraints, plugins that can be
    /// *asked* for that quality run before plugins that merely might
    /// deliver it. Which plugin that is comes from the manifests alone.
    /// </summary>
    private static IReadOnlyList<IDownloader> Rank(DownloadQuality quality, List<IDownloader> plugins)
    {
        if (!quality.HasConstraints) return plugins;

        // OrderBy is stable, so the user's order survives inside both groups.
        return plugins
            .OrderBy(p => p.Capabilities.ServesControlled(quality) ? 0 : 1)
            .ToList();
    }

    /// <summary>A human-readable reason, so the debug log and the UI can
    /// say why a plugin never ran for this request.</summary>
    private static string ExcludeReason(DownloadQuality q, PluginCapabilities caps)
    {
        if (q.Format == AudioFormat.Flac && !caps.SupportsLossless)
            return "no lossless source";
        if (q.Format != AudioFormat.Auto && !caps.Containers.Contains(q.Format))
            return $"cannot produce {q.Format}";
        return $"cannot serve {DescribeBand(q)}";
    }

    private static string DescribeBand(DownloadQuality q) => (q.MinKbps, q.MaxKbps) switch
    {
        (null, null) => "the request",
        (null, { } max) => $"up to {max} kbps",
        ({ } min, null) => $"{min}+ kbps",
        ({ } min, { } max) => $"{min}-{max} kbps",
    };
}
