using MelodyBridge.Core;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MelodyBridge.Infrastructure.Services;
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

    // SvelteKit's devalue output is JSON with trailing commas on some
    // releases. A comment or a trailing comma must not disable search.
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    // Search services lucida.to supports, lossless-oriented and most-covered
    // first. Qobuz currently resolves through Lucida's Netherlands account,
    // Amazon selects an account itself, Deezer uses France and Tidal uses
    // the US storefront. The current community client uses exactly these
    // defaults; the old US Qobuz storefront returned no usable results.
    private static readonly (string Service, string Country)[] SearchServices =
    [
        ("qobuz", "NL"),
        ("amazon", ""),
        ("deezer", "FR"),
        ("tidal", "US"),
    ];

    private readonly HttpClient _http;
    private readonly IChallengeSolver _solver;
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
        DownloaderSearchInfo.ViaProvider(SearchData.Text | SearchData.Isrc,
            "Lucida service search, ISRC first; hits carry no code, so a title match is still needed (legacy source-side adapter)");


    /// <inheritdoc />
    public string? LastUnavailableReason { get; private set; }

    public LucidaDownloader(HttpClient http, IChallengeSolver solver, ILogger<LucidaDownloader> logger)
    {
        _http = http;
        _solver = solver;
        _logger = logger;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // Without a solver every request hits the challenge page and fails;
        // being honest about it keeps the waterfall clean. The solver in
        // turn checks its endpoint health, so a wrong explicit URL cannot
        // present this plugin as ready.
        var solverReady = await _solver.IsAvailableAsync(ct);
        LastUnavailableReason = solverReady
            ? null
            : "A working FlareSolverr is required (see Settings > Network)";
        return solverReady;
    }

    public async Task<DownloaderSearchHit?> SearchAsync(TrackQuery query, CancellationToken ct = default)
    {
        // One challenge solve up front; the solver caches the clearance for
        // every service request below, and SendWithRefreshAsync re-solves on
        // a 403. A throwing solver must not escape into the waterfall (the
        // manager has no per-plugin catch on the search path), so solve
        // inside try.
        CloudflareCredentials? credentials;
        try
        {
            credentials = await _solver.SolveAsync(BaseUrl, ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Lucida solver failed for search: {Message}", ex.Message);
            LastUnavailableReason = "The FlareSolverr solver did not answer";
            return null;
        }
        if (credentials is null)
        {
            LastUnavailableReason = "lucida.to answered a Cloudflare challenge the solver could not solve";
            return null;
        }

        // ISRC first when the playlist (or the resolver) knows one: Qobuz
        // and Amazon index ISRCs, so the code finds the exact recording
        // even when the title search is noisy. Services that treat it as
        // text return noise the ranker rejects, and the text chain below
        // takes over. Scoring always runs against the real artist/title.
        if (query.HasIsrc)
        {
            var byIsrc = await SearchServicesAsync(query, query.Isrc!, credentials, ct);
            if (byIsrc?.MatchConfidence == MatchConfidence.High) return byIsrc;
        }

        // Storefront indexes are literal: parenthetical mix suffixes and
        // featuring tags tank the hit rate, so every service gets the
        // cleaned terms (the raw pair stays for match scoring).
        var searchText = SearchTerms.Query(query.Artist, query.Title);
        if (searchText.Length == 0) return null;
        return await SearchServicesAsync(query, searchText, credentials, ct);
    }

    /// <summary>
    /// Runs the service chain with one query text: the first
    /// High-confidence hit wins, otherwise the best Low-confidence hit is
    /// kept so the caller can try the text query.
    /// </summary>
    private async Task<DownloaderSearchHit?> SearchServicesAsync(
        TrackQuery query, string searchText, CloudflareCredentials credentials, CancellationToken ct)
    {
        DownloaderSearchHit? best = null;
        foreach (var (service, country) in SearchServices)
        {
            var hit = await SearchServiceAsync(service, country, query, searchText, credentials, ct);
            if (hit is null) continue;

            // A High-confidence match cannot be beaten by a later service;
            // skip the remaining requests entirely.
            if (hit.MatchConfidence == MatchConfidence.High) return hit;
            best = hit;
        }
        return best;
    }

    /// <summary>
    /// Runs one search against a single lucida service. Any failure (network,
    /// missing blob, bad parse) returns null so the chain can try the next
    /// service instead of failing the whole search.
    /// </summary>
    private async Task<DownloaderSearchHit?> SearchServiceAsync(
        string service, string country, TrackQuery query, string searchText,
        CloudflareCredentials credentials, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{BaseUrl}/search?query={Uri.EscapeDataString(searchText)}"
                + $"&service={service}"
                + (country.Length > 0 ? $"&country={country}" : string.Empty));
            request.Headers.UserAgent.ParseAdd(credentials.UserAgent);
            request.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
            if (!string.IsNullOrWhiteSpace(credentials.CookieHeader))
                request.Headers.Add("Cookie", credentials.CookieHeader);

            var reply = await SendWithRefreshAsync(request, credentials, ct);
            if (!reply.Ok || reply.Body is null)
            {
                _logger.LogDebug("Lucida {Service} search answered HTTP {Status}", service, reply.Status);
                return null;
            }

            var blob = Between(reply.Body, PdStart, PdEnd);
            if (blob is null) return null;

            return FindBestTrack(blob, query.Artist, query.Title, query.ExpectedDuration);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Lucida {Service} search failed: {Message}", service, ex.Message);
            return null;
        }
    }

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
            var credentials = await _solver.SolveAsync(BaseUrl, ct);
            if (credentials is null)
            {
                LastUnavailableReason = "lucida.to answered a Cloudflare challenge the solver could not solve";
                return new DownloaderDownloadResult(false, null, "Cloudflare solver unavailable");
            }

            var pageDataUrl = sourceUrl.StartsWith(BaseUrl, StringComparison.OrdinalIgnoreCase)
                ? sourceUrl
                : $"{BaseUrl}/?url={Uri.EscapeDataString(sourceUrl)}";
            using var pageRequest = new HttpRequestMessage(HttpMethod.Get, pageDataUrl);
            pageRequest.Headers.UserAgent.ParseAdd(credentials.UserAgent);
            pageRequest.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
            if (!string.IsNullOrWhiteSpace(credentials.CookieHeader))
                pageRequest.Headers.Add("Cookie", credentials.CookieHeader);

            var page = await SendWithRefreshAsync(pageRequest, credentials, ct);
            var blob = page.Body is null ? null : Between(page.Body, PdStart, PdEnd);
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
                $"{BaseUrl}/api/load?url=/api/fetch/stream/v2");
            loadRequest.Headers.UserAgent.ParseAdd(credentials.UserAgent);
            if (!string.IsNullOrWhiteSpace(credentials.CookieHeader))
                loadRequest.Headers.Add("Cookie", credentials.CookieHeader);
            loadRequest.Content = new StringContent(loadBody, Encoding.UTF8, "application/json");

            var load = await SendWithRefreshAsync(loadRequest, credentials, ct, loadBody);
            var loadJson = load.Body;
            var handoff = loadJson is null ? null : ReadString(loadJson, "handoff");
            var server = loadJson is null ? null : ReadString(loadJson, "server");
            if (handoff is null || server is null)
            {
                var reason = loadJson is null ? null : ReadString(loadJson, "error")
                    ?? ReadString(loadJson, "message");
                return new DownloaderDownloadResult(false, null,
                    $"lucida did not accept the load request (HTTP {load.Status}"
                    + (string.IsNullOrWhiteSpace(reason) ? ")" : $": {Truncate(reason, 180)})"));
            }

            // A solved challenge clears the earlier failure note.
            LastUnavailableReason = null;

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
                var poll = await SendWithRefreshAsync(pollRequest, credentials, ct);
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

                var nextStatus = ReadString(poll.Body, "status") ?? "unknown";
                var message = ReadString(poll.Body, "message") ?? "";
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

            using var fileResponse = await _http.SendAsync(fileRequest,
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

    private sealed record Reply(bool Ok, int Status, string? Body);

    /// <summary>
    /// Sends a request and, on a 401/403/429 challenge, asks the solver for
    /// fresh credentials and tries exactly once more (the browser-refresh
    /// pattern from lucida's own client). Other statuses come back as a
    /// reply so the caller can report the body instead of a generic miss.
    /// </summary>
    private async Task<Reply> SendWithRefreshAsync(
        HttpRequestMessage request, CloudflareCredentials credentials, CancellationToken ct,
        string? bodySnapshot = null)
    {
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode)
            return new Reply(true, (int)response.StatusCode, body);

        if (!IsChallengeStatus(response.StatusCode))
            return new Reply(false, (int)response.StatusCode, body);

        var fresh = await _solver.SolveFreshAsync(BaseUrl, ct);
        if (fresh is null) return new Reply(false, (int)response.StatusCode, body);

        using var retry = await _http.SendAsync(Clone(request, fresh, bodySnapshot), ct);
        var retryBody = await retry.Content.ReadAsStringAsync(ct);
        return new Reply(retry.IsSuccessStatusCode, (int)retry.StatusCode, retryBody);
    }

    private static bool IsChallengeStatus(HttpStatusCode status)
        => status is HttpStatusCode.Unauthorized
            or HttpStatusCode.Forbidden
            or HttpStatusCode.TooManyRequests;

    private static HttpRequestMessage Clone(
        HttpRequestMessage original, CloudflareCredentials credentials,
        string? bodySnapshot)
    {
        // The first send disposes the original content (default
        // CompletionOption buffers then releases it), so aliasing it in
        // the retry throws ObjectDisposedException. The body snapshot is
        // captured at request construction and rebuilt here instead.
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        if (bodySnapshot is not null)
            clone.Content = new StringContent(bodySnapshot, Encoding.UTF8, "application/json");
        clone.Headers.UserAgent.ParseAdd(credentials.UserAgent);
        clone.Headers.AcceptLanguage.ParseAdd("en-US,en;q=0.8");
        if (!string.IsNullOrWhiteSpace(credentials.CookieHeader))
            clone.Headers.Add("Cookie", credentials.CookieHeader);
        return clone;
    }

    /// <summary>
    /// Picks the best matching track from the search page-data blob:
    /// every entry is a candidate, ranked by title/artist similarity plus
    /// duration proximity, with the runner-ups attached as alternatives.
    /// </summary>
    private static DownloaderSearchHit? FindBestTrack(
        string blob, string artist, string title, TimeSpan? expectedDuration)
    {
        try
        {
            using var doc = JsonDocument.Parse(blob, JsonOptions);
            var node = FindTracksNode(doc.RootElement);
            var tracks = node?.GetPropertySafe("tracks");
            if (tracks is not { ValueKind: JsonValueKind.Array }
                || tracks.Value.GetArrayLength() == 0)
                return null;

            var candidates = new List<DownloaderSearchHit>();
            foreach (var entry in tracks.Value.EnumerateArray())
            {
                var url = entry.GetPropertySafe("url")?.GetString();
                if (string.IsNullOrWhiteSpace(url)) continue;

                var entryTitle = entry.GetPropertySafe("title")?.GetString() ?? "";
                var entryArtist = ReadArtists(entry);
                var durationMs = entry.GetPropertySafe("duration")?.GetInt64Safe();

                candidates.Add(new DownloaderSearchHit(
                    entryTitle,
                    entryArtist,
                    url,
                    durationMs is > 0 ? TimeSpan.FromMilliseconds(durationMs.Value) : null,
                    MatchConfidence: Services.FuzzyMatcher.Confidence(
                        artist, title, entryArtist, entryTitle)));
            }

            return SearchRanker.Rank(artist, title, expectedDuration, candidates);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadArtists(JsonElement entry)
    {
        var artists = entry.GetPropertySafe("artists");
        if (artists is { ValueKind: JsonValueKind.Array })
        {
            return string.Join(", ",
                artists.Value.EnumerateArray()
                    .Select(a => a.GetPropertySafe("name")?.GetString() ?? "")
                    .Where(name => name.Length > 0));
        }

        // Some SvelteKit releases flatten a single artist to text.
        return artists?.GetString() ?? entry.GetPropertySafe("artist")?.GetString() ?? "";
    }

    private readonly record struct TrackToken(string Csrf, string? CsrfFallback, string? Url);

    private static TrackToken? ExtractTrackWithToken(string blob, string sourceUrl)
    {
        try
        {
            using var doc = JsonDocument.Parse(blob, JsonOptions);
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

    private static string? ReadString(string json, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, JsonOptions);
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
