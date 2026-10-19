using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Accounts;

/// <summary>
/// Mints a Spotify web-player access token from an sp_dc session cookie.
///
/// This is the unofficial side of MelodyBridge: Spotify has no official
/// way for a free account to read its own library, so the route is the one
/// the web player itself uses. A secret list (fetched from a URL the user
/// can change) feeds a TOTP that the token endpoint accepts next to the
/// session cookie.
///
/// The TOTP derivation and the endpoints were ported to C# from the
/// Jellyfin Spotify import plugin by Viperinius
/// (https://github.com/Viperinius/jellyfin-plugin-spotify-import, GPL-3.0)
/// and modified for MelodyBridge in October 2026. That plugin credits
/// misiektoja/spotify_monitor for the TOTP math.
/// Everything here can break the moment Spotify rotates the secret format,
/// so callers must treat failures as normal and fall back.
/// </summary>
public class SpotifyWebSession
{
    /// <summary>Default secret list. Spotify rotates the format, the URL moves.</summary>
    public const string DefaultSecretsUrl =
        "https://raw.githubusercontent.com/xyloflake/spot-secrets-go/refs/heads/main/secrets/secretBytes.json";

    private const string OpenSpotify = "https://open.spotify.com";

    // A browser user agent: the token endpoint answers a plain HTTP client
    // with a 403.
    private const string BrowserUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64; rv:140.0) Gecko/20100101 Firefox/140.0";

    private static readonly TimeSpan TokenSafetyMargin = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly ILogger<SpotifyWebSession> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _token;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    /// <summary>Last secret version the token came from, for the UI.</summary>
    public int? LastSecretVersion { get; private set; }

    public SpotifyWebSession(HttpClient http, ILogger<SpotifyWebSession> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>Drops the cached token; the next call mints a fresh one.</summary>
    public void ForgetToken()
    {
        _token = null;
        _expiresAt = DateTimeOffset.MinValue;
    }

    /// <summary>
    /// A usable access token for the session cookie, cached until shortly
    /// before it expires. Throws InvalidOperationException with a readable
    /// message when the cookie is dead or the secret list is unreachable.
    /// </summary>
    public async Task<string> GetAccessTokenAsync(
        string cookie, string? secretsUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cookie))
            throw new InvalidOperationException(
                "No Spotify session cookie is configured. Paste the sp_dc value in Settings, Accounts.");

        if (_token is { Length: > 0 } && DateTimeOffset.UtcNow + TokenSafetyMargin < _expiresAt)
            return _token;

        await _gate.WaitAsync(ct);
        try
        {
            // Another caller may have refreshed while this one waited.
            if (_token is { Length: > 0 } && DateTimeOffset.UtcNow + TokenSafetyMargin < _expiresAt)
                return _token;

            var (version, secret) = await FetchNewestSecretAsync(secretsUrl, ct);
            var serverTime = await TryReadServerTimeAsync(ct);
            var totp = GenerateTotp(secret, serverTime);

            var tokenUrl = $"{OpenSpotify}/api/token?reason=init&productType=web-player"
                           + $"&totp={totp}&totpServer={totp}&totpVer={version}"
                           + $"&sTime={serverTime}&cTime={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";

            using var request = new HttpRequestMessage(HttpMethod.Get, tokenUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            request.Headers.TryAddWithoutValidation("Cookie", $"sp_dc={cookie}");

            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Spotify refused the web session token ({(int)response.StatusCode}). "
                    + "The cookie may be expired, or the TOTP secrets changed; try again in a minute.");

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(ct));
            var root = doc.RootElement;

            var anonymous = root.TryGetProperty("isAnonymous", out var anon)
                            && anon.ValueKind == JsonValueKind.True;
            var token = root.TryGetProperty("accessToken", out var tokenProp)
                ? tokenProp.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(token) || anonymous)
                throw new InvalidOperationException(
                    "Spotify answered with an anonymous token: the sp_dc cookie is expired or "
                    + "not a session cookie from a logged-in browser. Log in at open.spotify.com and "
                    + "copy the cookie again.");

            var expiresMs = root.TryGetProperty("accessTokenExpirationTimestampMs", out var exp)
                            && exp.ValueKind == JsonValueKind.Number
                ? exp.GetInt64()
                : DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeMilliseconds();

            _token = token;
            _expiresAt = DateTimeOffset.FromUnixTimeMilliseconds(expiresMs);
            LastSecretVersion = version;

            _logger.LogInformation(
                "Spotify web session token minted from secret v{Version}, valid until {ExpiresAt:u}",
                version, _expiresAt);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<(int Version, byte[] Secret)> FetchNewestSecretAsync(
        string? secretsUrl, CancellationToken ct)
    {
        var url = string.IsNullOrWhiteSpace(secretsUrl) ? DefaultSecretsUrl : secretsUrl;
        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"the secrets list answered {(int)response.StatusCode}");
            json = await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Could not fetch the Spotify TOTP secrets from {url}: {ex.Message}. "
                + "Set a working secrets URL in the account settings.");
        }

        var newest = ParseNewestSecret(json)
            ?? throw new InvalidOperationException(
                $"The Spotify TOTP secrets at {url} are in an unknown format. "
                + "Set a working secrets URL in the account settings.");

        return newest;
    }

    /// <summary>
    /// The current time according to Spotify, read from a HEAD request's
    /// Date header: the TOTP is checked against their clock, and a skewed
    /// local clock otherwise breaks every login. Falls back to local time
    /// when the header is missing.
    /// </summary>
    private async Task<long> TryReadServerTimeAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, OpenSpotify);
            request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
            using var response = await _http.SendAsync(request, ct);
            if (response.Headers.Date is { } date)
                return date.ToUnixTimeSeconds();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Could not read Spotify's clock; using local time for the TOTP");
        }

        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>
    /// Picks the newest entry out of the secrets list. The shape is an array
    /// of {"version": int, "secret": [bytes]}, newest first most of the time,
    /// so the version decides.
    /// </summary>
    internal static (int Version, byte[] Secret)? ParseNewestSecret(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            (int Version, byte[] Secret)? newest = null;
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("version", out var versionProp)
                    || versionProp.ValueKind != JsonValueKind.Number)
                    continue;
                if (!entry.TryGetProperty("secret", out var secretProp)
                    || secretProp.ValueKind != JsonValueKind.Array)
                    continue;

                var bytes = new List<byte>();
                foreach (var number in secretProp.EnumerateArray())
                {
                    if (number.ValueKind == JsonValueKind.Number && number.TryGetByte(out var b))
                        bytes.Add(b);
                }
                if (bytes.Count == 0) continue;

                var version = versionProp.GetInt32();
                if (newest is null || version > newest.Value.Version)
                    newest = (version, bytes.ToArray());
            }

            return newest;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// RFC 6238 TOTP (SHA-1, 6 digits, 30 second step) over the key the web
    /// player derives from a secret: each byte is XORed with ((index % 33) + 9)
    /// and the decimal digits of the results are joined into the key.
    /// </summary>
    internal static string GenerateTotp(IReadOnlyList<byte> secret, long serverTimeSeconds)
    {
        var digits = new StringBuilder(secret.Count * 3);
        for (var i = 0; i < secret.Count; i++)
            digits.Append(secret[i] ^ ((i % 33) + 9));

        var key = Encoding.UTF8.GetBytes(digits.ToString());

        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, serverTimeSeconds / 30);

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(key, counter, hash);

        var offset = hash[19] & 0x0F;
        var code = (BinaryPrimitives.ReadUInt32BigEndian(hash.Slice(offset, 4)) & 0x7FFFFFFF)
                   % 1_000_000;
        return code.ToString("D6", CultureInfo.InvariantCulture);
    }
}
