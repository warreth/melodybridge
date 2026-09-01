using MelodyBridge.Core;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using MelodyBridge.Infrastructure.Tagging;
using Microsoft.Extensions.Logging;

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
    private const string BaseUrl = "https://lucida.to";

    // Page-data blob markers: the search page embeds a JSON array whose first
    // element is {"type":"data","data":...} and ends before ,"uses":{"url":1}}];
    private const string PdStart = ",{\"type\":\"data\",\"data\":";
    private const string PdEnd = ",\"uses\":{\"url\":1}}];";

    private readonly HttpClient _http;
    private readonly IChallengeSolver _solver;
    private readonly ILogger<LucidaDownloader> _logger;

    public string Id => "lucida";
    public string Name => "Lucida";
    public string Description =>
        "High-quality rips from Tidal, Qobuz and more via lucida.to. Needs a Cloudflare solver (FlareSolverr) in the settings.";

    public LucidaDownloader(HttpClient http, IChallengeSolver solver, ILogger<LucidaDownloader> logger)
    {
        _http = http;
        _solver = solver;
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // Without a solver every request hits the challenge page and fails;
        // being honest about it keeps the waterfall clean.
        return await _solver.IsAvailableAsync(ct);
    }

    public async Task<DownloaderSearchHit?> SearchAsync(
        string artist, string title, DownloadQuality quality, CancellationToken ct = default)
    {
        var credentials = await _solver.SolveAsync(BaseUrl, ct);
        if (credentials is null) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{BaseUrl}/search?query={Uri.EscapeDataString($"{artist} {title}")}&service=tidal");
        request.Headers.UserAgent.ParseAdd(credentials.UserAgent);
        if (!string.IsNullOrWhiteSpace(credentials.CookieHeader))
            request.Headers.Add("Cookie", credentials.CookieHeader);

        var html = await SendWithRefreshAsync(request, credentials, ct);
        if (html is null) return null;

        var blob = Between(html, PdStart, PdEnd);
        if (blob is null) return null;

        var track = FindBestTrack(blob, artist, title);
        return track;
    }

    public async Task<DownloaderDownloadResult> DownloadAsync(
        string sourceUrl, string outputDirectory, string? melodyId,
        DownloadQuality? quality = null, CancellationToken ct = default)
    {
        try
        {
            // The source URL is a lucida track page; page-data carries the CSRF token.
            var credentials = await _solver.SolveAsync(sourceUrl, ct);
            if (credentials is null)
                return new DownloaderDownloadResult(false, null, "Cloudflare solver unavailable");

            using var pageRequest = new HttpRequestMessage(HttpMethod.Get, sourceUrl);
            pageRequest.Headers.UserAgent.ParseAdd(credentials.UserAgent);
            if (!string.IsNullOrWhiteSpace(credentials.CookieHeader))
                pageRequest.Headers.Add("Cookie", credentials.CookieHeader);

            var pageHtml = await SendWithRefreshAsync(pageRequest, credentials, ct);
            var blob = pageHtml is null ? null : Between(pageHtml, PdStart, PdEnd);
            if (blob is null)
                return new DownloaderDownloadResult(false, null, "could not read the track page data");

            var track = ExtractTrackWithToken(blob, sourceUrl);
            if (track is null)
                return new DownloaderDownloadResult(false, null, "no track token found on the page");

            // Ask the worker to prepare the rip.
            var loadBody = JsonSerializer.Serialize(new
            {
                account = new { id = "auto", type = "country" },
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
                url = sourceUrl,
            });

            using var loadRequest = new HttpRequestMessage(HttpMethod.Post,
                $"{BaseUrl}/api/load?url=/api/fetch/stream/v2");
            loadRequest.Headers.UserAgent.ParseAdd(credentials.UserAgent);
            if (!string.IsNullOrWhiteSpace(credentials.CookieHeader))
                loadRequest.Headers.Add("Cookie", credentials.CookieHeader);
            loadRequest.Content = new StringContent(loadBody, Encoding.UTF8, "application/json");

            var loadJson = await SendWithRefreshAsync(loadRequest, credentials, ct);
            if (loadJson is null) return new DownloaderDownloadResult(false, null, "load request failed");

            var handoff = ReadString(loadJson, "handoff");
            var server = ReadString(loadJson, "server");
            if (handoff is null || server is null)
                return new DownloaderDownloadResult(false, null, "lucida did not accept the load request");

            // Poll until the rip is ready.
            var statusUrl = $"https://{server}.lucida.to/api/fetch/request/{handoff}";
            string status;
            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(30);
            do
            {
                if (ct.IsCancellationRequested)
                    return new DownloaderDownloadResult(false, null, "cancelled");
                await Task.Delay(2000, ct);

                using var pollRequest = new HttpRequestMessage(HttpMethod.Get, statusUrl);
                pollRequest.Headers.UserAgent.ParseAdd(credentials.UserAgent);
                var poll = await SendWithRefreshAsync(pollRequest, credentials, ct);
                if (poll is null)
                    return new DownloaderDownloadResult(false, null, "status polling failed");

                status = ReadString(poll, "status") ?? "unknown";
                if (status == "error")
                    return new DownloaderDownloadResult(false, null,
                        "lucida worker: " + (ReadString(poll, "message") ?? "unknown error"));
            } while (status != "completed" && DateTime.UtcNow < deadline);

            if (status != "completed")
                return new DownloaderDownloadResult(false, null, "timed out waiting for lucida");

            // Stream the finished file.
            using var fileRequest = new HttpRequestMessage(HttpMethod.Get, $"{statusUrl}/download");
            fileRequest.Headers.UserAgent.ParseAdd(credentials.UserAgent);
            if (!string.IsNullOrWhiteSpace(credentials.CookieHeader))
                fileRequest.Headers.Add("Cookie", credentials.CookieHeader);

            using var fileResponse = await _http.SendAsync(fileRequest,
                HttpCompletionOption.ResponseHeadersRead, ct);
            if (!fileResponse.IsSuccessStatusCode)
                return new DownloaderDownloadResult(false, null,
                    $"download HTTP {(int)fileResponse.StatusCode}");

            var ext = GuessExtension(fileResponse.Content.Headers.ContentType?.MediaType);
            var fileName = $"{Sanitize(melodyId ?? "lucida-download")}{ext}";
            var path = Path.Combine(outputDirectory, fileName);
            await using (var source = await fileResponse.Content.ReadAsStreamAsync(ct))
            await using (var target = File.Create(path))
            {
                await source.CopyToAsync(target, ct);
            }

            if (melodyId is not null)
                TaglibHelper.WriteMelodyId(path, melodyId);
            return new DownloaderDownloadResult(true, path, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Lucida download failed: {Message}", ex.Message);
            return new DownloaderDownloadResult(false, null, ex.Message);
        }
    }

    /// <summary>
    /// Sends a request and, on a 403 challenge, asks the solver for fresh
    /// credentials and tries exactly once more (the browser-refresh
    /// pattern from lucida's own client).
    /// </summary>
    private async Task<string?> SendWithRefreshAsync(
        HttpRequestMessage request, CloudflareCredentials credentials, CancellationToken ct)
    {
        var response = await _http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            return body;
        }

        response.Dispose();
        var fresh = await _solver.SolveAsync(request.RequestUri!.ToString(), ct);
        if (fresh is null) return null;

        using var retry = await _http.SendAsync(Clone(request, fresh), ct);
        return retry.IsSuccessStatusCode
            ? await retry.Content.ReadAsStringAsync(ct)
            : null;
    }

    private static HttpRequestMessage Clone(
        HttpRequestMessage original, CloudflareCredentials credentials)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Content = original.Content,
        };
        clone.Headers.UserAgent.ParseAdd(credentials.UserAgent);
        if (!string.IsNullOrWhiteSpace(credentials.CookieHeader))
            clone.Headers.Add("Cookie", credentials.CookieHeader);
        return clone;
    }

    /// <summary>Picks the best matching track from the search page-data blob.</summary>
    private static DownloaderSearchHit? FindBestTrack(string blob, string artist, string title)
    {
        try
        {
            using var doc = JsonDocument.Parse(blob);
            var node = FindTracksNode(doc.RootElement);
            var tracks = node?.GetPropertySafe("tracks");
            if (tracks is not { ValueKind: JsonValueKind.Array }
                || tracks.Value.GetArrayLength() == 0)
                return null;

            DownloaderSearchHit? best = null;
            var bestScore = -1.0;
            foreach (var entry in tracks.Value.EnumerateArray())
            {
                var url = entry.GetPropertySafe("url")?.GetString();
                if (string.IsNullOrWhiteSpace(url)) continue;

                var entryTitle = entry.GetPropertySafe("title")?.GetString() ?? "";
                var entryArtist = string.Join(", ",
                    (entry.GetPropertySafe("artists") ?? default)
                    .EnumerateArraySafe()
                    .Select(a => a.GetPropertySafe("name")?.GetString() ?? ""));
                var durationMs = entry.GetPropertySafe("duration")?.GetInt64Safe();

                var score = FuzzyScore(artist, title, entryArtist, entryTitle);
                if (score <= bestScore) continue;

                bestScore = score;
                best = new DownloaderSearchHit(
                    entryTitle,
                    entryArtist,
                    url,
                    durationMs is > 0 ? TimeSpan.FromMilliseconds(durationMs.Value) : null,
                    MatchConfidence: MelodyBridge.Infrastructure.Services.FuzzyMatcher.Confidence(
                        artist, title, entryArtist, entryTitle));
            }
            return best;
        }
        catch
        {
            return null;
        }
    }

    private static (string Csrf, string? CsrfFallback)? ExtractTrackWithToken(
        string blob, string sourceUrl)
    {
        try
        {
            using var doc = JsonDocument.Parse(blob);
            var root = doc.RootElement;

            // Track pages embed info + token at the top level.
            var csrf = root.GetPropertySafe("token")?.GetString();
            if (string.IsNullOrWhiteSpace(csrf))
            {
                var info = root.GetPropertySafe("info");
                csrf = info?.GetPropertySafe("csrf")?.GetString();
            }
            if (string.IsNullOrWhiteSpace(csrf)) return null;
            var fallback = root.GetPropertySafe("csrfFallback")?.GetString()
                ?? root.GetPropertySafe("info")?.GetPropertySafe("csrfFallback")?.GetString();
            return (csrf!, fallback);
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement? FindTracksNode(JsonElement element, int depth = 0)
    {
        if (depth > 8) return null;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var tracks = element.GetPropertySafe("tracks");
                if (tracks is { ValueKind: JsonValueKind.Array })
                    return element;
                foreach (var value in element.EnumerateObject())
                {
                    var found = FindTracksNode(value.Value, depth + 1);
                    if (found is not null) return found;
                }
                return null;
            case JsonValueKind.Array:
                foreach (var value in element.EnumerateArray())
                {
                    var found = FindTracksNode(value, depth + 1);
                    if (found is not null) return found;
                }
                return null;
            default:
                return null;
        }
    }

    private static double FuzzyScore(
        string artist, string title, string hitArtist, string hitTitle)
    {
        // Averaged similarity; only used to rank hits against each other.
        return (MelodyBridge.Infrastructure.Services.FuzzyMatcher.Similarity(title, hitTitle)
                + MelodyBridge.Infrastructure.Services.FuzzyMatcher.Similarity(artist, hitArtist)) / 2;
    }

    private static string? ReadString(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? Between(string s, string start, string end)
    {
        var from = s.IndexOf(start, StringComparison.Ordinal);
        if (from < 0) return null;
        from += start.Length;
        var to = s.IndexOf(end, from, StringComparison.Ordinal);
        return to < 0 ? null : s[from..to];
    }

    private static string GuessExtension(string? mediaType) => mediaType switch
    {
        "audio/flac" => ".flac",
        "audio/mpeg" => ".mp3",
        "audio/mp4" or "audio/aac" => ".m4a",
        "audio/ogg" => ".opus",
        _ => ".mp3",
    };

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
        => element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var value) ? value : null;
}
