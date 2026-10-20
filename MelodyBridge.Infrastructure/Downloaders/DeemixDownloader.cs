using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Deezer;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// Deezer downloader plugin driven by the user's own deemix CLI
/// (https://github.com/bambanah/deemix). MelodyBridge searches Deezer's
/// public API and hands the track URL to the tool; deemix owns the login
/// (the ARL) and the download, and falls back within the account's tier on
/// its own. Works with a local `deemix` binary or the Docker image
/// ("docker exec Deemix deemix").
///
/// The shared CLI plumbing lives in <see cref="CliToolDownloader"/>; this
/// class only adds Deezer's search, the bitrate mapping and the ARL
/// seeding. Downloads land in a per-track folder under the downloads
/// folder (the plugin's own by default, or the one configured on the
/// Plugins page), and runs are serialized, so parallel tracks never pick
/// each other's files. Without an ARL the tool's own login is used, so a
/// deemix that is logged in elsewhere keeps working.
/// </summary>
public class DeemixDownloader : CliToolDownloader
{
    public const string DefaultCommand = "deemix";
    private const string CommandKey = "command";
    private const string DownloadsPathKey = "downloads_path";
    private const string ArlKey = "arl";

    // deemix downloads the best available quality up to the requested
    // maximum, and falls back within the account's tier on its own.
    public static readonly PluginCapabilities Caps = new(
    [
        new QualityDeliverable(AudioFormat.Flac, Lossless: true, 900, 10000),
        new QualityDeliverable(AudioFormat.Mp3, Lossless: false, 128, 320),
    ]);
    public override PluginCapabilities Capabilities => Caps;

    public override string Id => "deemix";
    public override string Name => "Deezer (deemix)";
    public override string Description => "Deezer downloads through your own deemix CLI: MP3 or FLAC with your ARL";

    public override IReadOnlyList<PluginConfigField> ConfigFields =>
    [
        new(ArlKey, "Deezer ARL", null,
            "From deezer.com: copy the 'arl' cookie in your browser's developer tools. A paid plan gives 320 and FLAC.",
            Secret: true),
        new(CommandKey, "deemix command", DefaultCommand,
            "Only needed when the deemix CLI is not on PATH; the Docker image ships it.",
            Advanced: true),
        new(DownloadsPathKey, "Downloads folder", null,
            "Leave empty and MelodyBridge uses its own folder. Set it only when deemix runs elsewhere, for example through docker exec.",
            Advanced: true),
    ];

    private readonly DeezerClient _deezer;

    /// <summary>Where the seeded ARL config lives; injectable for tests.</summary>
    private readonly string _dataDirectory;

    /// <summary>Where downloads land when no folder is configured; injectable for tests.</summary>
    private readonly string _defaultDownloadsDirectory;

    public DeemixDownloader(
        DeezerClient deezer, IPluginConfig config, ILogger<DeemixDownloader> logger,
        string? dataDirectory = null, string? downloadsDirectory = null)
        : base(config, logger)
    {
        _deezer = deezer;
        _dataDirectory = dataDirectory ?? DefaultDataDirectory();
        _defaultDownloadsDirectory = downloadsDirectory ?? DefaultDownloadsDirectory();
    }

    protected override string ToolLabel => "deemix";
    protected override string DefaultCommandLine => DefaultCommand;
    protected override string CommandConfigKey => CommandKey;
    protected override TimeSpan RunTimeout => TimeSpan.FromMinutes(15);

    protected override string NoFileMessage =>
        "deemix reported success but produced no audio file (is the downloads folder shared with deemix?)";

    /// <summary>
    /// The local data root for MelodyBridge-owned files: %LOCALAPPDATA%
    /// on Windows, ~/.local/share on Linux. .NET reports the Linux
    /// special folder as empty until that directory exists, which would
    /// otherwise turn these paths relative to the working directory.
    /// Internal for tests.
    /// </summary>
    internal static string LocalDataRoot()
        => LocalDataRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    internal static string LocalDataRoot(string localApplicationData, string userProfile)
    {
        if (!string.IsNullOrEmpty(localApplicationData)) return localApplicationData;
        return string.IsNullOrEmpty(userProfile)
            ? "."
            : Path.Combine(userProfile, ".local", "share");
    }

    /// <summary>
    /// The deemix config folder MelodyBridge manages
    /// (<c>%LOCALAPPDATA%</c> or <c>~/.local/share</c> plus
    /// <c>melodybridge/deemix</c>). Internal for tests.
    /// </summary>
    internal static string DefaultDataDirectory()
        => Path.Combine(LocalDataRoot(), "melodybridge", "deemix");

    /// <summary>
    /// Where deemix writes when the plugin has no folder configured: a
    /// folder MelodyBridge owns, so the common Docker setup needs no
    /// setting at all. Internal for tests.
    /// </summary>
    internal static string DefaultDownloadsDirectory()
        => Path.Combine(LocalDataRoot(), "melodybridge", "deemix-downloads");

    // ── Availability ─────────────────────────────────────────────────────

    public override async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (TryGetCachedAvailability(out var cached))
            return cached;

        if (!await CommandExistsAsync(ct))
        {
            LastUnavailableReason = "The deemix command was not found";
            SetAuth(PluginAuthState.Unknown);
            Logger.LogDebug("deemix: the command is not configured or not on PATH");
            StoreAvailability(false);
            return false;
        }

        var arl = await ConfigValueAsync(ArlKey, ct);
        if (arl.Length == 0)
        {
            // Without an ARL deemix uses its own login, which this app
            // cannot inspect. Let the CLI try.
            LastUnavailableReason = null;
            SetAuth(PluginAuthState.Unknown);
            StoreAvailability(true);
            return true;
        }

        var check = await _deezer.ValidateArlAsync(arl, ct);
        if (check.Status == DeezerClient.DeezerArlStatus.Invalid)
        {
            LastUnavailableReason = check.Message
                ?? "The Deezer ARL is no longer signed in; paste a fresh one";
            SetAuth(PluginAuthState.Expired, LastUnavailableReason);
            StoreAvailability(false);
            return false;
        }
        if (check.Status == DeezerClient.DeezerArlStatus.Unreachable)
        {
            // Never block downloads over the check itself: deemix may
            // still work while deezer.com blocks this network.
            LastUnavailableReason = null;
            SetAuth(PluginAuthState.Unknown,
                "The Deezer login could not be checked; downloads still try deemix");
            Logger.LogDebug("deemix: ARL check unavailable: {Reason}", check.Message);
            StoreAvailability(true);
            return true;
        }

        LastUnavailableReason = null;
        SetAuth(PluginAuthState.Valid,
            account: "Signed in",
            plan: check.Lossless == false ? "No lossless plan" : "Lossless",
            tier: check.Lossless == false ? "MP3 up to 320" : "FLAC available");
        if (check.Lossless == false)
            Logger.LogInformation("Deezer: the ARL is signed in without lossless");
        StoreAvailability(true);
        return true;
    }

    // ── Search ───────────────────────────────────────────────────────────

    public override async Task<DownloaderSearchHit?> SearchAsync(
        TrackQuery query, CancellationToken ct = default)
    {
        // Deezer's advanced search indexes ISRCs (isrc:"..."): the code
        // finds the exact recording even when the text search does not
        // return it. Only an exact match from that call is accepted.
        if (query.HasIsrc)
        {
            var byIsrc = await _deezer.SearchAsync($"isrc:\"{query.Isrc!}\"", 5, ct);
            if (Rank(byIsrc, query, requireIsrcMatch: true) is { } exact) return exact;
        }

        var searchText = query.SearchText;
        if (searchText.Length == 0) return null;

        var tracks = await _deezer.SearchAsync(searchText, 10, ct);
        return Rank(tracks, query);
    }

    /// <summary>
    /// Ranks the public Deezer results with the shared rules. An exact
    /// ISRC narrows the pool to the recording itself and is accepted on
    /// that code (the title wording differs across catalogues), with the
    /// length still checked. Internal for tests.
    /// </summary>
    internal static DownloaderSearchHit? Rank(
        IReadOnlyList<DeezerClient.DeezerTrack> tracks, TrackQuery query,
        bool requireIsrcMatch = false)
    {
        var candidates = new List<(DownloaderSearchHit Hit, string? Isrc)>();
        foreach (var track in tracks)
        {
            var hit = new DownloaderSearchHit(
                Title: track.Title,
                Artist: track.Artist,
                SourceUrl: $"https://www.deezer.com/track/{track.Id}",
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
        if (isrcPool.Count > 0)
            return SearchRanker.RankExactRecording(query.Artist, query.Title,
                query.ExpectedDuration, isrcPool.Select(c => c.Hit));
        if (requireIsrcMatch) return null;
        return SearchRanker.Rank(query.Artist, query.Title, query.ExpectedDuration,
            candidates.Select(c => c.Hit));
    }

    // ── Download ─────────────────────────────────────────────────────────

    /// <summary>
    /// The per-track folder deemix writes into: under the configured
    /// downloads folder, or the plugin's own folder when none is set.
    /// </summary>
    protected override async Task<string> WorkDirectoryAsync(
        string outputDirectory, string? melodyId, CancellationToken ct)
    {
        var downloadsPath = await ConfigValueAsync(DownloadsPathKey, ct);
        if (downloadsPath.Length == 0)
            downloadsPath = _defaultDownloadsDirectory;

        return Path.Combine(downloadsPath,
            $"mb-{(melodyId is { Length: > 0 } ? melodyId : Guid.NewGuid().ToString("N")[..8])}");
    }

    protected override Task<CliToolRequest> BuildRequestAsync(
        string sourceUrl, DownloadQuality quality, string workDirectory, CancellationToken ct)
    {
        if (!TryExtractTrackId(sourceUrl, out var trackId))
            return Task.FromResult(new CliToolRequest([], "not a Deezer track URL: " + sourceUrl));

        var bitrate = BitrateFor(quality);
        if (bitrate is null)
            return Task.FromResult(new CliToolRequest([],
                "no deemix bitrate fits this quality band; skipping instead of wasting a download"));

        return Task.FromResult(new CliToolRequest(
        [
            "-b", bitrate,
            "-p", workDirectory,
            $"https://www.deezer.com/track/{trackId}",
        ]));
    }

    /// <summary>
    /// With an ARL configured, MelodyBridge owns deemix's config: the
    /// login is seeded so the CLI never prompts, whatever state the
    /// container is in. Without one the tool's own login is left alone.
    /// </summary>
    protected override Task<IReadOnlyDictionary<string, string>?> PrepareEnvironmentAsync(
        CancellationToken ct)
        => SeedArlAsync(ct);

    /// <summary>
    /// Writes the ARL into deemix's config folder and returns the env
    /// that points the CLI at it; null when no ARL is configured, in which
    /// case the tool's own login is used. Internal for tests.
    /// </summary>
    internal async Task<IReadOnlyDictionary<string, string>?> SeedArlAsync(CancellationToken ct)
    {
        var arl = await ConfigValueAsync(ArlKey, ct);
        if (arl.Length == 0) return null;

        Directory.CreateDirectory(_dataDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(_dataDirectory, "login.json"),
            JsonSerializer.Serialize(new { arl }), ct);
        return new Dictionary<string, string> { ["DEEMIX_DATA_DIR"] = _dataDirectory };
    }

    /// <summary>The deemix -b value for a quality, or null when nothing fits.</summary>
    internal static string? BitrateFor(DownloadQuality quality)
    {
        switch (quality.Format)
        {
            case AudioFormat.Flac:
                return "flac";
            case AudioFormat.Mp3:
                if (quality.IsWithinBand(320)) return "320";
                if (quality.IsWithinBand(128)) return "128";
                return null;
            case AudioFormat.Aac:
            case AudioFormat.Opus:
                return null;
        }

        // Auto: the best the account allows; deemix falls back on its own.
        return "flac";
    }

    /// <summary>
    /// deemix says when the ARL stopped working. Only those lines count;
    /// a track missing at a tier is not a login problem.
    /// </summary>
    protected override void ObserveFailedRun(CliRunner.Result result)
    {
        var text = (result.StdErr + "\n" + result.StdOut).ToLowerInvariant();
        if (text.Contains("arl", StringComparison.Ordinal)
            && (text.Contains("invalid", StringComparison.Ordinal)
                || text.Contains("expired", StringComparison.Ordinal)
                || text.Contains("not logged", StringComparison.Ordinal)
                || text.Contains("login", StringComparison.Ordinal)))
        {
            SetAuth(PluginAuthState.Expired,
                "The Deezer ARL is no longer signed in; paste a fresh one");
        }
        else if (text.Contains("not logged in", StringComparison.Ordinal)
                 || text.Contains("login required", StringComparison.Ordinal)
                 || text.Contains("deezer connect", StringComparison.Ordinal))
        {
            SetAuth(PluginAuthState.Expired,
                "The Deezer ARL is no longer signed in; paste a fresh one");
        }
    }

    internal static bool TryExtractTrackId(string sourceUrl, out long trackId)
    {
        trackId = 0;
        if (string.IsNullOrWhiteSpace(sourceUrl)) return false;
        var lastSegment = sourceUrl.TrimEnd('/').Split('/')[^1].Split('?')[0];
        return long.TryParse(lastSegment, out trackId) && trackId > 0;
    }
}
