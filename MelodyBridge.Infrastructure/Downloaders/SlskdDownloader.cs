using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Files;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// Soulseek downloader plugin backed by the user's own slskd instance
/// (https://github.com/slskd/slskd). No cloud dependency: SlskdSearchProvider
/// searches the network, then this downloader enqueues the transfer from the
/// URL it gets back, polls it, and moves the finished file out of slskd's
/// downloads folder into the playlist folder.
///
/// Setup: run slskd, give it a Soulseek account, create a read-write API
/// key, and point this plugin at its URL and key. The plugin asks slskd
/// where it writes finished downloads and reads the file from there, so
/// that folder must be visible to MelodyBridge at the same path; the
/// downloads folder field is the override for when it is not (a container
/// mount). Sharing some music in slskd is expected on the network; peers
/// regularly refuse downloads from empty shares.
///
/// API shape (slskd v0.x, live-probed):
///   GET  /api/v0/application                    → {"server":{"isConnected","isLoggedIn"}}
///   POST /api/v0/transfers/downloads/{username} → {"enqueued":[{"id",…}]}
///   GET  /api/v0/transfers/downloads/{username} → {"directories":[{"files":[{"state",…}]}]}
/// </summary>
public class SlskdDownloader : IDownloader
{
    public const string DefaultBaseUrl = SlskdSession.DefaultBaseUrl;
    internal const string BaseUrlKey = SlskdSession.BaseUrlKey;
    internal const string ApiKeyKey = SlskdSession.ApiKeyKey;
    internal const string DownloadsPathKey = SlskdSession.DownloadsPathKey;

    // What peers actually serve: lossless FLAC (with bit depth and sample
    // rate in the search response) and lossy files at whatever the uploader
    // ripped. The file filter selects within the requested band, so the
    // lossy tiers are controllable; the post-download measurement still
    // has the last word on what really arrived.
    public static readonly PluginCapabilities Caps = new(
    [
        new QualityDeliverable(AudioFormat.Flac, Lossless: true, 900, 10000),
        new QualityDeliverable(AudioFormat.Mp3, Lossless: false, 128, 320),
        new QualityDeliverable(AudioFormat.Aac, Lossless: false, 128, 320),
    ]);
    public PluginCapabilities Capabilities => Caps;

    public string Id => "slskd";
    public string Name => "Soulseek (slskd)";
    public string Description => "Lossless and high-bitrate files from Soulseek peers through your own slskd instance";

    public DownloaderSearchInfo SearchInfo =>
        DownloaderSearchInfo.ViaProvider(SearchData.Text, "Soulseek peer search via slskd");

    public string IconName => "server";
    public string? GuideUrl => "https://docs.melodybridge.app/slskd";
    public IReadOnlyList<string> Notes => new[] { "lossless FLAC", "your own slskd" };

    public IReadOnlyList<PluginConfigField> ConfigFields =>
    [
        new(BaseUrlKey, "slskd URL", DefaultBaseUrl,
            "Base URL of your slskd instance, for example http://192.168.1.10:5030."),
        new(ApiKeyKey, "API key", null,
            "A read-write API key from slskd's web.authentication.api_keys list.",
            Secret: true),
        new(DownloadsPathKey, "Downloads folder", null,
            "MelodyBridge asks slskd for this folder; set it here only when the app sees it at another path.",
            Advanced: true, Folder: true),
    ];

    /// <inheritdoc />
    public string? LastUnavailableReason => _session.LastUnavailableReason;

    private readonly SlskdSession _session;
    private readonly ILogger<SlskdDownloader> _logger;

    /// <summary>
    /// How long a transfer may take, queue time included. The waterfall's
    /// plugin budget usually caps this first; raise that budget when you
    /// want Soulseek queues to be waited out.
    /// </summary>
    internal TimeSpan TransferTimeout { get; set; } = TimeSpan.FromMinutes(20);

    internal TimeSpan TransferPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    public SlskdDownloader(SlskdSession session, ILogger<SlskdDownloader> logger)
    {
        _session = session;
        _logger = logger;
    }

    /// <summary>Test and one-off host convenience: creates the session from the same inputs.</summary>
    public SlskdDownloader(HttpClient http, IPluginConfig config, ILogger<SlskdDownloader> logger)
        : this(new SlskdSession(http, config,
            NullLogger<SlskdSession>.Instance), logger)
    {
    }

    internal string? DetectedDownloadsPath => _session.DetectedDownloadsPath;

    public void RefreshAvailability() => _session.RefreshAvailability();

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => _session.IsAvailableAsync(ct);

    /// <summary>Provider-backed: the search lives in SlskdSearchProvider.</summary>
    public Task<DownloaderSearchHit?> SearchAsync(
        TrackQuery query, CancellationToken ct = default)
        => Task.FromResult<DownloaderSearchHit?>(null);

    // ── Transfer reference ───────────────────────────────────────────────

    internal static bool TryParseSourceUrl(
        string sourceUrl, out string username, out string filename, out long size)
    {
        username = "";
        filename = "";
        size = 0;
        if (string.IsNullOrWhiteSpace(sourceUrl) || !sourceUrl.StartsWith("slskd:", StringComparison.Ordinal))
            return false;

        var parts = sourceUrl.Split(':');
        if (parts.Length != 4) return false;
        username = Uri.UnescapeDataString(parts[1]);
        filename = Uri.UnescapeDataString(parts[2]);
        return username.Length > 0
               && filename.Length > 0
               && long.TryParse(parts[3], out size);
    }

    // ── Download ─────────────────────────────────────────────────────────

    public async Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl,
        string outputDirectory,
        string? melodyId,
        DownloadQuality? quality = null,
        CancellationToken ct = default)
    {
        if (!TryParseSourceUrl(sourceUrl, out var username, out var filename, out var size))
            return new DownloaderDownloadResult(false, null, "not a Soulseek transfer reference: " + sourceUrl);

        var downloadsPath = await _session.DownloadsPathAsync(ct);
        if (downloadsPath is null)
            return new DownloaderDownloadResult(false, null,
                "slskd's downloads folder could not be read (set it under Advanced)");

        try
        {
            Directory.CreateDirectory(outputDirectory);

            using (var enqueued = await _session.SendJsonAsync(
                       HttpMethod.Post, $"/api/v0/transfers/downloads/{Uri.EscapeDataString(username)}",
                       new[] { new { filename, size } }, ct))
            {
                if (enqueued is null)
                    return new DownloaderDownloadResult(false, null, $"slskd refused the transfer for '{filename}'");
            }

            // Wait for slskd to finish it: queue time included, because a
            // busy peer is normal on Soulseek.
            var state = await WaitForTransferAsync(username, filename, ct);
            if (state is null)
                return new DownloaderDownloadResult(false, null,
                    $"slskd transfer for '{filename}' did not finish");
            if (!state.Contains("Succeeded", StringComparison.OrdinalIgnoreCase))
                return new DownloaderDownloadResult(false, null, $"slskd transfer failed: {state}");

            var downloaded = FindDownloadedFile(downloadsPath, filename);
            if (downloaded is null)
            {
                // slskd's answer may be stale (it moved its downloads
                // folder, or a second instance took over the URL): drop it
                // so the next attempt asks again instead of failing forever.
                _session.ForgetDetectedDownloadsPath();
                return new DownloaderDownloadResult(false, null,
                    "slskd reported success but the file is not in the downloads folder");
            }

            var target = FileMove.UniqueTarget(outputDirectory, Path.GetFileName(downloaded));
            FileMove.MoveAcrossDevices(downloaded, target);

            // An error page saved under an audio name must never pass: sniff
            // the real bytes before accepting, and fix the extension.
            var container = AudioSignature.EnsureExtension(target);
            if (container is null)
            {
                FileCleanup.DeleteQuietly(target);
                return new DownloaderDownloadResult(false, null, "the Soulseek file is not audio");
            }

            YtDlpDownloader.TagDownloadedFile(container, expectedTitle: null);
            return new DownloaderDownloadResult(true, container, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // caller cancellation (or the plugin budget): the manager's call
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "slskd download for '{File}' failed", filename);
            return new DownloaderDownloadResult(false, null, ex.Message);
        }
    }

    /// <summary>The terminal transfer state, or null when it never finished.</summary>
    private async Task<string?> WaitForTransferAsync(
        string username, string filename, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + TransferTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            using (var transfers = await _session.SendJsonAsync(
                       HttpMethod.Get, $"/api/v0/transfers/downloads/{Uri.EscapeDataString(username)}", null, ct))
            {
                var state = transfers is null ? null : FindTransferState(transfers.RootElement, filename);
                if (state is not null && TransferIsTerminal(state)) return state;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                _logger.LogDebug("slskd transfer for '{File}' did not finish within {Minutes:0} minutes",
                    filename, TransferTimeout.TotalMinutes);
                return null;
            }
            await Task.Delay(TransferPollInterval, ct);
        }
    }

    /// <summary>The state of one transfer in a downloads response, or null.</summary>
    internal static string? FindTransferState(JsonElement root, string filename)
    {
        if (!root.TryGetProperty("directories", out var directories) ||
            directories.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var directory in directories.EnumerateArray())
        {
            if (!directory.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var file in files.EnumerateArray())
            {
                var name = SlskdJson.GetString(file, "filename");
                if (name is not null && string.Equals(name, filename, StringComparison.OrdinalIgnoreCase))
                    return SlskdJson.GetString(file, "state");
            }
        }
        return null;
    }

    /// <summary>True when the state will not change anymore.</summary>
    internal static bool TransferIsTerminal(string state)
        => state.Contains("Succeeded", StringComparison.OrdinalIgnoreCase)
        || state.Contains("Completed", StringComparison.OrdinalIgnoreCase)
        || state.Contains("Errored", StringComparison.OrdinalIgnoreCase)
        || state.Contains("Rejected", StringComparison.OrdinalIgnoreCase)
        || state.Contains("Aborted", StringComparison.OrdinalIgnoreCase)
        || state.Contains("Cancelled", StringComparison.OrdinalIgnoreCase)
        || state.Contains("FileNotShared", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds the finished file under slskd's downloads folder. slskd
    /// flattens the remote path (peer folders are not recreated), so the
    /// search is by file name; the newest match wins when several peers
    /// served the same name.
    /// </summary>
    internal static string? FindDownloadedFile(string downloadsRoot, string remoteFilename)
    {
        var basename = Path.GetFileName(remoteFilename.Replace('\\', '/'));
        if (basename.Length == 0 || !Directory.Exists(downloadsRoot)) return null;

        try
        {
            string? best = null;
            var bestWritten = DateTime.MinValue;
            foreach (var file in Directory.EnumerateFiles(downloadsRoot, "*", SearchOption.AllDirectories))
            {
                if (!string.Equals(Path.GetFileName(file), basename, StringComparison.OrdinalIgnoreCase))
                    continue;
                var written = File.GetLastWriteTimeUtc(file);
                if (best is null || written > bestWritten)
                {
                    best = file;
                    bestWritten = written;
                }
            }
            return best;
        }
        catch (Exception)
        {
            return null;
        }
    }


}
