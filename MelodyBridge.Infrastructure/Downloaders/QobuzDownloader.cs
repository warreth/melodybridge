using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Qobuz;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// Qobuz downloader plugin driven by the user's own qobuz-dlp
/// (https://github.com/infojunkie/qobuz-dlp.py), the yt-dlp model: the
/// tool owns the login and the download, MelodyBridge only searches and
/// hands over a track URL.
///
/// The search needs a user token; the plugin takes it from its settings
/// or, when empty, from qobuz-dlp's own token file, so a logged-in
/// qobuz-dlp just works. Downloads run into a hidden per-track folder
/// under the output directory and the finished file moves out; the shared
/// CLI plumbing lives in <see cref="CliToolDownloader"/>.
/// </summary>
public class QobuzDownloader : CliToolDownloader
{
    public const string DefaultCommand = "qobuz-dlp";
    public const string DefaultQuality = "6";
    private const string CommandKey = "command";
    private const string TokenKey = "user_auth_token";
    private const string QualityKey = "quality";

    // Qobuz has no continuous band: 320 MP3 and lossless FLAC, period.
    // The FLAC tier can be hi-res, depending on the configured quality.
    public static readonly PluginCapabilities Caps = new(
    [
        new QualityDeliverable(AudioFormat.Flac, Lossless: true, 900, 10000),
        new QualityDeliverable(AudioFormat.Mp3, Lossless: false, 320, 320),
    ]);
    public override PluginCapabilities Capabilities => Caps;

    public override string Id => "qobuz";
    public override string Name => "Qobuz";
    public override string Description => "Lossless and hi-res downloads through your own qobuz-dlp";

    public override IReadOnlyList<PluginConfigField> ConfigFields =>
    [
        new(TokenKey, "Qobuz token", null,
            "From play.qobuz.com: copy the X-User-Auth-Token header of an api.json request in the Network tab. A paid plan is required to download.",
            Secret: true),
        new(QualityKey, "FLAC quality", null,
            "How high lossless goes; MP3 requests always use the 320 tier.",
            Options:
            [
                new("6", "16-bit / 44.1 kHz (CD quality)"),
                new("7", "24-bit up to 96 kHz"),
                new("27", "24-bit above 96 kHz"),
            ]),
        new(CommandKey, "qobuz-dlp command", DefaultCommand,
            "Only needed when qobuz-dlp is not on PATH; the Docker image ships it.",
            Advanced: true),
    ];

    private readonly QobuzClient _qobuz;

    /// <summary>%APPDATA% on Windows, the home folder elsewhere; injectable for tests.</summary>
    private readonly string _homeRoot;

    public QobuzDownloader(
        QobuzClient qobuz, IPluginConfig config, ILogger<QobuzDownloader> logger,
        string? homeRoot = null)
        : base(config, logger)
    {
        _qobuz = qobuz;
        _homeRoot = homeRoot ?? DefaultHomeRoot();
    }

    protected override string ToolLabel => "qobuz-dlp";
    protected override string DefaultCommandLine => DefaultCommand;
    protected override string CommandConfigKey => CommandKey;
    protected override TimeSpan RunTimeout => TimeSpan.FromMinutes(20);

    /// <summary>The root qobuz-dlp resolves its config folder from. Internal for tests.</summary>
    internal static string DefaultHomeRoot()
        => OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>qobuz-dlp's config folder as the tool computes it.</summary>
    private string ToolConfigFolder
        => OperatingSystem.IsWindows()
            ? Path.Combine(_homeRoot, "qobuz-dl")
            : Path.Combine(_homeRoot, ".config", "qobuz-dl");

    /// <summary>The env that makes the CLI read exactly that config folder.</summary>
    private IReadOnlyDictionary<string, string> ToolEnvironment()
        => OperatingSystem.IsWindows()
            ? new Dictionary<string, string> { ["APPDATA"] = _homeRoot }
            : new Dictionary<string, string> { ["HOME"] = _homeRoot };

    // ── Availability ─────────────────────────────────────────────────────

    public override async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (TryGetCachedAvailability(out var cached))
            return cached;

        if (!await CommandExistsAsync(ct))
        {
            LastUnavailableReason = "qobuz-dlp is not installed on this system";
            SetAuth(PluginAuthState.Unknown);
            Logger.LogDebug("qobuz: the qobuz-dlp command is not configured or not on PATH");
            StoreAvailability(false);
            return false;
        }

        if (await _qobuz.GetBundleAsync(ct) is null)
        {
            LastUnavailableReason = "The Qobuz web player could not be reached";
            SetAuth(PluginAuthState.Unreachable, LastUnavailableReason);
            StoreAvailability(false);
            return false;
        }

        var token = await TokenAsync(ct);
        if (token is null)
        {
            LastUnavailableReason = "No Qobuz token yet";
            SetAuth(PluginAuthState.Missing, LastUnavailableReason);
            Logger.LogDebug("qobuz: no token configured and qobuz-dlp has no token file");
            StoreAvailability(false);
            return false;
        }

        var check = await _qobuz.ValidateTokenAsync(token, ct);
        var tier = TierLabel(await ConfigValueAsync(QualityKey, ct));
        var account = check.Account?.Label;
        if (check.Status == QobuzClient.QobuzTokenStatus.Blocked)
        {
            LastUnavailableReason = check.Message
                ?? "Qobuz blocked this account from downloading";
            SetAuth(PluginAuthState.Blocked, LastUnavailableReason,
                account, "Qobuz", tier);
            StoreAvailability(false);
            return false;
        }
        if (check.Status == QobuzClient.QobuzTokenStatus.Unreachable)
        {
            LastUnavailableReason = check.Message
                ?? "The Qobuz token could not be checked (the API did not answer)";
            SetAuth(PluginAuthState.Unreachable, LastUnavailableReason,
                account, "Qobuz", tier);
            StoreAvailability(false);
            return false;
        }
        if (check.Status == QobuzClient.QobuzTokenStatus.Rejected || check.Account is null)
        {
            LastUnavailableReason = check.Message ?? "The Qobuz token was rejected; paste a fresh one";
            SetAuth(PluginAuthState.Rejected, LastUnavailableReason,
                account, "Qobuz", tier);
            StoreAvailability(false);
            return false;
        }
        if (!check.Account.CanDownload)
        {
            LastUnavailableReason = "This Qobuz plan cannot download (a paid plan is required)";
            SetAuth(PluginAuthState.PlanRestricted, LastUnavailableReason,
                account, "Qobuz", tier);
            StoreAvailability(false);
            return false;
        }

        LastUnavailableReason = null;
        SetAuth(PluginAuthState.Valid, null, account, "Qobuz", tier);
        StoreAvailability(true);
        return true;
    }

    /// <summary>
    /// The user token: the plugin setting wins, an empty setting falls
    /// back to qobuz-dlp's own token file so a logged-in tool just works.
    /// </summary>
    private async Task<string?> TokenAsync(CancellationToken ct)
    {
        var configured = await ConfigValueAsync(TokenKey, ct);
        return configured.Length > 0 ? configured : ReadTokenFile(ToolConfigFolder);
    }

    /// <summary>
    /// Makes sure qobuz-dlp has a config it can run with: the app id,
    /// private key and secrets scraped from the web player, plus the user
    /// token when the plugin has one. Without this the CLI would stop for
    /// an interactive first-run setup. Internal for tests.
    /// </summary>
    internal async Task EnsureToolConfigAsync(string token, CancellationToken ct)
    {
        var folder = ToolConfigFolder;
        Directory.CreateDirectory(folder);

        var configPath = Path.Combine(folder, "config.ini");
        if (!File.Exists(configPath))
        {
            var bundle = await _qobuz.GetBundleAsync(ct);
            if (bundle is null)
            {
                Logger.LogWarning("Qobuz: could not scrape the web player bundle for the CLI config");
                return;
            }
            await File.WriteAllTextAsync(configPath, BuildConfigIni(bundle), ct);
        }

        if (token.Length > 0)
            await File.WriteAllTextAsync(Path.Combine(folder, ".qobuz_dl.oauth.txt"), token, ct);
    }

    /// <summary>The qobuz-dlp config file contents. Internal for tests.</summary>
    internal static string BuildConfigIni(QobuzClient.BundleInfo bundle) => $$"""
        [DEFAULT]
        default_folder = Qobuz Downloads
        default_quality = 6
        default_limit = 20
        no_m3u = false
        albums_only = false
        no_fallback = false
        og_cover = false
        embed_art = false
        no_cover = false
        no_database = false
        app_id = {{bundle.AppId}}
        private_key = {{bundle.PrivateKey}}
        secrets = {{string.Join(",", bundle.Secrets)}}
        folder_format = {artist} - {album} ({year})
        track_format = {tracknumber}. {tracktitle}
        smart_discography = false
        booklet_only = false
        """;

    /// <summary>
    /// qobuz-dlp's stored OAuth token. The tool's config folder changed
    /// name between builds ("qobuz-dl" in the PyPI wheel, "qobuz-dlp" in
    /// some checkouts), so both are tried. Internal for tests.
    /// </summary>
    internal static string? ReadQobuzDlpTokenFile()
    {
        var root = OperatingSystem.IsWindows()
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return ReadTokenFile(Path.Combine(root, "qobuz-dl"))
            ?? ReadTokenFile(Path.Combine(root, "qobuz-dlp"));
    }

    /// <summary>The token file inside a qobuz-dlp config folder, or null. Internal for tests.</summary>
    internal static string? ReadTokenFile(string configFolder)
    {
        var path = Path.Combine(configFolder, ".qobuz_dl.oauth.txt");
        try
        {
            if (!File.Exists(path)) return null;
            var token = File.ReadAllText(path).Trim();
            return token.Length > 0 ? token : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ── Search ───────────────────────────────────────────────────────────

    public override async Task<DownloaderSearchHit?> SearchAsync(
        TrackQuery query, CancellationToken ct = default)
    {
        var token = await TokenAsync(ct);
        if (token is null) return null;

        // The Qobuz catalogue indexes ISRCs: searching the code itself
        // finds the exact recording even when the text search never
        // returns it (a soundtrack sharing words, a decorated title).
        // Only an exact ISRC match is accepted from that call; anything
        // else falls through to the text search.
        if (query.HasIsrc)
        {
            var byIsrc = await _qobuz.SearchTracksAsync(query.Isrc!, 5, token, ct);
            if (!AcceptSearchResult(byIsrc)) return null;
            if (Rank(byIsrc.Tracks, query, requireIsrcMatch: true) is { } exact) return exact;
        }

        var searchText = query.SearchText;
        if (searchText.Length == 0) return null;

        var result = await _qobuz.SearchTracksAsync(searchText, 10, token, ct);
        if (!AcceptSearchResult(result)) return null;
        return Rank(result.Tracks, query);
    }

    /// <summary>
    /// Reads a search outcome into the plugin's login state: a blocked or
    /// refused token becomes the reason the plugin is skipped, so the
    /// card, the nav mark and the download failure all say what is wrong
    /// instead of "no source had this track".
    /// </summary>
    private bool AcceptSearchResult(QobuzClient.QobuzSearchResult result)
    {
        switch (result.Status)
        {
            case QobuzClient.QobuzSearchStatus.Ok:
                SetAuthValid();
                LastUnavailableReason = null;
                return true;

            case QobuzClient.QobuzSearchStatus.Blocked:
                LastUnavailableReason = result.Message
                    ?? "Qobuz blocked this account from downloading";
                SetAuth(PluginAuthState.Blocked, LastUnavailableReason,
                    AuthStatus.Account, AuthStatus.Plan, AuthStatus.Tier, AuthStatus.ExpiresAtUtc);
                Logger.LogDebug("Qobuz: {Reason}", LastUnavailableReason);
                StoreAvailability(false);
                return false;

            case QobuzClient.QobuzSearchStatus.Rejected:
                LastUnavailableReason = result.Message
                    ?? "The Qobuz token was rejected; paste a fresh one";
                SetAuth(PluginAuthState.Rejected, LastUnavailableReason,
                    AuthStatus.Account, AuthStatus.Plan, AuthStatus.Tier, AuthStatus.ExpiresAtUtc);
                Logger.LogDebug("Qobuz: {Reason}", LastUnavailableReason);
                StoreAvailability(false);
                return false;

            default:
                LastUnavailableReason = result.Message
                    ?? "The Qobuz API did not answer";
                SetAuth(PluginAuthState.Unreachable, LastUnavailableReason,
                    AuthStatus.Account, AuthStatus.Plan, AuthStatus.Tier, AuthStatus.ExpiresAtUtc);
                Logger.LogDebug("Qobuz: {Reason}", LastUnavailableReason);
                return false;
        }
    }

    /// <summary>
    /// Ranks the Qobuz results with the shared rules. An exact ISRC narrows
    /// the pool to the recording itself and is accepted on that code (the
    /// title spacing differs across catalogues), with the length still
    /// checked. Internal for tests.
    /// </summary>
    internal static DownloaderSearchHit? Rank(
        IReadOnlyList<QobuzClient.QobuzTrack> tracks, TrackQuery query,
        bool requireIsrcMatch = false)
    {
        var candidates = new List<(DownloaderSearchHit Hit, string? Isrc)>();
        foreach (var track in tracks)
        {
            var hit = new DownloaderSearchHit(
                Title: track.Title,
                Artist: track.Artist,
                SourceUrl: $"https://play.qobuz.com/track/{track.Id}",
                Duration: track.Duration,
                MatchConfidence: FuzzyMatcher.Confidence(
                    query.Artist, query.Title, hitArtist: track.Artist, hitTitle: track.Title),
                // The catalog already knows this track's ceiling. A
                // CD-only track reports 44.1 kHz even when the configured
                // tier allows hi-res, so the picker shows the real file
                // instead of the tier. When hi-res is not streamable,
                // CD quality is the ceiling whatever the master holds.
                BitDepth: track.HiresStreamable ? track.MaximumBitDepth : 16,
                SampleRateHz: track.HiresStreamable ? track.MaximumSamplingRate : 44100);
            candidates.Add((hit, track.Isrc));
        }

        if (candidates.Count == 0) return null;

        var isrcPool = candidates.Where(c =>
            !string.IsNullOrWhiteSpace(query.Isrc) &&
            !string.IsNullOrWhiteSpace(c.Isrc) &&
            string.Equals(query.Isrc.Trim(), c.Isrc.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        if (isrcPool.Count > 0)
            return SearchRanker.RankExactRecording(query.Artist, query.Title,
                query.ExpectedDuration, isrcPool.Select(c => c.Hit));
        if (requireIsrcMatch) return null;
        return SearchRanker.Rank(query.Artist, query.Title, query.ExpectedDuration,
            candidates.Select(c => c.Hit));
    }

    /// <inheritdoc />
    public async Task<CandidateAudioQuality?> ProbeQualityAsync(
        string sourceUrl, CancellationToken ct = default)
    {
        // Qobuz streams are lossless up to the configured tier; a track
        // that only exists in CD quality stays 16/44.1 whatever the tier
        // allows, so the answer is honest as "up to".
        var tier = NormalizeQuality(await ConfigValueAsync(QualityKey, ct));
        var (rate, depth) = tier switch
        {
            "7" => (96000, 24),
            "27" => (192000, 24),
            _ => (44100, 16),
        };
        return new CandidateAudioQuality(
            Codec: "flac", BitrateKbps: null,
            SampleRateHz: rate, BitDepth: depth, Lossless: true,
            Note: "up to your configured Qobuz tier");
    }

    // ── Download ─────────────────────────────────────────────────────────

    /// <summary>The hidden per-track folder under the output directory.</summary>
    protected override Task<string> WorkDirectoryAsync(
        string outputDirectory, string? melodyId, CancellationToken ct)
        => Task.FromResult(Path.Combine(outputDirectory,
            $".mb-qobuz-{(melodyId is { Length: > 0 } ? melodyId : Guid.NewGuid().ToString("N")[..8])}"));

    protected override async Task<CliToolRequest> BuildRequestAsync(
        string sourceUrl, DownloadQuality quality, string workDirectory, CancellationToken ct)
    {
        if (!TryExtractTrackId(sourceUrl, out var trackId))
            return new CliToolRequest([], "not a Qobuz track URL: " + sourceUrl);

        var configuredQuality = await ConfigValueAsync(QualityKey, ct);
        var qualityId = QualityFor(quality, configuredQuality);
        if (qualityId is null)
            return new CliToolRequest([],
                "no qobuz-dlp quality fits this quality band; skipping instead of wasting a download");

        return new CliToolRequest(
        [
            "dl",
            "--directory", workDirectory,
            "--quality", qualityId,
            "--no-m3u",
            "--no-db",
            "--embed-art",
            $"https://play.qobuz.com/track/{trackId}",
        ]);
    }

    /// <summary>
    /// Seeds the CLI config (app id, secrets, token) so qobuz-dlp never
    /// stops for an interactive setup, and points it at that folder.
    /// </summary>
    protected override async Task<IReadOnlyDictionary<string, string>?> PrepareEnvironmentAsync(
        CancellationToken ct)
    {
        var token = await TokenAsync(ct);
        await EnsureToolConfigAsync(token ?? "", ct);
        return ToolEnvironment();
    }

    /// <summary>
    /// The qobuz-dlp --quality value: FLAC requests use the configured
    /// tier, MP3 requests the 320 tier, and nothing else fits.
    /// Internal for tests.
    /// </summary>
    internal static string? QualityFor(DownloadQuality quality, string configuredQuality)
    {
        switch (quality.Format)
        {
            case AudioFormat.Flac:
                return NormalizeQuality(configuredQuality);
            case AudioFormat.Mp3:
                return quality.IsWithinBand(320) ? "5" : null;
            case AudioFormat.Aac:
            case AudioFormat.Opus:
                return null;
        }

        // Auto: the configured lossless tier.
        return NormalizeQuality(configuredQuality);
    }

    /// <summary>
    /// A qobuz-dlp failure can be the account being blocked or the token
    /// going stale mid-run. Only the tool's own words count: a network or
    /// quota error must not be called a login problem.
    /// </summary>
    protected override void ObserveFailedRun(CliRunner.Result result)
    {
        if (AnyFailureLine(result, line =>
                line.Contains("user_blocked", StringComparison.OrdinalIgnoreCase)
                || line.Contains("account is blocked", StringComparison.OrdinalIgnoreCase)
                || line.Contains("account blocked", StringComparison.OrdinalIgnoreCase)))
        {
            SetAuth(PluginAuthState.Blocked,
                "Qobuz blocked this account (USER_BLOCKED); the token is valid, but Qobuz refuses downloads for this account");
        }
        else if (AnyFailureLine(result, line =>
                     line.Contains("invalid credentials", StringComparison.OrdinalIgnoreCase)
                     || line.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
                     || line.Contains("invalid user auth token", StringComparison.OrdinalIgnoreCase)
                     || line.Contains("no user auth token", StringComparison.OrdinalIgnoreCase)
                     || line.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)))
        {
            SetAuth(PluginAuthState.Rejected,
                "The Qobuz token was rejected; paste a fresh one");
        }
    }

    /// <summary>A valid quality id, or the CD-quality default.</summary>
    internal static string NormalizeQuality(string configured)
        => configured.Trim() is "5" or "6" or "7" or "27" ? configured.Trim() : DefaultQuality;

    /// <summary>The configured FLAC tier as the card names it.</summary>
    internal static string TierLabel(string configured) => NormalizeQuality(configured) switch
    {
        "7" => "Hi-res up to 96 kHz",
        "27" => "Hi-res up to 192 kHz",
        _ => "CD quality (16/44.1)",
    };

    internal static bool TryExtractTrackId(string sourceUrl, out long trackId)
    {
        trackId = 0;
        if (string.IsNullOrWhiteSpace(sourceUrl)) return false;
        var lastSegment = sourceUrl.TrimEnd('/').Split('/')[^1].Split('?')[0];
        return long.TryParse(lastSegment, out trackId) && trackId > 0;
    }
}
