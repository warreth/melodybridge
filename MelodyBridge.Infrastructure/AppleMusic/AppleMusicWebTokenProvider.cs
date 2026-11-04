using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.AppleMusic;

/// <summary>Apple's public web bundle did not hand out a usable developer token.</summary>
public sealed class AppleMusicTokenException : Exception
{
    public AppleMusicTokenException(string message) : base(message) { }
    public AppleMusicTokenException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Reads the Apple Music developer token that the public web player ships in
/// its JavaScript bundle. The token is shared by every browser, lasts about
/// six months and is refreshed here a day before it expires. Nothing is
/// persisted: a restart simply reads the bundle again.
/// </summary>
public sealed class AppleMusicWebTokenProvider
{
    private const string HomeUrl = "https://music.apple.com";
    private const string BrowserUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/131.0.0.0 Safari/537.36";

    // Apple bundles three tokens. This issuer is the one the web player uses
    // for its own API calls.
    private const string WebTokenIssuer = "AMPWebPlay";

    private readonly HttpClient _http;
    private readonly ILogger<AppleMusicWebTokenProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CachedToken? _cached;

    /// <summary>Refresh a little before the JWT expires; tests shorten it.</summary>
    internal TimeSpan RefreshBeforeExpiry { get; set; } = TimeSpan.FromDays(1);

    public AppleMusicWebTokenProvider(
        HttpClient http,
        ILogger<AppleMusicWebTokenProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>The current token, refreshed when it is missing or nearly expired.</summary>
    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        var current = _cached;
        if (IsUsable(current)) return current!.Token;

        await _gate.WaitAsync(ct);
        try
        {
            current = _cached;
            if (IsUsable(current)) return current!.Token;

            try
            {
                var fresh = await FetchAsync(ct);
                _cached = fresh;
                return fresh.Token;
            }
            catch (Exception ex) when (current is not null && current.ExpiresAtUtc > DateTime.UtcNow)
            {
                // A failed refresh must not throw away a token that still works.
                _logger.LogWarning(ex, "Refreshing the Apple Music web token failed; using the cached token");
                return current.Token;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forces the next request to read the bundle again after a 401.</summary>
    public void Invalidate() => _cached = null;

    private bool IsUsable(CachedToken? token)
        => token is not null
           && token.ExpiresAtUtc > DateTime.UtcNow.Add(RefreshBeforeExpiry);

    private async Task<CachedToken> FetchAsync(CancellationToken ct)
    {
        _logger.LogInformation("Reading the Apple Music web token from the public bundle");

        var home = await GetStringAsync(HomeUrl, ct);
        var bundlePath = ReadBundlePath(home)
            ?? throw new AppleMusicTokenException(
                "Apple Music's homepage no longer links its web bundle, so the public playlist API cannot be used.");

        var bundle = await GetStringAsync($"{HomeUrl}{bundlePath}", ct);
        var token = ReadToken(bundle)
            ?? throw new AppleMusicTokenException(
                "Apple Music's web bundle no longer contains a usable API token.");

        var expiresAt = ReadExpiry(token) ?? DateTime.UtcNow.AddHours(1);
        _logger.LogInformation("Apple Music web token valid until {ExpiresAt:u}", expiresAt);
        return new CachedToken(token, expiresAt);
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/json");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new AppleMusicTokenException(
                $"Apple Music's public web page answered {(int)response.StatusCode}.");
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>The current /assets/index~hash.js path from the homepage. Internal for tests.</summary>
    internal static string? ReadBundlePath(string html)
    {
        var match = Regex.Match(html, @"(?<path>/assets/index[~\-][^/\""']+\.js)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups["path"].Value : null;
    }

    /// <summary>
    /// Picks the web player's token out of the bundle. The bundle carries
    /// more than one JWT; the AMPWebPlay issuer is the one used here.
    /// Internal for tests.
    /// </summary>
    internal static string? ReadToken(string javascript)
    {
        var candidates = Regex.Matches(
                javascript,
                @"eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+",
                RegexOptions.CultureInvariant)
            .Select(m => m.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0) return null;

        var preferred = candidates.FirstOrDefault(token =>
            string.Equals(ReadIssuer(token), WebTokenIssuer, StringComparison.Ordinal));
        if (preferred is not null) return preferred;

        // If Apple renames the issuer, keep working with the token that has
        // the furthest expiry instead of failing on a label.
        return candidates
            .Select(token => (Token: token, Expiry: ReadExpiry(token)))
            .Where(x => x.Expiry is not null)
            .OrderByDescending(x => x.Expiry)
            .Select(x => x.Token)
            .FirstOrDefault();
    }

    /// <summary>The exp claim from a JWT, when it has one. Internal for tests.</summary>
    internal static DateTime? ReadExpiry(string token)
    {
        var payload = ReadPayload(token);
        if (payload is null) return null;
        if (!payload.Value.TryGetProperty("exp", out var exp)
            || exp.ValueKind != JsonValueKind.Number
            || !exp.TryGetInt64(out var seconds))
            return null;
        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? ReadIssuer(string token)
    {
        var payload = ReadPayload(token);
        return payload is not null
               && payload.Value.TryGetProperty("iss", out var issuer)
               && issuer.ValueKind == JsonValueKind.String
            ? issuer.GetString()
            : null;
    }

    private static JsonElement? ReadPayload(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var bytes = Convert.FromBase64String(payload);
            using var document = JsonDocument.Parse(bytes);
            return document.RootElement.Clone();
        }
        catch (FormatException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record CachedToken(string Token, DateTime ExpiresAtUtc);
}
