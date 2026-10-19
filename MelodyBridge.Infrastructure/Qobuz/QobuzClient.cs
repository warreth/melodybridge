using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Qobuz;

/// <summary>
/// Minimal Qobuz search client for the qobuz-dlp plugin: Qobuz rotates
/// its app id and private key, so nothing is hardcoded. The web player's
/// bundle is scraped for the current values (cached for hours), the user
/// token (from the plugin settings or qobuz-dlp's own token file) is
/// validated, and searches run against the official API.
///
/// Verified against the live API: bundle scrape, token check shapes and
/// the search response.
/// </summary>
public class QobuzClient
{
    private const string Base = "https://www.qobuz.com/api.json/0.2/";
    private const string PlayBase = "https://play.qobuz.com";

    private static readonly TimeSpan BundleTtl = TimeSpan.FromHours(6);

    private static readonly Regex BundleUrlRegex = new(
        @"<script src=""(/resources/\d+\.\d+\.\d+-[a-z]\d{3}/bundle\.js)""></script>",
        RegexOptions.Compiled);

    private static readonly Regex AppIdRegex = new(
        @"production:{api:{appId:""(\d{9})"",appSecret:""\w{32}""",
        RegexOptions.Compiled);

    private static readonly Regex PrivateKeyRegex = new(
        @"privateKey:\s*""([A-Za-z0-9]{6,30})""",
        RegexOptions.Compiled);

    private static readonly Regex SeedRegex = new(
        @"[a-z]\.initialSeed\(""([\w=]+)"",window\.utimezone\.([a-z]+)\)",
        RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly ILogger<QobuzClient> _logger;

    private BundleInfo? _bundle;
    private DateTimeOffset _bundleFetchedAt = DateTimeOffset.MinValue;

    public QobuzClient(HttpClient http, ILogger<QobuzClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>The scraped app credentials.</summary>
    public record BundleInfo(string AppId, string PrivateKey, IReadOnlyList<string> Secrets);

    /// <summary>One search result, with the fields the ranker and quality mapper need.</summary>
    public record QobuzTrack(
        long Id,
        string Title,
        string? Artist,
        TimeSpan? Duration,
        string? Isrc,
        int? MaximumBitDepth,
        bool HiresStreamable,
        int? MaximumSamplingRate = null);

    /// <summary>The account behind a token.</summary>
    public record QobuzAccount(string Token, string? Label, bool CanDownload);

    /// <summary>How a token check ended.</summary>
    public enum QobuzTokenStatus
    {
        /// <summary>The token works; the account travels along.</summary>
        Valid,

        /// <summary>Qobuz refused the token itself (401 or 403).</summary>
        Rejected,

        /// <summary>
        /// The token is real, but Qobuz blocks the account from searching
        /// or downloading (the API's USER_BLOCKED answer).
        /// </summary>
        Blocked,

        /// <summary>
        /// The check could not run: the web player was unreachable, the
        /// API answered with a server error, or the request timed out.
        /// The token may well be fine, so the UI must not call it stale.
        /// </summary>
        Unreachable,
    }

    /// <summary>The outcome of a token check.</summary>
    public record QobuzTokenCheck(
        QobuzTokenStatus Status, QobuzAccount? Account, string? Message = null)
    {
        public static readonly QobuzTokenCheck Unreachable =
            new(QobuzTokenStatus.Unreachable, null);
        public static readonly QobuzTokenCheck Rejected =
            new(QobuzTokenStatus.Rejected, null);

        public static QobuzTokenCheck Valid(QobuzAccount account) =>
            new(QobuzTokenStatus.Valid, account);
    }

    /// <summary>How one search ended, beyond "no hits".</summary>
    public enum QobuzSearchStatus
    {
        /// <summary>The API answered; the list may be empty.</summary>
        Ok,

        /// <summary>The token was refused.</summary>
        Rejected,

        /// <summary>The token works but Qobuz blocks the account.</summary>
        Blocked,

        /// <summary>The API did not answer or returned a server error.</summary>
        Unreachable,
    }

    /// <summary>The tracks plus the reason a search produced none.</summary>
    public record QobuzSearchResult(
        IReadOnlyList<QobuzTrack> Tracks,
        QobuzSearchStatus Status,
        string? Message = null)
    {
        public static readonly IReadOnlyList<QobuzTrack> Empty = Array.Empty<QobuzTrack>();

        public static QobuzSearchResult Ok(IReadOnlyList<QobuzTrack> tracks) =>
            new(tracks, QobuzSearchStatus.Ok);
    }

    // ── Bundle (app id, private key) ─────────────────────────────────────

    /// <summary>
    /// The current app credentials, scraped from the web player and
    /// cached; null when the player is unreachable or the bundle changed
    /// shape.
    /// </summary>
    public async Task<BundleInfo?> GetBundleAsync(CancellationToken ct = default)
    {
        if (_bundle is not null && DateTimeOffset.UtcNow - _bundleFetchedAt < BundleTtl)
            return _bundle;

        try
        {
            var login = await _http.GetStringAsync($"{PlayBase}/login", ct);
            var bundlePath = BundleUrlRegex.Match(login);
            if (!bundlePath.Success)
            {
                _logger.LogWarning("Qobuz: no bundle script found on the login page");
                return null;
            }

            var bundle = await _http.GetStringAsync(PlayBase + bundlePath.Groups[1].Value, ct);
            var appId = AppIdRegex.Match(bundle);
            var privateKey = PrivateKeyRegex.Match(bundle);
            if (!appId.Success || !privateKey.Success)
            {
                _logger.LogWarning("Qobuz: the bundle no longer exposes the app id or private key");
                return null;
            }

            _bundle = new BundleInfo(appId.Groups[1].Value, privateKey.Groups[1].Value,
                ParseSecrets(bundle));
            _bundleFetchedAt = DateTimeOffset.UtcNow;
            return _bundle;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Qobuz: scraping the web player bundle failed");
            return null;
        }
    }

    /// <summary>
    /// Derives the request secrets from the bundle's timezone seeds:
    /// seed + info + extras, minus the trailing 44 characters, base64.
    /// Internal for tests.
    /// </summary>
    internal static List<string> ParseSecrets(string bundle)
    {
        // timezone -> [seed, info, extras], insertion-ordered.
        var seeds = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (Match match in SeedRegex.Matches(bundle))
        {
            var timezone = match.Groups[2].Value;
            seeds[timezone] = new List<string> { match.Groups[1].Value };
            order.Add(timezone);
        }
        if (order.Count == 0) return new List<string>();

        // The bundle lists the seeds in a fixed order; the reference
        // implementation moves the second timezone to the front before
        // matching the info/extras entries.
        if (order.Count >= 2)
        {
            var second = order[1];
            order.RemoveAt(1);
            order.Insert(0, second);
        }

        var timezones = string.Join("|", order.Select(t =>
            char.ToUpperInvariant(t[0]) + t[1..]));
        var infoRegex = new Regex(
            @"name:""\w+/(" + timezones + @")"",info:""([\w=]+)"",extras:""([\w=]+)""",
            RegexOptions.Compiled);

        foreach (Match match in infoRegex.Matches(bundle))
        {
            var timezone = match.Groups[1].Value.ToLowerInvariant();
            if (seeds.TryGetValue(timezone, out var parts))
                parts.AddRange(new[] { match.Groups[2].Value, match.Groups[3].Value });
        }

        var secrets = new List<string>();
        foreach (var timezone in order)
        {
            var parts = seeds[timezone];
            if (parts.Count != 3) continue;
            var joined = string.Concat(parts);
            if (joined.Length <= 44) continue;
            try
            {
                secrets.Add(Encoding.UTF8.GetString(Convert.FromBase64String(joined[..^44])));
            }
            catch (FormatException)
            {
                // A seed that is not valid base64 is skipped, not fatal.
            }
        }
        return secrets;
    }

    // ── Token ────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates a user token with the partner login call. A rejected
    /// token is told apart from a check that could not run, so the UI
    /// never sends someone hunting for a new token over a server error.
    /// </summary>
    public async Task<QobuzTokenCheck> ValidateTokenAsync(
        string token, CancellationToken ct = default)
    {
        var bundle = await GetBundleAsync(ct);
        if (bundle is null) return QobuzTokenCheck.Unreachable;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{Base}user/login")
            {
                Content = new StringContent("extra=partner", Encoding.UTF8, "text/plain"),
            };
            request.Headers.Add("X-App-Id", bundle.AppId);
            request.Headers.Add("X-User-Auth-Token", token);

            using var response = await _http.SendAsync(request, ct);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                or System.Net.HttpStatusCode.Forbidden)
            {
                _logger.LogDebug("Qobuz rejected the token ({Status})", (int)response.StatusCode);
                return QobuzTokenCheck.Rejected;
            }
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Qobuz token check returned {Status}", (int)response.StatusCode);
                return QobuzTokenCheck.Unreachable;
            }

            using var doc = JsonDocument.Parse(await ReadBodyAsync(response, ct));
            string? label = null;
            var canDownload = true; // a valid token; the plan shows in parameters when present
            if (doc.RootElement.TryGetProperty("user", out var user) &&
                user.TryGetProperty("credential", out var credential) &&
                credential.TryGetProperty("parameters", out var parameters))
            {
                if (parameters.ValueKind == JsonValueKind.Object)
                    label = GetString(parameters, "short_label");
                else
                    canDownload = false; // free plans have no parameters block
            }

            var account = new QobuzAccount(token, label, canDownload);

            // The login call says the token is real, not that the account
            // may use the catalogue. Qobuz answers a blocked account with
            // a 403 USER_BLOCKED on a real search, so one tiny search is
            // the only honest check.
            var access = await SearchTracksAsync("a", 1, token, ct);
            return access.Status switch
            {
                QobuzSearchStatus.Ok => QobuzTokenCheck.Valid(account),
                QobuzSearchStatus.Blocked =>
                    new QobuzTokenCheck(QobuzTokenStatus.Blocked, account, access.Message),
                QobuzSearchStatus.Rejected =>
                    new QobuzTokenCheck(QobuzTokenStatus.Rejected, null, access.Message),
                _ => new QobuzTokenCheck(QobuzTokenStatus.Unreachable, account, access.Message),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Qobuz token check failed");
            return QobuzTokenCheck.Unreachable;
        }
    }

    // ── Search ───────────────────────────────────────────────────────────

    /// <summary>
    /// Tracks matching the query, best-effort parsed, plus the reason the
    /// list is empty (a refused token, a blocked account, a dead API) so
    /// the plugin can report the real problem instead of "no source".
    /// </summary>
    public async Task<QobuzSearchResult> SearchTracksAsync(
        string query, int limit, string token, CancellationToken ct = default)
    {
        var bundle = await GetBundleAsync(ct);
        if (bundle is null)
            return new(QobuzSearchResult.Empty, QobuzSearchStatus.Unreachable,
                "The Qobuz web player could not be reached");

        try
        {
            var url = $"{Base}track/search?query={Uri.EscapeDataString(query)}&limit={limit}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("X-App-Id", bundle.AppId);
            request.Headers.Add("X-User-Auth-Token", token);

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await ReadBodyAsync(response, ct);
                var failure = ClassifySearchFailure((int)response.StatusCode, body);
                _logger.LogDebug("Qobuz search returned {Status}: {Message}",
                    (int)response.StatusCode, failure.Message);
                return new(QobuzSearchResult.Empty, failure.Status, failure.Message);
            }

            using var doc = JsonDocument.Parse(await ReadBodyAsync(response, ct));
            if (!doc.RootElement.TryGetProperty("tracks", out var tracks) ||
                !tracks.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
                return QobuzSearchResult.Ok(QobuzSearchResult.Empty);

            var results = new List<QobuzTrack>();
            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("id", out var id) || !id.TryGetInt64(out var trackId))
                    continue;

                var duration = item.TryGetProperty("duration", out var d) &&
                               d.ValueKind == JsonValueKind.Number
                    ? TimeSpan.FromSeconds(d.GetDouble())
                    : (TimeSpan?)null;

                var artist = item.TryGetProperty("performer", out var performer) &&
                             performer.ValueKind == JsonValueKind.Object
                    ? GetString(performer, "name")
                    : null;

                int? maxDepth = item.TryGetProperty("maximum_bit_depth", out var depth) &&
                                depth.ValueKind == JsonValueKind.Number &&
                                depth.TryGetInt32(out var depthValue)
                    ? depthValue
                    : null;

                // Qobuz reports the sampling rate in kHz as a decimal
                // (44.1, 96, 192), not in Hz. Older payloads and fixtures
                // sometimes carry Hz; accept both so a CD track does not
                // make GetInt32 throw and take the whole search with it.
                int? maxRate = null;
                if (item.TryGetProperty("maximum_sampling_rate", out var rate) &&
                    rate.ValueKind == JsonValueKind.Number)
                {
                    var value = rate.GetDouble();
                    if (value > 0)
                        maxRate = value < 1000
                            ? (int)Math.Round(value * 1000)
                            : (int)Math.Round(value);
                }

                var hires = item.TryGetProperty("hires_streamable", out var h) &&
                            h.ValueKind == JsonValueKind.True;

                results.Add(new QobuzTrack(
                    trackId,
                    GetString(item, "title") ?? "",
                    artist,
                    duration,
                    GetString(item, "isrc"),
                    maxDepth,
                    hires,
                    maxRate));
            }
            return QobuzSearchResult.Ok(results);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Qobuz search failed");
            return new(QobuzSearchResult.Empty, QobuzSearchStatus.Unreachable,
                "The Qobuz API did not answer");
        }
    }

    /// <summary>
    /// Reads Qobuz's error body. USER_BLOCKED is the account being banned
    /// from search/download while the token itself is still valid; a plain
    /// 401/403 is the token being refused; anything else is the API being
    /// unavailable. Internal for tests.
    /// </summary>
    internal static (QobuzSearchStatus Status, string Message) ClassifySearchFailure(
        int statusCode, string? body)
    {
        if (statusCode is 401 or 403)
        {
            if (body is not null &&
                (body.Contains("USER_BLOCKED", StringComparison.OrdinalIgnoreCase)
                 || body.Contains("Account is blocked", StringComparison.OrdinalIgnoreCase)))
            {
                return (QobuzSearchStatus.Blocked,
                    "Qobuz blocked this account (USER_BLOCKED); the token is valid, but Qobuz refuses downloads for this account");
            }

            return (QobuzSearchStatus.Rejected,
                "Qobuz rejected the token; paste a fresh one");
        }

        return (QobuzSearchStatus.Unreachable,
            $"The Qobuz API answered HTTP {statusCode}");
    }

    /// <summary>
    /// Reads a response body as text, unpacking gzip when the API sends it.
    /// Qobuz compresses some catalogue answers (including the 403 that
    /// carries USER_BLOCKED) even when the request did not ask for it, so
    /// reading the raw bytes as UTF-8 used to turn every blocked account
    /// into an unexplained rejection and every compressed hit into junk.
    /// </summary>
    internal static async Task<string> ReadBodyAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var gzip = response.Content.Headers.ContentEncoding
            .Any(encoding => string.Equals(encoding, "gzip", StringComparison.OrdinalIgnoreCase));

        if (!gzip)
        {
            using var plain = new StreamReader(stream, Encoding.UTF8);
            return await plain.ReadToEndAsync(ct);
        }

        await using var decompressed = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(decompressed, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
