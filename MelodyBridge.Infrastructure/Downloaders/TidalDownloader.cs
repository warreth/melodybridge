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
    private const string TokenKey = "token";
    private const string RefreshTokenKey = "refresh_token";
    private const string UserIdKey = "user_id";
    private const string CountryKey = "country_code";

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
        new(TokenKey, "Tidal token", null,
            "Paste the token from your provider, or tiddl's auth.json. The user id, country and expiry are read from it and written to tiddl's auth.json, so no tiddl auth login is needed. Without a refresh token the token works until it expires.",
            Secret: true),
        new(QualityKey, "FLAC quality", null,
            "How high lossless goes; AAC requests use the 320 or 96 kbps tier.",
            Options:
            [
                new("high", "16-bit / 44.1 kHz (CD quality)"),
                new("max", "Up to 24-bit / 192 kHz (hi-res)"),
            ]),
        new(RefreshTokenKey, "Tidal refresh token", null,
            "Only when your provider gave one separately. With it, tiddl renews the session by itself.",
            Advanced: true, Secret: true),
        new(UserIdKey, "Tidal user id", null,
            "Read from the token when possible; tiddl refuses to download without it.",
            Advanced: true),
        new(CountryKey, "Tidal country code", null,
            "Two letters, for example NL. Read from the token when possible; the catalogue follows it.",
            Advanced: true),
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

    // ── Auth seeding ─────────────────────────────────────────────────────

    /// <summary>How seeding the pasted token ended.</summary>
    private enum SeedState
    {
        /// <summary>No token in the settings: tiddl's own login is used.</summary>
        NotConfigured,

        /// <summary>The settings were readable; the auth file is up to date.</summary>
        Done,

        /// <summary>The pasted value could not be read at all.</summary>
        Unreadable,

        /// <summary>The auth file could not be written.</summary>
        Failed,
    }

    /// <summary>
    /// Writes a pasted token (or full auth.json) into tiddl's app folder
    /// before anything reads the session. Settings that did not change
    /// since the last seed leave the file alone, so a session tiddl
    /// renewed itself is never overwritten.
    /// </summary>
    private async Task<SeedState> EnsureAuthSeededAsync(CancellationToken ct)
    {
        var raw = await ConfigValueAsync(TokenKey, ct);
        if (raw.Length == 0) return SeedState.NotConfigured;

        var data = TidalAuth.ParseSettings(
            raw,
            await ConfigValueAsync(RefreshTokenKey, ct),
            await ConfigValueAsync(UserIdKey, ct),
            await ConfigValueAsync(CountryKey, ct));
        if (data is null) return SeedState.Unreadable;

        try
        {
            TidalAuth.Seed(await AppPathAsync(ct), data);
            return SeedState.Done;
        }
        catch (Exception ex)
        {
            LastUnavailableReason = "The tiddl app folder could not be written: " + ex.Message;
            Logger.LogDebug(ex, "tidal: seeding auth.json failed");
            return SeedState.Failed;
        }
    }

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

        var seed = await EnsureAuthSeededAsync(ct);
        if (seed == SeedState.Failed)
        {
            StoreAvailability(false);
            return false;
        }
        if (seed == SeedState.Unreadable)
        {
            LastUnavailableReason = "The Tidal token is not readable; paste the token or auth.json again";
            StoreAvailability(false);
            return false;
        }

        var session = TidalAuth.Read(await AppPathAsync(ct));
        if (session is null)
        {
            LastUnavailableReason = "Run tiddl auth login to connect Tidal, or paste a token in the settings";
            Logger.LogDebug("tidal: tiddl has no stored session in its app folder");
            StoreAvailability(false);
            return false;
        }
        if (session.Expired)
        {
            LastUnavailableReason =
                $"The Tidal token expired on {DateTimeOffset.FromUnixTimeSeconds(session.ExpiresAtUnix).ToLocalTime():yyyy-MM-dd HH:mm}; paste a fresh one";
            Logger.LogDebug("tidal: the stored tiddl session is past its expiry");
            StoreAvailability(false);
            return false;
        }
        if (string.IsNullOrWhiteSpace(session.UserId) || string.IsNullOrWhiteSpace(session.CountryCode))
        {
            LastUnavailableReason =
                "The token did not carry the user id and country code; fill them under Advanced, or paste a full auth.json";
            Logger.LogDebug("tidal: the session misses the user id or country");
            StoreAvailability(false);
            return false;
        }

        var check = await _tidal.ValidateTokenAsync(session.Token, session.CountryCode, ct);
        if (check == TidalClient.TidalTokenStatus.Rejected)
        {
            LastUnavailableReason = "Tidal rejected the token; paste a fresh one";
            StoreAvailability(false);
            return false;
        }
        if (check == TidalClient.TidalTokenStatus.Unreachable)
        {
            LastUnavailableReason = "The Tidal API did not answer";
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

        await EnsureAuthSeededAsync(ct);
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
    /// Seeds a pasted token first, then points tiddl at the configured
    /// app folder (its login and cache live there). The default folder
    /// needs no environment override.
    /// </summary>
    protected override async Task<IReadOnlyDictionary<string, string>?> PrepareEnvironmentAsync(
        CancellationToken ct)
    {
        await EnsureAuthSeededAsync(ct);

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
