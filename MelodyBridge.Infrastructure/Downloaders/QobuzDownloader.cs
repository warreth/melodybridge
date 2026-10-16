using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Files;
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
/// under the output directory and the finished file moves out.
/// </summary>
public class QobuzDownloader : IDownloader
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
    public PluginCapabilities Capabilities => Caps;

    public string Id => "qobuz";
    public string Name => "Qobuz";
    public string Description => "Lossless and hi-res downloads through your own qobuz-dlp";

    public IReadOnlyList<PluginConfigField> ConfigFields =>
    [
        new(CommandKey, "qobuz-dlp command", DefaultCommand,
            "The qobuz-dlp command, for example a full path when it is not on PATH."),
        new(TokenKey, "User auth token (optional)", null,
            "Used for the search. Empty reads qobuz-dlp's own token file, so a logged-in qobuz-dlp just works."),
        new(QualityKey, "FLAC quality", DefaultQuality,
            "6 = 16/44 lossless, 7 = 24-bit up to 96 kHz, 27 = 24-bit above 96 kHz."),
    ];

    // One CLI run at a time: qobuz-dlp keeps a small database and log in
    // its config folder, and serial runs keep that clean.
    private static readonly SemaphoreSlim RunGate = new(1, 1);

    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(20);

    private readonly QobuzClient _qobuz;
    private readonly IPluginConfig _config;
    private readonly ILogger<QobuzDownloader> _logger;
    private readonly AvailabilityCache _availability = new();

    public QobuzDownloader(
        QobuzClient qobuz, IPluginConfig config, ILogger<QobuzDownloader> logger)
    {
        _qobuz = qobuz;
        _config = config;
        _logger = logger;
    }

    // ── Availability ─────────────────────────────────────────────────────

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_availability.TryGet(out var cached))
            return cached;

        var command = await CommandAsync(ct);
        if (command is null || !CliRunner.Exists(command.Value.Executable))
        {
            _logger.LogDebug("qobuz: the qobuz-dlp command is not configured or not on PATH");
            _availability.Store(false);
            return false;
        }

        var token = await TokenAsync(ct);
        if (token is null)
        {
            _logger.LogDebug("qobuz: no token configured and qobuz-dlp has no token file");
            _availability.Store(false);
            return false;
        }

        var account = await _qobuz.ValidateTokenAsync(token, ct);
        var available = account is { CanDownload: true };
        if (account is not null && !account.CanDownload)
            _logger.LogInformation("Qobuz: this account cannot download (a paid plan is required)");
        _availability.Store(available);
        return available;
    }

    private async Task<(string Executable, IReadOnlyList<string> Arguments)?> CommandAsync(
        CancellationToken ct)
    {
        var configured = await _config.GetAsync(Id, CommandKey, ct);
        var line = string.IsNullOrWhiteSpace(configured) ? DefaultCommand : configured.Trim();
        return CliRunner.SplitCommand(line);
    }

    /// <summary>
    /// The user token: the plugin setting wins, an empty setting falls
    /// back to qobuz-dlp's own token file so a logged-in tool just works.
    /// </summary>
    private async Task<string?> TokenAsync(CancellationToken ct)
    {
        var configured = (await _config.GetAsync(Id, TokenKey, ct)).Trim();
        return configured.Length > 0 ? configured : ReadQobuzDlpTokenFile();
    }

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

    public async Task<DownloaderSearchHit?> SearchAsync(
        TrackQuery query, CancellationToken ct = default)
    {
        var searchText = query.SearchText;
        if (searchText.Length == 0) return null;

        var token = await TokenAsync(ct);
        if (token is null) return null;

        var tracks = await _qobuz.SearchTracksAsync(searchText, 10, token, ct);
        return Rank(tracks, query);
    }

    /// <summary>
    /// Ranks the Qobuz results with the shared rules; an exact ISRC match
    /// narrows the pool to the recording itself. Internal for tests.
    /// </summary>
    internal static DownloaderSearchHit? Rank(
        IReadOnlyList<QobuzClient.QobuzTrack> tracks, TrackQuery query)
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

    public async Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl,
        string outputDirectory,
        string? melodyId,
        DownloadQuality? quality = null,
        CancellationToken ct = default)
    {
        if (!TryExtractTrackId(sourceUrl, out var trackId))
            return new DownloaderDownloadResult(false, null, "not a Qobuz track URL: " + sourceUrl);

        var command = await CommandAsync(ct);
        if (command is null || !CliRunner.Exists(command.Value.Executable))
            return new DownloaderDownloadResult(false, null,
                "the qobuz-dlp command is not configured or not on PATH");

        var configuredQuality = (await _config.GetAsync(Id, QualityKey, ct)).Trim();
        var qualityId = QualityFor(quality ?? DownloadQuality.Any, configuredQuality);
        if (qualityId is null)
            return new DownloaderDownloadResult(false, null,
                "no qobuz-dlp quality fits this quality band; skipping instead of wasting a download");

        Directory.CreateDirectory(outputDirectory);
        var tempDir = Path.Combine(outputDirectory,
            $".mb-qobuz-{(melodyId is { Length: > 0 } ? melodyId : Guid.NewGuid().ToString("N")[..8])}");
        Directory.CreateDirectory(tempDir);

        await RunGate.WaitAsync(ct);
        try
        {
            var arguments = new List<string>(command.Value.Arguments)
            {
                "dl",
                "--directory", tempDir,
                "--quality", qualityId,
                "--no-m3u",
                "--no-db",
                "--embed-art",
                $"https://play.qobuz.com/track/{trackId}",
            };
            _logger.LogDebug("qobuz: running {Command} {Args}",
                command.Value.Executable, string.Join(" ", arguments));

            var result = await CliRunner.RunAsync(
                command.Value.Executable, arguments, RunTimeout, ct);
            if (!result.Success)
            {
                var reason = DescribeFailure(result);
                _logger.LogWarning("qobuz-dlp failed for track {TrackId}: {Reason}", trackId, reason);
                return new DownloaderDownloadResult(false, null, reason);
            }

            var produced = CliRunner.FindNewestAudioFile(tempDir);
            if (produced is null)
                return new DownloaderDownloadResult(false, null,
                    "qobuz-dlp reported success but produced no audio file");

            var target = FileMove.UniqueTarget(outputDirectory, Path.GetFileName(produced));
            FileMove.MoveAcrossDevices(produced, target);

            var container = AudioSignature.EnsureExtension(target);
            if (container is null)
            {
                FileCleanup.DeleteQuietly(target);
                return new DownloaderDownloadResult(false, null, "the qobuz-dlp file is not audio");
            }

            YtDlpDownloader.TagDownloadedFile(container, expectedTitle: null);
            return new DownloaderDownloadResult(true, container, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "qobuz-dlp download for track {TrackId} failed", trackId);
            return new DownloaderDownloadResult(false, null, ex.Message);
        }
        finally
        {
            RunGate.Release();
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
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

    /// <summary>A valid quality id, or the CD-quality default.</summary>
    internal static string NormalizeQuality(string configured)
        => configured.Trim() is "5" or "6" or "7" or "27" ? configured.Trim() : DefaultQuality;

    /// <summary>The last meaningful line of a failed CLI run.</summary>
    internal static string DescribeFailure(CliRunner.Result result)
    {
        var text = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var last = lines.Length > 0 ? lines[^1] : "no output";
        if (last.Length > 300) last = last[..300] + "...";
        return $"qobuz-dlp failed (exit {result.ExitCode}): {last}";
    }

    internal static bool TryExtractTrackId(string sourceUrl, out long trackId)
    {
        trackId = 0;
        if (string.IsNullOrWhiteSpace(sourceUrl)) return false;
        var lastSegment = sourceUrl.TrimEnd('/').Split('/')[^1].Split('?')[0];
        return long.TryParse(lastSegment, out trackId) && trackId > 0;
    }
}
