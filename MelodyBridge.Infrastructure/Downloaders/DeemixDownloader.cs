using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Deezer;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// Deezer downloader plugin driven by the user's own deemix CLI
/// (https://github.com/bambanah/deemix), the yt-dlp model: the tool owns
/// the login (ARL) and the download, MelodyBridge only searches and
/// hands over a track URL. Works with a local `deemix` binary or the
/// Docker image ("docker exec Deemix deemix").
///
/// Downloads land in a per-track folder under the configured downloads
/// path, so parallel tracks never pick each other's files, and CLI runs
/// are serialized anyway because deemix writes into a shared folder.
/// </summary>
public class DeemixDownloader : IDownloader
{
    public const string DefaultCommand = "deemix";
    private const string CommandKey = "command";
    private const string DownloadsPathKey = "downloads_path";

    // deemix downloads the best available quality up to the requested
    // maximum, and falls back within the account's tier on its own.
    public static readonly PluginCapabilities Caps = new(
    [
        new QualityDeliverable(AudioFormat.Flac, Lossless: true, 900, 10000),
        new QualityDeliverable(AudioFormat.Mp3, Lossless: false, 128, 320),
    ]);
    public PluginCapabilities Capabilities => Caps;

    public string Id => "deemix";
    public string Name => "Deezer (deemix)";
    public string Description => "Deezer downloads through your own deemix CLI: MP3 or FLAC with your ARL";

    public IReadOnlyList<PluginConfigField> ConfigFields =>
    [
        new(CommandKey, "deemix command", DefaultCommand,
            "The deemix command. In Docker use \"docker exec Deemix deemix\"."),
        new(DownloadsPathKey, "Downloads folder", null,
            "The folder deemix writes to, as both deemix and MelodyBridge see it (mount it at the same path)."),
    ];

    // One CLI run at a time: deemix writes into a shared folder, and two
    // concurrent runs could pick each other's files.
    private static readonly SemaphoreSlim RunGate = new(1, 1);

    private static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(15);

    private readonly DeezerClient _deezer;
    private readonly IPluginConfig _config;
    private readonly ILogger<DeemixDownloader> _logger;
    private readonly AvailabilityCache _availability = new();

    public DeemixDownloader(
        DeezerClient deezer, IPluginConfig config, ILogger<DeemixDownloader> logger)
    {
        _deezer = deezer;
        _config = config;
        _logger = logger;
    }

    // ── Availability ─────────────────────────────────────────────────────

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_availability.TryGet(out var cached))
            return cached;

        var command = await CommandAsync(ct);
        var available = command is not null && CliRunner.Exists(command.Value.Executable);
        if (!available)
            _logger.LogDebug("deemix: the command is not configured or not on PATH");
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

    // ── Search ───────────────────────────────────────────────────────────

    public async Task<DownloaderSearchHit?> SearchAsync(
        TrackQuery query, CancellationToken ct = default)
    {
        var searchText = query.SearchText;
        if (searchText.Length == 0) return null;

        var tracks = await _deezer.SearchAsync(searchText, 10, ct);
        return Rank(tracks, query);
    }

    /// <summary>
    /// Ranks the public Deezer results with the shared rules; an exact
    /// ISRC match narrows the pool to the recording itself. Internal for
    /// tests.
    /// </summary>
    internal static DownloaderSearchHit? Rank(
        IReadOnlyList<DeezerClient.DeezerTrack> tracks, TrackQuery query)
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
            return new DownloaderDownloadResult(false, null, "not a Deezer track URL: " + sourceUrl);

        var command = await CommandAsync(ct);
        if (command is null || !CliRunner.Exists(command.Value.Executable))
            return new DownloaderDownloadResult(false, null,
                "the deemix command is not configured or not on PATH");

        var downloadsPath = (await _config.GetAsync(Id, DownloadsPathKey, ct)).Trim();
        if (downloadsPath.Length == 0)
            return new DownloaderDownloadResult(false, null,
                "the deemix downloads folder is not configured");

        var bitrate = BitrateFor(quality ?? DownloadQuality.Any);
        if (bitrate is null)
            return new DownloaderDownloadResult(false, null,
                "no deemix bitrate fits this quality band; skipping instead of wasting a download");

        Directory.CreateDirectory(outputDirectory);
        var downloadDir = Path.Combine(downloadsPath,
            $"mb-{(melodyId is { Length: > 0 } ? melodyId : Guid.NewGuid().ToString("N")[..8])}");
        Directory.CreateDirectory(downloadDir);

        await RunGate.WaitAsync(ct);
        try
        {
            var arguments = new List<string>(command.Value.Arguments)
            {
                "-b", bitrate,
                "-p", downloadDir,
                $"https://www.deezer.com/track/{trackId}",
            };
            _logger.LogDebug("deemix: running {Command} {Args}",
                command.Value.Executable, string.Join(" ", arguments));

            var result = await CliRunner.RunAsync(
                command.Value.Executable, arguments, RunTimeout, ct);
            if (!result.Success)
            {
                var reason = DescribeFailure(result);
                _logger.LogWarning("deemix failed for track {TrackId}: {Reason}", trackId, reason);
                return new DownloaderDownloadResult(false, null, reason);
            }

            var produced = CliRunner.FindNewestAudioFile(downloadDir);
            if (produced is null)
                return new DownloaderDownloadResult(false, null,
                    "deemix reported success but produced no audio file (is the downloads folder shared with deemix?)");

            var target = FileMove.UniqueTarget(outputDirectory, Path.GetFileName(produced));
            FileMove.MoveAcrossDevices(produced, target);

            var container = AudioSignature.EnsureExtension(target);
            if (container is null)
            {
                FileCleanup.DeleteQuietly(target);
                return new DownloaderDownloadResult(false, null, "the deemix file is not audio");
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
            _logger.LogDebug(ex, "deemix download for track {TrackId} failed", trackId);
            return new DownloaderDownloadResult(false, null, ex.Message);
        }
        finally
        {
            RunGate.Release();
            try { Directory.Delete(downloadDir, recursive: true); } catch { /* best effort */ }
        }
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

    /// <summary>The last meaningful line of a failed CLI run.</summary>
    internal static string DescribeFailure(CliRunner.Result result)
    {
        var text = string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr;
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var last = lines.Length > 0 ? lines[^1] : "no output";
        if (last.Length > 300) last = last[..300] + "...";
        return $"deemix failed (exit {result.ExitCode}): {last}";
    }

    internal static bool TryExtractTrackId(string sourceUrl, out long trackId)
    {
        trackId = 0;
        if (string.IsNullOrWhiteSpace(sourceUrl)) return false;
        var lastSegment = sourceUrl.TrimEnd('/').Split('/')[^1].Split('?')[0];
        return long.TryParse(lastSegment, out trackId) && trackId > 0;
    }
}
