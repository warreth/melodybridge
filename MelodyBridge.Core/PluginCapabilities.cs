namespace MelodyBridge.Core;

/// <summary>
/// One quality a plugin can deliver: a container, whether the content is
/// lossless (a real lossless source, never a lossy file re-boxed as FLAC)
/// and the bitrate band that container arrives in.
///
/// <paramref name="Controlled"/> is the honesty flag: true when the plugin
/// can be asked for this and will deliver it, false when the service
/// decides the exact file and the entry only describes what may arrive.
/// <see cref="AudioFormat.Auto"/> as the container means "any container"
/// (the permissive stub manifest).
/// </summary>
public record QualityDeliverable(
    AudioFormat Container,
    bool Lossless,
    int? MinKbps = null,
    int? MaxKbps = null,
    bool Controlled = true);

/// <summary>
/// A plugin's honest manifest: everything it can deliver, plus whether it
/// can hand back a source file untouched (the Auto request). The router
/// filters and ranks on exactly this data: no plugin name, no priority
/// table and no magic band decides what runs for which quality. The
/// post-download measurement still has the last word, because a manifest
/// is a promise about capability, not about one file.
/// </summary>
public record PluginCapabilities(
    IReadOnlyList<QualityDeliverable> Deliverables,
    bool SupportsPassthrough = true)
{
    /// <summary>Permissive manifest for stubs and tests: anything goes.</summary>
    public static PluginCapabilities Any { get; } = new(
    [
        new QualityDeliverable(AudioFormat.Auto, Lossless: true, Controlled: false),
        new QualityDeliverable(AudioFormat.Auto, Lossless: false, Controlled: false),
    ]);

    /// <summary>One controlled lossy container, for manifests and stubs.</summary>
    public static PluginCapabilities Lossy(AudioFormat container, int? min = null, int? max = null)
        => new(new[] { new QualityDeliverable(container, Lossless: false, min, max) });

    /// <summary>One lossless container, for manifests and stubs.</summary>
    public static PluginCapabilities LosslessSources(AudioFormat container = AudioFormat.Flac, int? min = 900, int? max = 10000)
        => new(new[] { new QualityDeliverable(container, Lossless: true, min, max) });

    /// <summary>The concrete containers, for the UI badges and manifests.</summary>
    public IReadOnlyList<AudioFormat> Containers => Deliverables
        .Select(d => d.Container)
        .Where(c => c != AudioFormat.Auto)
        .Distinct()
        .OrderBy(c => c)
        .ToList();

    /// <summary>Lowest declared bitrate floor, null when every band is open.</summary>
    public int? MinKbps => Deliverables.Select(d => d.MinKbps).Min();

    /// <summary>Highest declared bitrate ceiling, null when every band is open.</summary>
    public int? MaxKbps => Deliverables.Select(d => d.MaxKbps).Max();

    /// <summary>True when a lossless source is in reach.</summary>
    public bool SupportsLossless => Deliverables.Any(d => d.Lossless);

    /// <summary>True when a lossy container is in reach.</summary>
    public bool SupportsLossy => Deliverables.Any(d => !d.Lossless);

    /// <summary>
    /// True when this plugin could deliver the request: a deliverable
    /// matches the container and lossless-ness, and either the band
    /// overlaps (controlled) or the service decides but may land in range
    /// (uncontrolled; the post-download measurement decides).
    /// </summary>
    public bool CanServe(DownloadQuality q)
    {
        if (!q.HasConstraints) return SupportsPassthrough || Deliverables.Count > 0;
        return Deliverables.Any(d => Matches(d, q));
    }

    /// <summary>
    /// True when a matching deliverable is one the plugin controls, so it
    /// can be asked for this quality instead of hoping for the best. The
    /// router runs these before the maybes when a request has constraints.
    /// </summary>
    public bool ServesControlled(DownloadQuality q)
        => Deliverables.Any(d => d.Controlled && Matches(d, q));

    private static bool Matches(QualityDeliverable d, DownloadQuality q)
    {
        if (d.Container != AudioFormat.Auto && q.Format != AudioFormat.Auto && d.Container != q.Format)
            return false;

        // A lossless request wants lossless content, and ignores the band:
        // a lossless file has no meaningful kbps ceiling.
        if (q.Format == AudioFormat.Flac)
            return d.Lossless;

        // A lossless deliverable can only answer a lossy or automatic
        // request when its real size fits the requested band (a FLAC never
        // fits "up to 160 kbps"). An unknown size can only answer a request
        // without a band.
        if (d.Lossless)
        {
            if (d.MinKbps is null && d.MaxKbps is null)
                return q.MinKbps is null && q.MaxKbps is null;
            return BandOverlaps(d.MinKbps, d.MaxKbps, q.MinKbps, q.MaxKbps);
        }

        // A lossy deliverable the plugin cannot control may still land in
        // range; only the measured file can prove it.
        if (!d.Controlled) return true;

        return BandOverlaps(d.MinKbps, d.MaxKbps, q.MinKbps, q.MaxKbps);
    }

    /// <summary>Band overlap with null bounds meaning unbounded.</summary>
    private static bool BandOverlaps(int? aMin, int? aMax, int? bMin, int? bMax)
    {
        var floor = Higher(aMin, bMin);
        var ceiling = Lower(aMax, bMax);
        return floor is null || ceiling is null || floor <= ceiling;
    }

    /// <summary>Higher of two bounds; null means unbounded.</summary>
    internal static int? Higher(int? a, int? b)
        => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);

    /// <summary>Lower of two bounds; null means unbounded.</summary>
    internal static int? Lower(int? a, int? b)
        => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);
}
