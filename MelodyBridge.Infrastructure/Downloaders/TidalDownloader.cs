using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Tidal;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// Tidal downloader plugin driven by the user's own tiddl
/// (https://github.com/oskvr37/tiddl). tiddl owns the login
/// (<c>tiddl auth login</c>) and the download; MelodyBridge searches
/// Tidal's catalogue with that same session and hands over the track URL.
///
/// The search runs against the public v1 API with the stored bearer token,
/// ISRC-first like the other subscription plugins. Downloads go through
/// the shared CLI plumbing, with the tool's own quality tiers mapped onto
/// the requested band: LOW (96 kbps m4a), NORMAL (320 kbps m4a), HIGH
/// (16-bit FLAC) and MAX (hi-res FLAC).
/// </summary>
public class TidalDownloader : CliToolDownloader
{
    public const string DefaultCommand = "tiddl";
    public const string DefaultQuality = "high";
    private const string CommandKey = "command";
    private const string QualityKey = "quality";
    private const string AppPathKey = "app_path";

    // Tidal has no MP3: it serves AAC (LOW/NORMAL) and FLAC (HIGH/MAX).
    // The lossless tier depends on the configured quality.
    public static readonly PluginCapabilities Caps = new(
    [
        new QualityDeliverable(AudioFormat.Flac, Lossless: true, 900, 10000),
        new QualityDeliverable(AudioFormat.Aac, Lossless: false, 96, 320),
    ]);
    public override PluginCapabilities Capabilities => Caps;

    public override string Id => "tidal";
    public override string Name => "Tidal";
    public override string Description => "Lossless and hi-res downloads through your own tiddl";

    public override IReadOnlyList<PluginConfigField> ConfigFields =>
    [
        new(QualityKey, "FLAC quality", null,
            "How high lossless goes; AAC requests use the 320 or 96 kbps tier.",
            Options:
            [
                new("high", "16-bit / 44.1 kHz (CD quality)"),
                new("max", "Up to 24-bit / 192 kHz (hi-res)"),
            ]),
        new(AppPathKey, "tiddl app folder", null,
            "Only needed when tiddl runs with a custom TIDDL_PATH. The login comes from the tiddl session, see the Tidal docs.",
            Advanced: true),
        new(CommandKey, "tiddl command", DefaultCommand,
            "Only needed when tiddl is not on PATH; the Docker image ships it.",
            Advanced: true),
    ];

    private readonly TidalClient _tidal;

    public TidalDownloader(
        TidalClient tidal, IPluginConfig config, ILogger<TidalDownloader> logger)
        : base(config, logger)
    {
        _tidal = tidal;
    }

    protected override string ToolLabel => "tiddl";
    protected override string DefaultCommandLine => DefaultCommand;
    protected override string CommandConfigKey => CommandKey;
    protected override TimeSpan RunTimeout => TimeSpan.FromMinutes(20);

    /// <summary>The tiddl app folder the login is read from, and where a custom one is written.</summary>
    private async Task<string> AppPathAsync(CancellationToken ct)
        => TidalAuth.ResolveAppPath(await ConfigValueAsync(AppPathKey, ct));

    // ── Availability ─────────────────────────────────────────────────────

    public override async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (TryGetCachedAvailability(out var cached))
            return cached;

        if (!await CommandExistsAsync(ct))
        {
            LastUnavailableReason = "tiddl is not installed on this system";
            Logger.LogDebug("tidal: the tiddl command is not configured or not on PATH");
            StoreAvailability(false);
            return false;
        }

        var session = TidalAuth.Read(await AppPathAsync(ct));
        if (session is null)
        {
            LastUnavailableReason = "Run tiddl auth login to connect Tidal";
            Logger.LogDebug("tidal: tiddl has no stored session in its app folder");
            StoreAvailability(false);
            return false;
        }
        if (session.Expired)
        {
            LastUnavailableReason = "The tiddl session expired; run tiddl auth login again";
            Logger.LogDebug("tidal: the stored tiddl session is past its expiry");
            StoreAvailability(false);
            return false;
        }

        LastUnavailableReason = null;
        StoreAvailability(true);
        return true;
    }

    // ── Search ───────────────────────────────────────────────────────────

    public override async Task<DownloaderSearchHit?> SearchAsync(
        TrackQuery query, CancellationToken ct = default)
    {
        var searchText = query.SearchText;
        if (searchText.Length == 0) return null;

        var session = TidalAuth.Read(await AppPathAsync(ct));
        if (session is null || session.Expired || string.IsNullOrWhiteSpace(session.Token))
            return null;

        var tracks = await _tidal.SearchTracksAsync(
            searchText, 10, session.Token!, session.CountryCode ?? "US", ct);
        return Rank(tracks, query);
    }

    /// <summary>
    /// Ranks the Tidal results with the shared rules; an exact ISRC match
    /// narrows the pool to the recording itself. Internal for tests.
    /// </summary>
    internal static DownloaderSearchHit? Rank(
        IReadOnlyList<TidalClient.TidalTrack> tracks, TrackQuery query)
    {
        var candidates = new List<(DownloaderSearchHit Hit, string? Isrc)>();
        foreach (var track in tracks)
        {
            if (!track.StreamReady) continue;

            var hit = new DownloaderSearchHit(
                Title: track.Title,
                Artist: track.Artist,
                SourceUrl: $"https://tidal.com/browse/track/{track.Id}",
                Duration: track.Duration,
                MatchConfidence: FuzzyMatcher.Confidence(
                    query.Artist, query.Title, hitArtist: track.Artist, hitTitle: track.Title));
            candidates.Add((hit, track.Isrc));
        }

        if (candidates.Count == 0) return null;

        var isrcPool = candidates.Where(c =>
            !string.IsNullOrWhiteSpace(query.Isrc) &&
            !string.IsNullOrWhiteSpace(c.Isrc) &&
            string.Equals(query.Isrc.Trim(), c.Isrc.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = isrcPool.Count > 0 ? isrcPool : candidates;
        return SearchRanker.Rank(query.Artist, query.Title, query.ExpectedDuration,
            pool.Select(c => c.Hit));
    }

    // ── Download ─────────────────────────────────────────────────────────

    /// <summary>The private folder tiddl writes into for this track.</summary>
    protected override Task<string> WorkDirectoryAsync(
        string outputDirectory, string? melodyId, CancellationToken ct)
        => Task.FromResult(Path.Combine(outputDirectory,
            $".mb-tidal-{(melodyId is { Length: > 0 } ? melodyId : Guid.NewGuid().ToString("N")[..8])}"));

    protected override async Task<CliToolRequest> BuildRequestAsync(
        string sourceUrl, DownloadQuality quality, string workDirectory, CancellationToken ct)
    {
        if (!TryExtractTrackId(sourceUrl, out var trackId))
            return new CliToolRequest([], "not a Tidal track URL: " + sourceUrl);

        var qualityTier = QualityFor(quality, await ConfigValueAsync(QualityKey, ct));
        if (qualityTier is null)
            return new CliToolRequest([],
                "no tiddl quality fits this quality band; skipping instead of wasting a download");

        return new CliToolRequest(
        [
            "download",
            "--track-quality", qualityTier,
            "--path", workDirectory,
            // Keep the "already downloaded" scan inside the fresh work
            // folder: the user's own library must never make the tool skip
            // the file this run has to produce.
            "--scan-path", workDirectory,
            "--no-skip",
            "url", $"https://tidal.com/browse/track/{trackId}",
        ]);
    }

    /// <summary>
    /// Points tiddl at the configured app folder (its login and cache live
    /// there). The default folder needs no environment override.
    /// </summary>
    protected override async Task<IReadOnlyDictionary<string, string>?> PrepareEnvironmentAsync(
        CancellationToken ct)
    {
        var configured = await ConfigValueAsync(AppPathKey, ct);
        if (configured.Length == 0) return null;

        return new Dictionary<string, string>
        {
            ["TIDDL_PATH"] = TidalAuth.ResolveAppPath(configured),
        };
    }

    /// <summary>
    /// The tiddl --track-quality value: FLAC requests use the configured
    /// tier, AAC requests the 320 or 96 tier, and nothing else fits.
    /// Internal for tests.
    /// </summary>
    internal static string? QualityFor(DownloadQuality quality, string configuredQuality)
    {
        switch (quality.Format)
        {
            case AudioFormat.Flac:
                return NormalizeQuality(configuredQuality);
            case AudioFormat.Aac:
                if (quality.IsWithinBand(320)) return "normal";
                if (quality.IsWithinBand(96)) return "low";
                return null;
            case AudioFormat.Mp3:
            case AudioFormat.Opus:
                return null;
        }

        // Auto: the configured lossless tier.
        return NormalizeQuality(configuredQuality);
    }

    /// <summary>A valid tier, or the CD-quality default.</summary>
    internal static string NormalizeQuality(string configured)
        => configured.Trim().ToLowerInvariant() is "low" or "normal" or "high" or "max"
            ? configured.Trim().ToLowerInvariant()
            : DefaultQuality;

    internal static bool TryExtractTrackId(string sourceUrl, out long trackId)
    {
        trackId = 0;
        if (string.IsNullOrWhiteSpace(sourceUrl)) return false;
        var lastSegment = sourceUrl.Trim().TrimEnd('/').Split('/')[^1].Split('?')[0];
        return long.TryParse(lastSegment, out trackId) && trackId > 0;
    }
}
