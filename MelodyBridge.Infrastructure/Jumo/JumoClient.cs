using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Jumo;

/// <summary>
/// Protocol client for the Jumo-DL backend (jumo-dl.pages.dev), which
/// serves Qobuz catalog and stream descriptors without an account. The
/// service guards its API with request-context headers (session, nonce,
/// sequence, an interaction payload) and encrypts the sensitive responses
/// with an ephemeral ECDH P-256 exchange, HKDF-SHA256 and AES-GCM; search
/// and descriptor responses arrive gzip-compressed inside the encryption.
/// This client reproduces exactly what the site's own bundle sends.
///
/// The heavy bytes (the audio segments) do not come from this backend:
/// the descriptor carries a signed Qobuz CDN template that the caller
/// fetches directly.
/// </summary>
public sealed class JumoClient
{
    public const string Origin = "https://jumo-dl.pages.dev";

    public const string SearchPath = "/qggybiqvrmvapbsrfrlg";
    public const string FetchPath = "/wydmcqpplrfjrstcj";
    public const string AlbumPath = "/xtkbpqjnrrhspkqgjp";
    public const string RegionsPath = "/symdofufvpjpgkmru";

    private const int MaxAttempts = 5;
    private const int MinRetrySeconds = 1;
    private const int MaxRetrySeconds = 15;

    /// <summary>
    /// The service rejects an interaction whose page is younger than a few
    /// seconds (live-probed: 2s is refused, 30s accepted). The client
    /// reports at least this page age so a freshly launched app is not
    /// indistinguishable from a script with no page.
    /// </summary>
    private const int MinInteractionPageAgeMs = 30_000;

    /// <summary>Status plus decoded body of one API call.</summary>
    public sealed record ApiResponse(int Status, string Body);

    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly ECDiffieHellman _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    private readonly object _ecdhLock = new();
    private readonly string _session = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    private readonly long _bootedAt = Environment.TickCount64;
    private int _sequence;
    private string? _publicKey;

    public JumoClient(HttpClient http, ILogger logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>GET through the guarded API with decoding and 429/503 retries.</summary>
    public Task<ApiResponse> ApiGetAsync(string url, CancellationToken ct)
        => SendAsync(HttpMethod.Get, url, jsonBody: null, ct);

    /// <summary>POST JSON through the guarded API with decoding and retries.</summary>
    public Task<ApiResponse> ApiPostJsonAsync(string url, string jsonBody, CancellationToken ct)
        => SendAsync(HttpMethod.Post, url, jsonBody, ct);

    /// <summary>
    /// Plain download without the API guard, for the signed Qobuz CDN
    /// URLs the descriptor hands out. These are absolute URLs to another
    /// host and take no Jumo headers.
    /// </summary>
    public async Task<byte[]> DownloadBytesAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return bytes;
    }

    private async Task<ApiResponse> SendAsync(HttpMethod method, string url, string? jsonBody, CancellationToken ct)
    {
        var uri = new Uri(url, UriKind.Absolute);
        var pathname = uri.AbsolutePath;

        for (var attempt = 0; ; attempt++)
        {
            var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            var sequence = Interlocked.Increment(ref _sequence);

            using var request = new HttpRequestMessage(method, uri);
            AddContextHeaders(request, pathname, method.Method, nonce, sequence);
            if (jsonBody is not null)
                request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var raw = await response.Content.ReadAsByteArrayAsync(ct);

            // 429 and 503 arrive encrypted like every other payload; the
            // Retry-After header is the only plain signal on them.
            if ((int)response.StatusCode is 429 or 503 && raw.Length > 0)
            {
                try
                {
                    raw = DecodePayload(response, raw, uri.AbsoluteUri, nonce);
                }
                catch (Exception ex)
                {
                    // A throttled response still hit the limiter: its body is
                    // only useful for messages, and an undecodable one is not
                    // worth failing the retry over.
                    _logger.LogDebug(ex, "Jumo retry response could not be decoded");
                    raw = Array.Empty<byte>();
                }
            }
            else if (raw.Length > 0 && response.Headers.TryGetValues("X-Jumo-Payload", out var payloadFlag)
                     && payloadFlag.FirstOrDefault() == "1")
            {
                raw = DecodePayload(response, raw, uri.AbsoluteUri, nonce);
            }

            if ((int)response.StatusCode is 429 or 503 && attempt < MaxAttempts - 1)
            {
                var retryAfter = (int?)response.Headers.RetryAfter?.Delta?.TotalSeconds ?? MinRetrySeconds;
                var wait = TimeSpan.FromSeconds(Math.Clamp(retryAfter, MinRetrySeconds, MaxRetrySeconds));
                _logger.LogDebug("Jumo throttled ({Status}), waiting {Seconds:0}s before retry {Attempt}",
                    (int)response.StatusCode, wait.TotalSeconds, attempt + 1);
                await Task.Delay(wait, ct);
                continue;
            }

            var body = raw.Length == 0 ? string.Empty : Encoding.UTF8.GetString(raw);
            return new ApiResponse((int)response.StatusCode, body);
        }
    }

    /// <summary>
    /// Adds the request-context headers the site sends. The public key and
    /// the gzip hint go only to the search and descriptor endpoints, the
    /// two that answer with an encrypted, compressed payload.
    /// </summary>
    /// <summary>
    /// The service refuses requests without a browser-like User-Agent
    /// (live-probed: no UA is answered with HTTP 403 "Invalid request.").
    /// </summary>
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36";

    private void AddContextHeaders(HttpRequestMessage request, string pathname, string method, string nonce, int sequence)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        request.Headers.TryAddWithoutValidation("X-Jumo-Client", "browser");
        request.Headers.TryAddWithoutValidation("X-Jumo-Session", _session);
        request.Headers.TryAddWithoutValidation("X-Jumo-Request-Nonce", nonce);
        request.Headers.TryAddWithoutValidation("X-Jumo-Sequence", sequence.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-Jumo-Interaction", InteractionHeader(nonce, method, pathname));

        if (pathname is SearchPath or FetchPath)
        {
            request.Headers.TryAddWithoutValidation("X-Jumo-Public-Key", PublicKey());
            if (pathname == SearchPath)
                request.Headers.TryAddWithoutValidation("X-Jumo-Accept-Encoding", "gzip");
        }
    }

    /// <summary>
    /// The interaction header: the site's per-request "human context"
    /// record, base64url of a JSON object. The field set and order match
    /// what the bundle serializes.
    /// </summary>
    private string InteractionHeader(string nonce, string method, string pathname)
    {
        var pageAge = Math.Max(MinInteractionPageAgeMs, Environment.TickCount64 - _bootedAt);
        var json = string.Create(CultureInfo.InvariantCulture,
            $"{{\"v\":1,\"nonce\":\"{nonce}\",\"method\":\"{method}\",\"path\":\"{pathname}\",\"pageAgeMs\":{pageAge},\"lastInputAgeMs\":null,\"pointer\":0,\"key\":0,\"scroll\":0,\"touch\":0,\"focused\":true,\"visibility\":\"visible\",\"botSignals\":[]}}");
        return Base64Url(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Our ephemeral ECDH public key: raw P-256 point, base64url.</summary>
    private string PublicKey()
    {
        if (_publicKey is not null) return _publicKey;
        lock (_ecdhLock)
        {
            var parameters = _ecdh.ExportParameters(false);
            var raw = new byte[65];
            raw[0] = 0x04;
            parameters.Q.X!.CopyTo(raw, 1);
            parameters.Q.Y!.CopyTo(raw, 33);
            _publicKey = Base64Url(raw);
            return _publicKey;
        }
    }

    /// <summary>
    /// Decrypts one payload: ECDH shared secret, HKDF-SHA256 (salt
    /// "jumo-response-v1", info "fetch-json"), AES-GCM with the first
    /// twelve bytes as the nonce, the request URL and nonce as additional
    /// data, then gunzip when the response says so.
    /// </summary>
    private byte[] DecodePayload(HttpResponseMessage response, byte[] body, string href, string nonce)
    {
        var serverKey = response.Headers.TryGetValues("X-Jumo-Public-Key", out var values)
            ? values.FirstOrDefault()
            : null;
        if (string.IsNullOrEmpty(serverKey))
            throw new InvalidDataException("Jumo response is marked encrypted but carries no public key");

        var raw = Base64UrlDecode(serverKey);
        if (raw.Length != 65 || raw[0] != 0x04)
            throw new InvalidDataException("Jumo server public key has an unexpected shape");

        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = raw[1..33], Y = raw[33..65] },
        };
        using var serverEcdh = ECDiffieHellman.Create(parameters);

        byte[] secret;
        lock (_ecdhLock)
        {
            secret = _ecdh.DeriveRawSecretAgreement(serverEcdh.PublicKey);
        }

        var key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256, secret, 32,
            salt: Encoding.ASCII.GetBytes("jumo-response-v1"),
            info: Encoding.ASCII.GetBytes("fetch-json"));

        if (body.Length < 12 + 16)
            throw new InvalidDataException("Jumo encrypted payload is too short");

        var plain = new byte[body.Length - 12 - 16];
        using (var gcm = new AesGcm(key, 16))
        {
            gcm.Decrypt(
                body.AsSpan(0, 12),
                body.AsSpan(12, body.Length - 28),
                body.AsSpan(body.Length - 16, 16),
                plain,
                Encoding.UTF8.GetBytes(href + "\n" + nonce));
        }

        if (response.Headers.TryGetValues("X-Jumo-Encoding", out var encoding) &&
            encoding.FirstOrDefault() == "gzip")
        {
            using var input = new MemoryStream(plain);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            plain = output.ToArray();
        }

        return plain;
    }

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            _ => padded,
        };
        return Convert.FromBase64String(padded);
    }
}
