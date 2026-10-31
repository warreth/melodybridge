using MelodyBridge.Core;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MelodyBridge.Infrastructure.SearchProviders;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Infrastructure.Lucida;

/// <summary>
/// Downloader plugin for lucida.to, a multi-service rip host (Tidal, Qobuz,
/// SoundCloud, Amazon Music, Apple Music, Deezer, Spotify). Lucida sits
/// behind a Cloudflare challenge, so every request flows through the
/// configured <see cref="IChallengeSolver"/> (FlareSolverr by default);
/// without a working solver the plugin reports itself unavailable instead
/// of hammering the challenge page.
///
/// Flow per the public web client: search parses the embedded page-data
/// blob, the chosen track goes to POST /api/load with its CSRF token, the
/// response carries a handoff id + worker server, and the finished file
/// streams from that server.
/// </summary>
public class LucidaDownloader : IDownloader
{
    private readonly LucidaSession _session;
    private readonly ILogger<LucidaDownloader> _logger;

    // Lucida's API has no quality parameter (verified against the public
    // community clients: the load body only carries downscale "original"),
    // so the worker hands back whatever the source service serves. Every
    // entry is honest but uncontrollable: the router tries this plugin for
    // the containers it can return, and the post-download check decides.
    public static readonly PluginCapabilities Caps = new(
    [
        new QualityDeliverable(AudioFormat.Flac, Lossless: true, Controlled: false),
        new QualityDeliverable(AudioFormat.Mp3, Lossless: false, Controlled: false),
        new QualityDeliverable(AudioFormat.Aac, Lossless: false, Controlled: false),
    ]);
    public PluginCapabilities Capabilities => Caps;

    public string Id => "lucida";
    public string Name => "Lucida";
    public string Description =>
        "High-quality rips from Qobuz, Amazon, Deezer and Tidal via lucida.to. Needs a working Cloudflare solver (FlareSolverr).";

    public DownloaderSearchInfo SearchInfo =>
        DownloaderSearchInfo.ViaProvider(SearchData.Url,
            "Accepts Qobuz, Tidal, Deezer, SoundCloud, Amazon and Yandex URLs; no own catalogue search");

    public string IconName => "zap";
    public string? GuideUrl => "https://docs.melodybridge.app/lucida";
    public IReadOnlyList<string> Notes => new[] { "needs a FlareSolverr URL" };

    /// <inheritdoc />
    public string? LastUnavailableReason => _session.LastUnavailableReason;

    public LucidaDownloader(LucidaSession session, ILogger<LucidaDownloader> logger)
    {
        _session = session;
        _logger = logger;
    }

    /// <summary>
    /// Convenience constructor for tests and one-off hosts: creates a
    /// session from the same client and solver. The app wires one shared
    /// session so the provider and the downloader see the same state.
    /// </summary>
    public LucidaDownloader(
        HttpClient http, IChallengeSolver solver, ILogger<LucidaDownloader> logger)
        : this(new LucidaSession(http, solver, NullLogger<LucidaSession>.Instance), logger)
    {
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => _session.IsAvailableAsync(ct);

    public async Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl, string outputDirectory, string? melodyId,
        DownloadQuality? quality = null, CancellationToken ct = default)
    {
        try
        {
            // Search hits carry the SOURCE service url (qobuz.com/track/N
            // and friends), not a lucida page: lucida.to hosts the page-data
            // for any service url through the url query param, exactly like
            // the reference client's fetch_page_data. Asking the service host
            // directly gets a service page with no SvelteKit blob at all.
            var credentials = await _session.SolveAsync(ct);
            if (credentials is null)
            {
                _session.SetUnavailable("lucida.to answered a Cloudflare challenge the solver could not solve");
                return new DownloaderDownloadResult(false, null, "Cloudflare solver unavailable");
            }

            var pageDataUrl = sourceUrl.StartsWith(LucidaSession.BaseUrl, StringComparison.OrdinalIgnoreCase)
                ? sourceUrl
                : $"{LucidaSession.BaseUrl}/?url={Uri.EscapeDataString(sourceUrl)}";
            using var pageRequest = new HttpRequestMessage(HttpMethod.Get, pageDataUrl);
            pageRequest.Headers.UserAgent.ParseAdd(credentials.UserAgent);
            pageRequest.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
            if (!string.IsNullOrWhiteSpace(credentials.CookieHeader))
                pageRequest.Headers.Add("Cookie", credentials.CookieHeader);

            var page = await _session.SendWithRefreshAsync(pageRequest, credentials, ct);
            var blob = page.Body is null ? null : LucidaSession.Between(page.Body, LucidaSession.PdStart, LucidaSession.PdEnd);
            if (!page.Ok || blob is null)
                return new DownloaderDownloadResult(false, null,
                    $"could not read the track page data (HTTP {page.Status}, challenge or page format changed)");

            var track = ExtractTrackWithToken(blob, sourceUrl);
            if (track is null)
                return new DownloaderDownloadResult(false, null, "no track token found on the page");

            // The site's current single-track pages carry the token encoded
            // twice with atob() (base64). Sending the encoded value makes
            // /api/load reject every submission, which looked like "search
            // works, downloads never do". ExtractTrackWithToken decodes it.
            var country = CountryFor(sourceUrl);
            var loadBody = JsonSerializer.Serialize(new
            {
                account = new { id = country.Length > 0 ? country : "auto", type = "country" },
                compat = false,
                downscale = "original",
                handoff = true,
                metadata = true,
                @private = false,
                token = new
                {
                    expiry = "1h",
                    primary = track.Value.Csrf,
                    secondary = track.Value.CsrfFallback,
                },
                upload = new { enabled = false },
                url = track.Value.Url ?? sourceUrl,
            });

            using var loadRequest = new HttpRequestMessage(HttpMethod.Post,
                $"{LucidaSession.BaseUrl}/api/load?url=/api/fetch/stream/v2");
            loadRequest.Headers.UserAgent.ParseAdd(credentials.UserAgent);
            if (!string.IsNullOrWhiteSpace(credentials.CookieHeader))
                loadRequest.Headers.Add("Cookie", credentials.CookieHeader);
            loadRequest.Content = new StringContent(loadBody, Encoding.UTF8, "application/json");

            var load = await _session.SendWithRefreshAsync(loadRequest, credentials, ct, loadBody);
            var loadJson = load.Body;
            var handoff = loadJson is null ? null : LucidaSession.ReadString(loadJson, "handoff");
            var server = loadJson is null ? null : LucidaSession.ReadString(loadJson, "server");
            if (handoff is null || server is null)
            {
                var reason = loadJson is null ? null : LucidaSession.ReadString(loadJson, "error")
                    ?? LucidaSession.ReadString(loadJson, "message");
                return new DownloaderDownloadResult(false, null,
                    $"lucida did not accept the load request (HTTP {load.Status}"
                    + (string.IsNullOrWhiteSpace(reason) ? ")" : $": {Truncate(reason, 180)})"));
            }

            // A solved challenge clears the earlier failure note.
            _session.SetUnavailable(null);

            // Poll until the rip is ready. A single failed poll (transient
            // 5xx, a solver hiccup) must not kill a long rip: a few
            // consecutive failures are tolerated before giving up.
            var statusUrl = $"https://{server}.lucida.to/api/fetch/request/{handoff}";
            var status = "unknown";
            var lastMessage = "";
            var lastStatusChange = DateTime.UtcNow;
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(30);
            var pollFailures = 0;
            do
            {
                if (ct.IsCancellationRequested)
                    return new DownloaderDownloadResult(false, null, "cancelled");
                await Task.Delay(1000, ct);

                using var pollRequest = new HttpRequestMessage(HttpMethod.Get, statusUrl);
                pollRequest.Headers.UserAgent.ParseAdd(credentials.UserAgent);
                var poll = await _session.SendWithRefreshAsync(pollRequest, credentials, ct);
                if (!poll.Ok || poll.Body is null)
                {
                    pollFailures++;
                    if (poll.Status == 404)
                        return new DownloaderDownloadResult(false, null, "lucida lost the worker job (404)");
                    if (pollFailures >= 6)
                        return new DownloaderDownloadResult(false, null,
                            $"status polling failed (HTTP {poll.Status})");
                    continue;
                }
                pollFailures = 0;

                var nextStatus = LucidaSession.ReadString(poll.Body, "status") ?? "unknown";
                var message = LucidaSession.ReadString(poll.Body, "message") ?? "";
                if (nextStatus != status || message != lastMessage)
                {
                    status = nextStatus;
                    lastMessage = message;
                    lastStatusChange = DateTime.UtcNow;
                }

                if (status == "error")
                    return new DownloaderDownloadResult(false, null,
                        "lucida worker: " + (message.Length > 0 ? message : "unknown error"));

                // A worker that never changes status for 90 seconds is
                // stuck; waiting out the full 30 minute deadline only makes
                // the queue slower.
                if (DateTime.UtcNow - lastStatusChange > TimeSpan.FromSeconds(90))
                    return new DownloaderDownloadResult(false, null,
                        $"lucida worker made no progress for 90s (last status: {status})");
            } while (status != "completed" && DateTime.UtcNow < deadline);

            if (status != "completed")
                return new DownloaderDownloadResult(false, null, "timed out waiting for lucida");

            // Stream the finished file.
            using var fileRequest = new HttpRequestMessage(HttpMethod.Get, $"{statusUrl}/download");
            fileRequest.Headers.UserAgent.ParseAdd(credentials.UserAgent);
            if (!string.IsNullOrWhiteSpace(credentials.CookieHeader))
                fileRequest.Headers.Add("Cookie", credentials.CookieHeader);

            using var fileResponse = await _session.Http.SendAsync(fileRequest,
                HttpCompletionOption.ResponseHeadersRead, ct);
            if (!fileResponse.IsSuccessStatusCode)
                return new DownloaderDownloadResult(false, null,
                    $"download HTTP {(int)fileResponse.StatusCode}");

            var ext = ExtensionFromHeaders(fileResponse.Content.Headers)
                ?? GuessExtension(fileResponse.Content.Headers.ContentType?.MediaType);
            var fileName = $"{Sanitize(melodyId ?? "lucida-download")}{ext}";
            var path = Path.Combine(outputDirectory, fileName);
            Directory.CreateDirectory(outputDirectory);

            try
            {
                await using (var source = await fileResponse.Content.ReadAsStreamAsync(ct))
                await using (var target = File.Create(path))
                {
                    await source.CopyToAsync(target, ct);
                }
            }
            catch
            {
                // A half-written file is a corrupt file: never leave it
                // for the library scanner to pick up.
                try { File.Delete(path); } catch { /* best effort */ }
                throw;
            }

            // Sniff the real bytes: a rip server that answers with an HTML
            // error page under a 200 must not land as an audio file. The
            // extension follows the actual container, not the content type.
            var container = Audio.AudioSignature.EnsureExtension(path);
            if (container is null || new FileInfo(container).Length == 0)
            {
                try { File.Delete(container ?? path); } catch { /* best effort */ }
                return new DownloaderDownloadResult(false, null,
                    "lucida returned a file that is not audio");
            }

            return new DownloaderDownloadResult(true, container, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Lucida download failed: {Message}", ex.Message);
            return new DownloaderDownloadResult(false, null, ex.Message);
        }
    }

    private readonly record struct TrackToken(string Csrf, string? CsrfFallback, string? Url);

    private static TrackToken? ExtractTrackWithToken(string blob, string sourceUrl)
    {
        try
        {
            using var doc = JsonDocument.Parse(blob, LucidaSession.JsonOptions);
            var root = doc.RootElement;
            var info = root.GetPropertySafe("info");

            // Single track pages: token at the top level, encoded twice with
            // atob() by the web client. Album pages carry csrf per track;
            // this plugin only downloads single tracks, but both shapes are
            // accepted so a future album flow does not silently break.
            var csrf = DecodeTrackToken(root.GetPropertySafe("token")?.GetString())
                ?? info?.GetPropertySafe("csrf")?.GetString()
                ?? root.GetPropertySafe("csrf")?.GetString();
            if (string.IsNullOrWhiteSpace(csrf)) return null;

            var fallback = root.GetPropertySafe("csrfFallback")?.GetString()
                ?? info?.GetPropertySafe("csrfFallback")?.GetString();
            var url = info?.GetPropertySafe("url")?.GetString()
                ?? root.GetPropertySafe("url")?.GetString()
                ?? sourceUrl;
            return new TrackToken(csrf!, fallback, url);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Lucida encodes the single-track token with two base64 (atob) calls.
    /// A token that is already plain text, or that a future release stops
    /// encoding, comes back unchanged instead of becoming an empty value.
    /// </summary>
    private static string? DecodeTrackToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        var current = token.Trim();
        for (var pass = 0; pass < 2; pass++)
        {
            try
            {
                var normalized = current.Replace('-', '+').Replace('_', '/');
                var padding = (4 - normalized.Length % 4) % 4;
                if (padding > 0) normalized = normalized.PadRight(normalized.Length + padding, '=');
                var text = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
                if (text.Length == 0 || text.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t'))
                    return current;
                current = text;
            }
            catch
            {
                return current;
            }
        }
        return current;
    }

    private static string CountryFor(string sourceUrl)
    {
        var host = Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri)
            ? uri.Host.ToLowerInvariant()
            : sourceUrl.ToLowerInvariant();
        if (host.Contains("qobuz")) return "NL";
        if (host.Contains("deezer")) return "FR";
        if (host.Contains("tidal")) return "US";
        // Amazon and SoundCloud pick an account themselves.
        return "";
    }

    private static string? ExtensionFromHeaders(HttpContentHeaders headers)
    {
        if (headers.ContentDisposition is { } disposition)
        {
            var name = disposition.FileNameStar ?? disposition.FileName;
            if (!string.IsNullOrWhiteSpace(name))
            {
                var ext = Path.GetExtension(name.Trim('"'));
                if (ext.Length is > 1 and <= 6) return ext.ToLowerInvariant();
            }
        }
        return null;
    }

    private static string GuessExtension(string? mediaType) => mediaType switch
    {
        "audio/flac" or "audio/x-flac" => ".flac",
        "audio/mpeg" or "audio/mp3" => ".mp3",
        "audio/mp4" or "audio/aac" or "audio/x-m4a" => ".m4a",
        "audio/ogg" => ".ogg",
        "audio/opus" => ".opus",
        "audio/wav" or "audio/x-wav" => ".wav",
        _ => ".mp3",
    };

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
}

internal static class JsonElementExtensions
{
    public static JsonElement? GetPropertySafe(this JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind != JsonValueKind.Null
            ? value
            : null;

    public static IEnumerable<JsonElement> EnumerateArraySafe(this JsonElement element)
        => element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : Enumerable.Empty<JsonElement>();

    public static long? GetInt64Safe(this JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value))
            return value;
        if (element.ValueKind == JsonValueKind.String
            && long.TryParse(element.GetString(), out var parsed))
            return parsed;
        return null;
    }
}
