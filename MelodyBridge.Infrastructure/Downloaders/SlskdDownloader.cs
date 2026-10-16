using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// Soulseek downloader plugin backed by the user's own slskd instance
/// (https://github.com/slskd/slskd). No cloud dependency: the search runs
/// on the Soulseek network through slskd's REST API, the transfer is
/// enqueued and polled, and the finished file is moved out of slskd's
/// downloads folder into the playlist folder.
///
/// Setup: run slskd, give it a Soulseek account, create a read-write API
/// key, and point this plugin at its URL, key and downloads folder. That
/// folder must be the directory MelodyBridge sees (a shared volume when
/// both run in containers). Sharing some music in slskd is expected on
/// the network; peers regularly refuse downloads from empty shares.
///
/// API shape (slskd v0.x, live-probed):
///   GET  /api/v0/application                    → {"server":{"isConnected","isLoggedIn"}}
///   POST /api/v0/searches {"searchText"}        → {"id"}
///   GET  /api/v0/searches/{id}                  → {"state":"Completed, …"}
///   GET  /api/v0/searches/{id}/responses        → [{"username","hasFreeUploadSlot","files":[…]}]
///   POST /api/v0/transfers/downloads/{username} → {"enqueued":[{"id",…}]}
///   GET  /api/v0/transfers/downloads/{username} → {"directories":[{"files":[{"state",…}]}]}
/// </summary>
public class SlskdDownloader : IDownloader
{
    public const string DefaultBaseUrl = "http://127.0.0.1:5030";
    private const string BaseUrlKey = "base_url";
    private const string ApiKeyKey = "api_key";
    private const string DownloadsPathKey = "downloads_path";

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

    public IReadOnlyList<PluginConfigField> ConfigFields =>
    [
        new(BaseUrlKey, "slskd URL", DefaultBaseUrl,
            "Base URL of your slskd instance, for example http://192.168.1.10:5030."),
        new(ApiKeyKey, "API key", null,
            "A read-write API key from slskd's web.authentication.api_keys list."),
        new(DownloadsPathKey, "Downloads folder", null,
            "The folder slskd writes finished downloads to, as MelodyBridge sees it. Share this folder between the two."),
    ];

    private readonly HttpClient _http;
    private readonly IPluginConfig _config;
    private readonly ILogger<SlskdDownloader> _logger;

    // Availability probe cache: the waterfall asks per track, the Plugins
    // page re-checks on a timer.
    private readonly AvailabilityCache _availability = new();

    /// <summary>Per-request deadline for the small JSON calls.</summary>
    internal TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How long a Soulseek search may take before giving up.</summary>
    internal TimeSpan SearchPollTimeout { get; set; } = TimeSpan.FromSeconds(30);

    internal TimeSpan SearchPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long a transfer may take, queue time included. The waterfall's
    /// plugin budget usually caps this first; raise that budget when you
    /// want Soulseek queues to be waited out.
    /// </summary>
    internal TimeSpan TransferTimeout { get; set; } = TimeSpan.FromMinutes(20);

    internal TimeSpan TransferPollInterval { get; set; } = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(8);
    private static readonly Regex TrackNumberPrefix = new(@"^\d{1,3}[\s._-]+", RegexOptions.Compiled);

    public SlskdDownloader(HttpClient http, IPluginConfig config, ILogger<SlskdDownloader> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    // ── Config ───────────────────────────────────────────────────────────

    private async Task<string> BaseUrlAsync(CancellationToken ct)
    {
        var configured = await _config.GetAsync(Id, BaseUrlKey, ct);
        if (string.IsNullOrWhiteSpace(configured)) return DefaultBaseUrl;
        return configured.Trim().TrimEnd('/');
    }

    private async Task<string> ApiKeyAsync(CancellationToken ct)
        => (await _config.GetAsync(Id, ApiKeyKey, ct)).Trim();

    private async Task<string> DownloadsPathAsync(CancellationToken ct)
        => (await _config.GetAsync(Id, DownloadsPathKey, ct)).Trim();

    // ── Availability ─────────────────────────────────────────────────────

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_availability.TryGet(out var cached))
            return cached;

        // Not configured is not available, exactly like a missing binary:
        // the waterfall skips the plugin without a single request.
        if ((await ApiKeyAsync(ct)).Length == 0 || (await DownloadsPathAsync(ct)).Length == 0)
        {
            _logger.LogDebug("slskd is not configured: set the API key and downloads folder on the Plugins page");
            _availability.Store(false);
            return false;
        }

        try
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(HealthTimeout);
            using var application = await SendJsonAsync(HttpMethod.Get, "/api/v0/application", null, probe.Token);
            var available = application is not null && IsServerLoggedIn(application.RootElement);
            if (!available)
                _logger.LogDebug("slskd is reachable but not logged in to the Soulseek network");
            _availability.Store(available);
            return available;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "slskd health probe failed");
            _availability.Store(false);
            return false;
        }
    }

    /// <summary>True when slskd reports a connected, logged-in server.</summary>
    internal static bool IsServerLoggedIn(JsonElement root)
    {
        if (!root.TryGetProperty("server", out var server) || server.ValueKind != JsonValueKind.Object)
            return false;
        var connected = server.TryGetProperty("isConnected", out var c) && c.ValueKind == JsonValueKind.True;
        var loggedIn = server.TryGetProperty("isLoggedIn", out var l) && l.ValueKind == JsonValueKind.True;
        return connected && loggedIn;
    }

    // ── Search ───────────────────────────────────────────────────────────

    public async Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
    {
        var searchText = query.SearchText;
        if (searchText.Length == 0) return null;
        if ((await ApiKeyAsync(ct)).Length == 0) return null;

        string? searchId = null;
        try
        {
            using (var created = await SendJsonAsync(HttpMethod.Post, "/api/v0/searches",
                       new { searchText }, ct))
            {
                if (created is null) return null;
                searchId = GetString(created.RootElement, "id");
                if (string.IsNullOrEmpty(searchId)) return null;
            }

            // The network answers in seconds; a search that never completes
            // is a miss, not something to wait on.
            if (!await WaitForSearchAsync(searchId, ct)) return null;

            using var responses = await SendJsonAsync(
                HttpMethod.Get, $"/api/v0/searches/{searchId}/responses", null, ct);
            if (responses is null) return null;
            return ParseResponses(responses.RootElement, query);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("slskd search timed out");
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "slskd search '{Query}' failed", searchText);
            return null;
        }
        finally
        {
            await CleanupSearchAsync(searchId);
        }
    }

    private async Task<bool> WaitForSearchAsync(string searchId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + SearchPollTimeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            using (var status = await SendJsonAsync(HttpMethod.Get, $"/api/v0/searches/{searchId}", null, ct))
            {
                if (status is not null)
                {
                    var state = GetString(status.RootElement, "state") ?? "";
                    if (SearchIsComplete(state)) return true;
                }
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                _logger.LogDebug("slskd search did not complete within {Seconds:0} seconds", SearchPollTimeout.TotalSeconds);
                return false;
            }
            await Task.Delay(SearchPollInterval, ct);
        }
    }

    /// <summary>True when the search will not produce more responses.</summary>
    internal static bool SearchIsComplete(string state)
        => state.Contains("Completed", StringComparison.OrdinalIgnoreCase)
        || state.Contains("ResponseLimitReached", StringComparison.OrdinalIgnoreCase)
        || state.Contains("FileLimitReached", StringComparison.OrdinalIgnoreCase);

    /// <summary>Best-effort cleanup so slskd's search list does not grow.</summary>
    private async Task CleanupSearchAsync(string? searchId)
    {
        if (searchId is null) return;
        try
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var _ = await SendJsonAsync(HttpMethod.Delete, $"/api/v0/searches/{searchId}", null, cleanup.Token);
        }
        catch { /* cleanup is best effort */ }
    }

    /// <summary>
    /// Turns the responses into ranked candidates: locked files and
    /// containers the plugin does not declare are dropped, files outside
    /// the requested band are dropped, and the ranker picks the best
    /// match with free upload slots and lossless files as tie-breakers.
    /// </summary>
    internal static DownloaderSearchHit? ParseResponses(JsonElement root, TrackQuery query)
    {
        if (root.ValueKind != JsonValueKind.Array) return null;

        var candidates = new List<DownloaderSearchHit>();
        var scores = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var response in root.EnumerateArray())
        {
            if (response.ValueKind != JsonValueKind.Object) continue;
            var username = GetString(response, "username");
            if (string.IsNullOrEmpty(username)) continue;

            var freeSlot = response.TryGetProperty("hasFreeUploadSlot", out var slot)
                           && slot.ValueKind == JsonValueKind.True;

            if (!response.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var file in files.EnumerateArray())
            {
                if (file.ValueKind != JsonValueKind.Object) continue;
                if (file.TryGetProperty("isLocked", out var locked) && locked.ValueKind == JsonValueKind.True)
                    continue;

                var filename = GetString(file, "filename");
                if (string.IsNullOrEmpty(filename)) continue;

                var container = ContainerFor(filename);
                if (container is null) continue;

                var size = file.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number
                    ? s.GetInt64()
                    : 0;
                if (size <= 0) continue;

                int? kbps = file.TryGetProperty("bitRate", out var b) && b.ValueKind == JsonValueKind.Number
                    ? b.GetInt32()
                    : null;
                // Lossless files report no bitrate: check the band against
                // a typical lossless size, so a "up to 320" request never
                // settles for a FLAC the measurement would reject.
                var effectiveKbps = container == AudioFormat.Flac ? 900 : kbps;
                if (!query.Quality.IsWithinBand(effectiveKbps)) continue;

                TimeSpan? duration = file.TryGetProperty("length", out var l) && l.ValueKind == JsonValueKind.Number
                    ? TimeSpan.FromSeconds(l.GetInt32())
                    : null;

                var (artist, title) = SplitArtistTitle(filename);
                var sourceUrl = BuildSourceUrl(username, filename, size);
                candidates.Add(new DownloaderSearchHit(
                    Title: title,
                    Artist: artist,
                    SourceUrl: sourceUrl,
                    Duration: duration,
                    BitrateKbps: kbps,
                    MatchConfidence: FuzzyMatcher.Confidence(
                        query.Artist, query.Title, hitArtist: artist, hitTitle: title)));
                scores[sourceUrl] = (container == AudioFormat.Flac ? 2 : 0) + (freeSlot ? 1 : 0);
            }
        }

        if (candidates.Count == 0) return null;

        return SearchRanker.Rank(query.Artist, query.Title, query.ExpectedDuration, candidates,
            tieBreaker: hit => scores.GetValueOrDefault(hit.SourceUrl ?? "", 0));
    }

    /// <summary>The declared container for a Soulseek filename, or null.</summary>
    internal static AudioFormat? ContainerFor(string filename)
    {
        var ext = Path.GetExtension(filename.Replace('\\', '/')).ToLowerInvariant();
        return ext switch
        {
            ".flac" => AudioFormat.Flac,
            ".mp3" => AudioFormat.Mp3,
            ".m4a" or ".mp4" or ".aac" => AudioFormat.Aac,
            _ => null,
        };
    }

    /// <summary>
    /// Best-effort artist/title from a Soulseek filename ("01 - Artist -
    /// Title.ext"): peers name files loosely, so the ranker gets what it
    /// can and the duration check does the rest.
    /// </summary>
    internal static (string? Artist, string Title) SplitArtistTitle(string filename)
    {
        var name = Path.GetFileNameWithoutExtension(filename.Replace('\\', '/'));
        name = TrackNumberPrefix.Replace(name, "");
        var sep = name.IndexOf(" - ", StringComparison.Ordinal);
        if (sep > 0)
            return (name[..sep].Trim(), name[(sep + 3)..].Trim());
        return (null, name.Trim());
    }

    // ── Transfer reference ───────────────────────────────────────────────

    /// <summary>The opaque reference the manager passes back for a download.</summary>
    internal static string BuildSourceUrl(string username, string filename, long size)
        => $"slskd:{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(filename)}:{size}";

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

        var downloadsPath = await DownloadsPathAsync(ct);
        if (downloadsPath.Length == 0)
            return new DownloaderDownloadResult(false, null, "the slskd downloads folder is not configured");

        try
        {
            Directory.CreateDirectory(outputDirectory);

            using (var enqueued = await SendJsonAsync(
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
                return new DownloaderDownloadResult(false, null,
                    "slskd reported success but the file is not in the downloads folder");

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

            using (var transfers = await SendJsonAsync(
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
                var name = GetString(file, "filename");
                if (name is not null && string.Equals(name, filename, StringComparison.OrdinalIgnoreCase))
                    return GetString(file, "state");
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

    // ── HTTP plumbing ────────────────────────────────────────────────────

    private async Task<JsonDocument?> SendJsonAsync(
        HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var apiKey = await ApiKeyAsync(ct);
        if (apiKey.Length == 0) return null;

        var baseUrl = await BaseUrlAsync(ct);
        using var request = new HttpRequestMessage(method, baseUrl + path);
        request.Headers.Add("X-API-Key", apiKey);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using var response = await _http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogDebug("slskd {Method} {Path} returned {Status}", method, path, (int)response.StatusCode);
            return null;
        }

        var text = await response.Content.ReadAsStringAsync(timeout.Token);
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            _logger.LogDebug("slskd {Path} returned a non-JSON body", path);
            return null;
        }
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
