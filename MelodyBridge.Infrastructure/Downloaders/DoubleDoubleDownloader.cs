using System.Net;
using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Audio;
using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
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
///
/// The site turned Turnstile captcha-gated for every submission, so the
/// plugin now reports itself unavailable (with the reason) and is retired
/// from the shipped waterfall order. It stays in the codebase disabled:
/// if the site ever drops the gate, the availability probe turns ready
/// again and a user can move it back up by hand.
/// </summary>
public class DoubleDoubleDownloader : IDownloader
{
    private readonly ILogger<DoubleDoubleDownloader> _logger;
    private readonly HttpClient _http;

    // DoubleDouble has no quality parameter either: it submits the URL and
    // streams back the service's own file (FLAC from Tidal/Qobuz, MP3
    // otherwise), so both entries are honest but uncontrollable. Its
    // SearchAsync returns null by design (captcha-gated frontend).
    public static readonly PluginCapabilities Caps = new(
    [
        new QualityDeliverable(AudioFormat.Flac, Lossless: true, Controlled: false),
        new QualityDeliverable(AudioFormat.Mp3, Lossless: false, Controlled: false),
    ]);
    public PluginCapabilities Capabilities => Caps;

    public string Id => "doubledouble";
    public string Name => "DoubleDouble";
    public string Description => "doubledouble.top: Tidal, Qobuz, Deezer, Amazon Music, SoundCloud direct-URL rips";

    /// <inheritdoc />
    public string? LastUnavailableReason { get; private set; }

    private static readonly string[] Hosts = { "https://us.doubledouble.top", "https://eu.doubledouble.top" };

    // Availability cache: the waterfall probes every enabled plugin per
    // track; a host probe answer is trusted for a short window.
    private readonly AvailabilityCache _availability = new();

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
        if (_availability.TryGet(out var cached))
            return cached;

        // The site decides whether it still accepts automated rips, and its
        // captcha config is the honest signal: active=true means every /dl
        // submission is rejected with a CAPTCHA error. Only an explicit
        // active=false counts as ready, so a changed or unreadable answer
        // never presents the plugin as working when it cannot deliver.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var available = false;
        string? reason = null;
        foreach (var host in Hosts)
        {
            try
            {
                using var resp = await _http.GetAsync($"{host}/captcha/config?url=x", cts.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    reason = "doubledouble.top did not answer";
                    continue;
                }

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync(cts.Token));
                if (doc.RootElement.TryGetProperty("active", out var active) &&
                    active.ValueKind == JsonValueKind.False)
                {
                    available = true;
                    reason = null;
                    break;
                }

                reason = "doubledouble.top requires a captcha; automated downloads are not possible";
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DoubleDouble availability probe failed for {Host}", host);
                reason ??= "doubledouble.top did not answer";
            }
        }
        LastUnavailableReason = available ? null : reason ?? "doubledouble.top did not answer";
        _availability.Store(available);
        return available;
    }

    // No metadata search exists without a captcha (see class doc): this
    // plugin only downloads direct URLs handed to it by the waterfall.
    public Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
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
        // A few consecutive transient failures (5xx, network hiccups) are
        // tolerated: aborting a 90-second rip over one blip would throw
        // away all the work the site already did.
        string? downloadUrl = null;
        var pollFailures = 0;
        for (var attempt = 0; attempt < MaxPollAttempts && downloadUrl is null; attempt++)
        {
            if (attempt > 0) await Delay(2000, ct);

            JsonElement pollRoot;
            try
            {
                using var pollResp = await _http.GetAsync($"{host}/dl/{submit.Id}", ct);
                if (!pollResp.IsSuccessStatusCode)
                {
                    if (++pollFailures >= 5)
                        return new DownloaderDownloadResult(false, null,
                            $"doubledouble.top: status polling failed with HTTP {(int)pollResp.StatusCode}");
                    continue;
                }
                using var pollDoc = await JsonDocument.ParseAsync(
                    await pollResp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                pollRoot = pollDoc.RootElement.Clone();
            }
            catch (Exception ex) when (ex is HttpRequestException
                || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                if (++pollFailures >= 5)
                    return new DownloaderDownloadResult(false, null,
                        "doubledouble.top: status polling failed: " + ex.Message);
                continue;
            }
            pollFailures = 0;

            var (status, url, message, kickback) = ParsePollResponse(pollRoot);
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

        // Step 3: stream the finished rip to dd_{guid}{ext}. The bytes are
        // sniffed before acceptance: a JSON/HTML error body served under a
        // 200 must not enter the library as audio.
        Directory.CreateDirectory(outputDirectory);
        var ext = ExtensionFromUrl(downloadUrl) ?? DefaultExtension(sourceUrl);
        var target = Path.Combine(outputDirectory, $"dd_{Guid.NewGuid():N}{ext}");

        try
        {
            using var dlResp = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            dlResp.EnsureSuccessStatusCode();
            await using (var src = await dlResp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(target))
            {
                await src.CopyToAsync(dst, 81920, ct);
            }
        }
        catch
        {
            FileCleanup.DeleteQuietly(target);
            throw;
        }

        var container = AudioSignature.EnsureExtension(target);
        if (container is null)
        {
            FileCleanup.DeleteQuietly(target);
            return new DownloaderDownloadResult(false, null,
                "doubledouble.top: the rip endpoint did not return audio");
        }

        YtDlpDownloader.TagDownloadedFile(container, expectedTitle: null);
        return new DownloaderDownloadResult(true, container, null);
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
