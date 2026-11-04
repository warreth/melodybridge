using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.AppleMusic;

/// <summary>Apple Music answered with an error that the caller can explain.</summary>
public sealed class AppleMusicApiException : Exception
{
    public AppleMusicApiException(HttpStatusCode statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    public HttpStatusCode StatusCode { get; }
}

/// <summary>
/// Small client for the Apple Music API. It uses the public web developer
/// token, adds the Origin header that token requires, and adds the user's
/// music user token when an account is connected. Retries cover the
/// short-lived 429 and 5xx answers Apple returns under load.
/// </summary>
public sealed class AppleMusicApiClient
{
    public const string BaseUrl = "https://api.music.apple.com";
    private const string Origin = "https://music.apple.com";
    private const string BrowserUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/131.0.0.0 Safari/537.36";

    private readonly HttpClient _http;
    private readonly AppleMusicWebTokenProvider _tokens;
    private readonly ILogger<AppleMusicApiClient> _logger;

    /// <summary>Total attempts per request, including the first. Tests lower it.</summary>
    internal int MaxAttempts { get; set; } = 4;

    /// <summary>Base delay for 429/5xx retries. Tests set zero.</summary>
    internal TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    public AppleMusicApiClient(
        HttpClient http,
        AppleMusicWebTokenProvider tokens,
        ILogger<AppleMusicApiClient> logger)
    {
        _http = http;
        _tokens = tokens;
        _logger = logger;
    }

    /// <summary>
    /// GETs an API path or a next-page URL. The returned document belongs to
    /// the caller and must be disposed.
    /// </summary>
    public async Task<JsonDocument> GetAsync(
        string pathOrUrl,
        string? musicUserToken = null,
        CancellationToken ct = default)
    {
        var url = BuildUrl(pathOrUrl);
        var refreshed = false;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var token = await _tokens.GetTokenAsync(ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.TryAddWithoutValidation("Origin", Origin);
            request.Headers.TryAddWithoutValidation("Referer", Origin + "/");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            if (!string.IsNullOrWhiteSpace(musicUserToken))
            {
                var userToken = musicUserToken.Trim();
                request.Headers.TryAddWithoutValidation("Music-User-Token", userToken);
                // The web player sends the same value as a cookie. Sending
                // both shapes covers a pasted media-user-token and a pasted
                // music user token.
                request.Headers.TryAddWithoutValidation("Cookie", $"media-user-token={userToken}");
            }

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized && !refreshed)
            {
                refreshed = true;
                _tokens.Invalidate();
                _logger.LogWarning("Apple Music rejected the web token; reading a fresh one");
                continue;
            }

            if (ShouldRetry(response.StatusCode) && attempt + 1 < MaxAttempts)
            {
                var delay = RetryDelay(response, attempt);
                _logger.LogInformation(
                    "Apple Music answered {Status}; retrying in {Delay}",
                    (int)response.StatusCode, delay);
                await Task.Delay(delay, ct);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                throw new AppleMusicApiException(
                    response.StatusCode,
                    DescribeError(response.StatusCode, body, musicUserToken));
            }

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }

        throw new AppleMusicApiException(
            HttpStatusCode.TooManyRequests,
            "Apple Music kept refusing the request. Try again in a minute.");
    }

    private static string BuildUrl(string pathOrUrl)
    {
        if (pathOrUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return pathOrUrl;
        return BaseUrl + (pathOrUrl.StartsWith('/') ? pathOrUrl : "/" + pathOrUrl);
    }

    private static bool ShouldRetry(HttpStatusCode status)
        => status == HttpStatusCode.TooManyRequests || (int)status >= 500;

    private TimeSpan RetryDelay(HttpResponseMessage response, int attempt)
    {
        if (RetryBaseDelay <= TimeSpan.Zero) return TimeSpan.Zero;
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return delta > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delta;
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                return wait > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : wait;
        }
        return TimeSpan.FromTicks(RetryBaseDelay.Ticks * (1L << Math.Min(attempt, 5)));
    }

    private static string DescribeError(
        HttpStatusCode status, string body, string? musicUserToken)
    {
        var detail = ReadDetail(body);
        return status switch
        {
            HttpStatusCode.Unauthorized =>
                "Apple Music refused the public web token. Try again in a moment.",
            HttpStatusCode.Forbidden when !string.IsNullOrWhiteSpace(musicUserToken) =>
                "Apple Music rejected the session. Paste a fresh media-user-token in Settings, "
                + "and make sure the Apple ID has an active Apple Music subscription.",
            HttpStatusCode.Forbidden =>
                "Apple Music refused the request. The playlist may be region-blocked, "
                + "or the public web token changed.",
            HttpStatusCode.NotFound =>
                "Apple Music did not find that playlist. It must be public or shared with a link.",
            _ => detail is { Length: > 0 }
                ? $"Apple Music answered {(int)status}: {detail}"
                : $"Apple Music answered {(int)status}.",
        };
    }

    private static string? ReadDetail(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("errors", out var errors)
                || errors.ValueKind != JsonValueKind.Array
                || errors.GetArrayLength() == 0)
                return null;
            var first = errors[0];
            if (first.TryGetProperty("detail", out var detail)
                && detail.ValueKind == JsonValueKind.String)
                return detail.GetString();
            if (first.TryGetProperty("title", out var title)
                && title.ValueKind == JsonValueKind.String)
                return title.GetString();
        }
        catch (JsonException)
        {
            // Not JSON: fall back to the status text.
        }
        return null;
    }
}
