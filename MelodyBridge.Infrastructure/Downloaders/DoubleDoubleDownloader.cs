using System.Net;
using System.Text.Json;
using MelodyBridge.Core;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Downloaders;

/// <summary>
/// DoubleDouble downloader plugin (doubledouble.top).
///
/// A multi-service rip site: submit a direct Tidal / Qobuz / Deezer /
/// Amazon Music / SoundCloud track URL, poll until the rip is done,
/// then stream the result (lossless services hand back FLAC).
///
/// SearchAsync returns null by design: the site's /search endpoint is
/// interactive and captcha-gated, so this plugin only participates in
/// DownloadAsync(url) flows with direct source URLs resolved elsewhere.
/// The site is Turnstile captcha-gated: on a CAPTCHA error or HTTP 403
/// the download fails fast with a clear message so the waterfall moves
/// on to the next plugin.
/// </summary>
public class DoubleDoubleDownloader : IDownloader
{
    private readonly ILogger<DoubleDoubleDownloader> _logger;
    private readonly HttpClient _http;

    // DoubleDouble rips FLAC (lossless services) and MP3/AAC; bitrate depends on the source.
    public static readonly PluginCapabilities Caps =
        new([AudioFormat.Flac, AudioFormat.Mp3, AudioFormat.Aac], null, null, true, true);
    public PluginCapabilities Capabilities => Caps;

    public string Id => "doubledouble";
    public string Name => "DoubleDouble";
    public string Description => "doubledouble.top: Tidal, Qobuz, Deezer, Amazon Music, SoundCloud direct-URL rips";

    private static readonly string[] Hosts = { "https://us.doubledouble.top", "https://eu.doubledouble.top" };

    /// <summary>Poll cap, settable for tests. 45 x 2s gives ~90s+ per track.</summary>
    internal int MaxPollAttempts { get; set; } = 45;

    /// <summary>Delay between polls, overridable so tests run instantly.</summary>
    internal virtual Task Delay(int milliseconds, CancellationToken ct) => Task.Delay(milliseconds, ct);

    public DoubleDoubleDownloader(HttpClient http, ILogger<DoubleDoubleDownloader> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // Cheap per-waterfall check: any host answering HTTP 200 counts.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        foreach (var host in Hosts)
        {
            try
            {
                using var resp = await _http.GetAsync($"{host}/captcha/config?url=x", cts.Token);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DoubleDouble availability probe failed for {Host}", host);
            }
        }
        return false;
    }

    // No metadata search exists without a captcha (see class doc): this
    // plugin only downloads direct URLs handed to it by the waterfall.
    public Task<DownloaderSearchHit?> SearchAsync(string artist, string title, DownloadQuality quality, CancellationToken ct = default)
        => Task.FromResult<DownloaderSearchHit?>(null);

    public async Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl,
        string outputDirectory,
        string? melodyId,
        DownloadQuality? quality = null,
        CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourceUrl))
                return new DownloaderDownloadResult(false, null, "no source URL");

            // us host first; eu is the fallback on connection errors.
            Exception? lastError = null;
            foreach (var host in Hosts)
            {
                try
                {
                    return await DownloadFromHostAsync(host, sourceUrl, outputDirectory, melodyId, ct);
                }
                catch (Exception ex) when (ex is HttpRequestException
                    || (ex is TaskCanceledException && !ct.IsCancellationRequested))
                {
                    lastError = ex;
                    _logger.LogDebug(ex, "DoubleDouble host {Host} failed, trying next region", host);
                }
            }

            return new DownloaderDownloadResult(false, null,
                "doubledouble.top unreachable: " + (lastError?.Message ?? "connection failed"));
        }
        catch (Exception ex)
        {
            return new DownloaderDownloadResult(false, null, ex.Message);
        }
    }

    private async Task<DownloaderDownloadResult> DownloadFromHostAsync(
        string host, string sourceUrl, string outputDirectory, string? melodyId, CancellationToken ct)
    {
        // Step 1: submit: GET /dl?url=...[&external=service].
        var external = DetectService(sourceUrl);
        var submitUrl = $"{host}/dl?url={Uri.EscapeDataString(sourceUrl)}";
        if (external is not null) submitUrl += $"&external={external}";

        using var submitResp = await _http.GetAsync(submitUrl, ct);
        if (submitResp.StatusCode == HttpStatusCode.Forbidden)
            return new DownloaderDownloadResult(false, null,
                "doubledouble.top returned 403 (captcha-gated), skipping");

        submitResp.EnsureSuccessStatusCode();
        using var submitDoc = await JsonDocument.ParseAsync(
            await submitResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var submit = ParseSubmitResponse(submitDoc.RootElement);

        if (!submit.Success || string.IsNullOrEmpty(submit.Id))
        {
            // A CAPTCHA demand is a normal outcome for automated rips: fail
            // fast so the waterfall moves on to the next plugin.
            var error = submit.Error ?? "submission rejected";
            if (error.Contains("captcha", StringComparison.OrdinalIgnoreCase))
                error += ": open doubledouble.top in a browser if this happens constantly";
            return new DownloaderDownloadResult(false, null, $"doubledouble.top: {error}");
        }

        // Step 2: poll GET /dl/{id} every 2s until done / error / cap.
        string? downloadUrl = null;
        for (var attempt = 0; attempt < MaxPollAttempts && downloadUrl is null; attempt++)
        {
            if (attempt > 0) await Delay(2000, ct);

            using var pollResp = await _http.GetAsync($"{host}/dl/{submit.Id}", ct);
            pollResp.EnsureSuccessStatusCode();
            using var pollDoc = await JsonDocument.ParseAsync(
                await pollResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);

            var (status, url, message, kickback) = ParsePollResponse(pollDoc.RootElement);
            switch (status)
            {
                case "done":
                    downloadUrl = TryResolveDownloadUrl(url, host);
                    if (downloadUrl is null)
                        return new DownloaderDownloadResult(false, null,
                            "doubledouble.top: done without a download URL"
                            + (message is null ? "" : $" ({message})"));
                    break;
                case "error":
                    // kickback:false is terminal; kickback:true would be retryable
                    // but we still fail fast: the waterfall re-runs the plugin.
                    return new DownloaderDownloadResult(false, null,
                        $"doubledouble.top: {message ?? "rip failed"}"
                        + (kickback ? " (retryable)" : ""));
                default:
                    if (attempt == MaxPollAttempts - 1)
                        _logger.LogDebug("DoubleDouble poll cap reached for {Id}, last status {Status}",
                            submit.Id, status);
                    break;
            }
        }

        if (downloadUrl is null)
            return new DownloaderDownloadResult(false, null,
                $"doubledouble.top: download did not finish within {MaxPollAttempts} polls");

        // Step 3: stream the finished rip to dd_{guid}{ext}.
        Directory.CreateDirectory(outputDirectory);
        var ext = ExtensionFromUrl(downloadUrl) ?? DefaultExtension(sourceUrl);
        var target = Path.Combine(outputDirectory, $"dd_{Guid.NewGuid():N}{ext}");

        using var dlResp = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        dlResp.EnsureSuccessStatusCode();
        await using (var src = await dlResp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(target))
        {
            await src.CopyToAsync(dst, 81920, ct);
        }

        YtDlpDownloader.TagDownloadedFile(target, expectedTitle: null);
        return new DownloaderDownloadResult(true, target, null);
    }

    /// <summary>Maps a track URL host to the site's external= service name (null = omit).</summary>
    private static string? DetectService(string url)
    {
        var lower = url.ToLowerInvariant();
        if (lower.Contains("tidal.com")) return "tidal";
        if (lower.Contains("qobuz.com")) return "qobuz";
        if (lower.Contains("deezer.com")) return "deezer";
        if (lower.Contains("amazon.com")) return "amazon";
        if (lower.Contains("soundcloud.com")) return "soundcloud";
        return null;
    }

    /// <summary>"./dl/abc.flac" becomes host-absolute; absolute URLs pass through.</summary>
    private static string? TryResolveDownloadUrl(string? url, string host)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        if (url.StartsWith("./", StringComparison.Ordinal) || url.StartsWith("/", StringComparison.Ordinal))
            return $"{host}/{url.TrimStart('.', '/')}";

        return Uri.TryCreate(url, UriKind.Absolute, out var abs)
               && abs.Scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : $"{host}/{url.TrimStart('.')}";
    }

    /// <summary>Pulls success/id/error out of a GET /dl?url= response.</summary>
    private static (bool Success, string? Id, string? Error) ParseSubmitResponse(JsonElement root)
    {
        var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
        var id = GetString(root, "id");
        var error = GetString(root, "error");
        return (success, id, error);
    }

    /// <summary>Pulls the poll fields out of a GET /dl/{id} response.</summary>
    private static (string? Status, string? Url, string? Message, bool Kickback) ParsePollResponse(JsonElement root)
    {
        var status = GetString(root, "status");
        var url = GetString(root, "url");
        var message = GetString(root, "message") ?? GetString(root, "friendlyStatus");
        var kickback = root.TryGetProperty("kickback", out var k) && k.ValueKind == JsonValueKind.True;
        return (status, url, message, kickback);
    }

    /// <summary>".flac" from "https://cdn.example/x.flac?sig=1"; null when there is no usable ext.</summary>
    private static string? ExtensionFromUrl(string url)
    {
        try
        {
            var ext = Path.GetExtension(new Uri(url).AbsolutePath);
            return ext.Length is > 1 and <= 5 && ext.Trim('.').All(char.IsLetterOrDigit)
                ? ext.ToLowerInvariant()
                : null;
        }
        catch { return null; }
    }

    /// <summary>Lossless services default to .flac; everything else to .mp3.</summary>
    private static string DefaultExtension(string sourceUrl)
    {
        var lower = sourceUrl.ToLowerInvariant();
        return lower.Contains("tidal.com") || lower.Contains("qobuz.com") ? ".flac" : ".mp3";
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
