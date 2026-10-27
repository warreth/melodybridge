using System.Net;
using System.Text;
using System.Text.Json;
using MelodyBridge.Core;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.SearchProviders;

/// <summary>
/// The lucida.to transport shared by the search provider and the
/// downloader: the challenge solver, the challenge-aware send with one
/// credential refresh, and the page-data markers every caller parses.
/// The session also owns the availability verdict, so a search that hits a
/// challenge nobody can solve marks the plugin unavailable for the card and
/// the waterfall, not just for that one query.
/// </summary>
public sealed class LucidaSession
{
    public const string BaseUrl = "https://lucida.to";

    // Page-data blob markers: the search page embeds a JSON array whose first
    // element is {"type":"data","data":...} and ends before ,"uses":{"url":1}}];
    public const string PdStart = ",{\"type\":\"data\",\"data\":";
    public const string PdEnd = ",\"uses\":{\"url\":1}}];";

    // SvelteKit's devalue output is JSON with trailing commas on some
    // releases. A comment or a trailing comma must not disable parsing.
    public static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly HttpClient _http;
    private readonly IChallengeSolver _solver;
    private readonly ILogger<LucidaSession> _logger;

    public LucidaSession(
        HttpClient http, IChallengeSolver solver, ILogger<LucidaSession> logger)
    {
        _http = http;
        _solver = solver;
        _logger = logger;
    }

    public HttpClient Http => _http;

    public string? LastUnavailableReason { get; private set; }

    public void SetUnavailable(string? reason) => LastUnavailableReason = reason;

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // Without a solver every request hits the challenge page and fails;
        // being honest about it keeps the waterfall clean. The solver in
        // turn checks its endpoint health, so a wrong explicit URL cannot
        // present this plugin as ready.
        var solverReady = await _solver.IsAvailableAsync(ct);
        SetUnavailable(solverReady
            ? null
            : "A working FlareSolverr is required (see Settings > Network)");
        return solverReady;
    }

    /// <summary>One challenge solve up front; the solver caches the clearance.</summary>
    public Task<CloudflareCredentials?> SolveAsync(CancellationToken ct = default)
        => _solver.SolveAsync(BaseUrl, ct);

    public sealed record Reply(bool Ok, int Status, string? Body);

    /// <summary>
    /// Sends a request and, on a 401/403/429 challenge, asks the solver for
    /// fresh credentials and tries exactly once more (the browser-refresh
    /// pattern from lucida's own client). Other statuses come back as a
    /// reply so the caller can report the body instead of a generic miss.
    /// </summary>
    public async Task<Reply> SendWithRefreshAsync(
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

    /// <summary>Reads a string property from a JSON body, or null.</summary>
    public static string? ReadString(string json, string property)
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

    /// <summary>The text between two markers, or null when either is missing.</summary>
    public static string? Between(string s, string start, string end)
    {
        var from = s.IndexOf(start, StringComparison.Ordinal);
        if (from < 0) return null;
        from += start.Length;
        var to = s.IndexOf(end, from, StringComparison.Ordinal);
        return to < 0 ? null : s[from..to];
    }
}
