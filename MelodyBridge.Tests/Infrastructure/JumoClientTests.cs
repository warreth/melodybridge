using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MelodyBridge.Infrastructure.Jumo;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Jumo protocol client tests against a stubbed server that reproduces
/// the service's contract: request-context headers on the way in, an
/// ECDH + HKDF + AES-GCM encrypted (and optionally gzipped) payload on
/// the way back. The stub derives the same shared secret from the
/// client's public key header, so a client that sends wrong headers,
/// nonces or additional data cannot decrypt anything; nothing here is a
/// mock of the client itself.
/// </summary>
[TestFixture]
public class JumoClientTests
{
    private sealed class JumoServerStub : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(Clone(request));
            return Task.FromResult(Respond(request));
        }

        /// <summary>The request is disposed after the call; capture what the test needs.</summary>
        private static HttpRequestMessage Clone(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return clone;
        }
    }

    private static JumoClient Create(JumoServerStub stub)
        => new(new HttpClient(stub), NullLogger<JumoClient>.Instance);

    private static string Header(HttpRequestMessage request, string name)
        => string.Join(",", request.Headers.GetValues(name));

    private static string Base64Url(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch { 2 => padded + "==", 3 => padded + "=", _ => padded };
        return Convert.FromBase64String(padded);
    }

    /// <summary>
    /// Builds an encrypted response the way the service does: ECDH against
    /// the client's public key header, HKDF-SHA256, AES-GCM with the
    /// request URL and nonce as additional data.
    /// </summary>
    private static HttpResponseMessage EncryptedJson(object payload, HttpRequestMessage request, bool gzip = false)
    {
        var clientPublic = Base64UrlDecode(Header(request, "X-Jumo-Public-Key"));
        var nonceHeader = Header(request, "X-Jumo-Request-Nonce");

        using var server = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        using var clientEcdh = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = clientPublic[1..33], Y = clientPublic[33..65] },
        });
        var secret = server.DeriveRawSecretAgreement(clientEcdh.PublicKey);
        var key = HKDF.DeriveKey(
            HashAlgorithmName.SHA256, secret, 32,
            salt: Encoding.ASCII.GetBytes("jumo-response-v1"),
            info: Encoding.ASCII.GetBytes("fetch-json"));

        var plain = JsonSerializer.SerializeToUtf8Bytes(payload);
        if (gzip)
        {
            using var output = new MemoryStream();
            using (var compressor = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
                compressor.Write(plain);
            plain = output.ToArray();
        }

        var iv = RandomNumberGenerator.GetBytes(12);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(key, 16))
        {
            gcm.Encrypt(iv, plain, cipher, tag,
                Encoding.UTF8.GetBytes(request.RequestUri!.AbsoluteUri + "\n" + nonceHeader));
        }

        var body = new byte[iv.Length + cipher.Length + tag.Length];
        iv.CopyTo(body, 0);
        cipher.CopyTo(body, iv.Length);
        tag.CopyTo(body, iv.Length + cipher.Length);

        var serverParameters = server.ExportParameters(false);
        var serverRaw = new byte[65];
        serverRaw[0] = 0x04;
        serverParameters.Q.X!.CopyTo(serverRaw, 1);
        serverParameters.Q.Y!.CopyTo(serverRaw, 33);

        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Headers.TryAddWithoutValidation("X-Jumo-Payload", "1");
        response.Headers.TryAddWithoutValidation("X-Jumo-Public-Key", Base64Url(serverRaw));
        if (gzip) response.Headers.TryAddWithoutValidation("X-Jumo-Encoding", "gzip");
        return response;
    }

    private static string SearchUrl()
        => JumoClient.Origin + JumoClient.SearchPath + "?query=daft+punk&offset=0&limit=30&region=CA&view=ui";

    [Test]
    public async Task Search_SendsTheContextHeaders_AndDecodesTheEncryptedGzipPayload()
    {
        var stub = new JumoServerStub
        {
            Respond = request => request.RequestUri!.AbsolutePath == JumoClient.SearchPath
                ? EncryptedJson(new { tracks = new { items = new[] { new { id = 9140024, title = "One More Time" } } } }, request, gzip: true)
                : new HttpResponseMessage(HttpStatusCode.NotFound),
        };
        var client = Create(stub);

        var response = await client.ApiGetAsync(SearchUrl(), CancellationToken.None);

        Assert.That(response.Status, Is.EqualTo(200));
        using var doc = JsonDocument.Parse(response.Body);
        Assert.That(doc.RootElement.GetProperty("tracks").GetProperty("items")[0].GetProperty("id").GetInt64(),
            Is.EqualTo(9140024));

        var sent = stub.Requests.Single();
        Assert.That(Header(sent, "X-Jumo-Client"), Is.EqualTo("browser"));
        Assert.That(Header(sent, "User-Agent"), Does.Contain("Chrome"),
            "the service answers 403 without a browser-like User-Agent (live-probed)");
        Assert.That(Header(sent, "X-Jumo-Session"), Has.Length.EqualTo(32));
        Assert.That(Header(sent, "X-Jumo-Request-Nonce"), Has.Length.EqualTo(24));
        Assert.That(Header(sent, "X-Jumo-Sequence"), Is.EqualTo("1"));
        Assert.That(Header(sent, "X-Jumo-Public-Key"), Has.Length.EqualTo(87));
        Assert.That(Header(sent, "X-Jumo-Accept-Encoding"), Is.EqualTo("gzip"));

        // The interaction header must carry this request's nonce and path;
        // the server-side AAD in the stub already proves the nonce matched.
        var interaction = JsonDocument.Parse(Base64UrlDecode(Header(sent, "X-Jumo-Interaction")));
        Assert.That(interaction.RootElement.GetProperty("nonce").GetString(),
            Is.EqualTo(Header(sent, "X-Jumo-Request-Nonce")));
        Assert.That(interaction.RootElement.GetProperty("method").GetString(), Is.EqualTo("GET"));
        Assert.That(interaction.RootElement.GetProperty("path").GetString(), Is.EqualTo(JumoClient.SearchPath));
        Assert.That(interaction.RootElement.GetProperty("pageAgeMs").GetInt64(), Is.GreaterThanOrEqualTo(30_000),
            "the service refuses an implausibly young page (live-probed)");
    }

    [Test]
    public async Task PlainResponse_PassesThroughUntouched()
    {
        var stub = new JumoServerStub
        {
            Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"regions":[{"code":"CA","available":true}],"default":"CA"}""",
                    Encoding.UTF8, "application/json"),
            },
        };
        var client = Create(stub);

        var response = await client.ApiGetAsync(JumoClient.Origin + JumoClient.RegionsPath, CancellationToken.None);

        Assert.That(response.Status, Is.EqualTo(200));
        Assert.That(response.Body, Does.Contain("\"default\":\"CA\""));
    }

    [Test]
    public async Task ThrottledRequest_WaitsForRetryAfter_AndSucceeds()
    {
        var attempts = 0;
        var stub = new JumoServerStub
        {
            Respond = request =>
            {
                attempts++;
                if (attempts == 1)
                {
                    var throttled = new HttpResponseMessage((HttpStatusCode)429) { Content = new ByteArrayContent([]) };
                    throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                    return throttled;
                }
                return EncryptedJson(new { ok = true }, request);
            },
        };
        var client = Create(stub);

        var response = await client.ApiGetAsync(SearchUrl(), CancellationToken.None);

        Assert.That(response.Status, Is.EqualTo(200));
        Assert.That(attempts, Is.EqualTo(2), "a 429 must be retried once the wait elapsed");
        Assert.That(stub.Requests.Select(r => Header(r, "X-Jumo-Sequence")), Is.EqualTo(new[] { "1", "2" }),
            "the sequence must advance across retries");
    }

    [Test]
    public async Task TamperedPayload_FailsClosed()
    {
        var stub = new JumoServerStub
        {
            Respond = request =>
            {
                var response = EncryptedJson(new { ok = true }, request);
                var body = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                body[^1] ^= 0xff; // flip a tag bit
                response.Content = new ByteArrayContent(body);
                return response;
            },
        };
        var client = Create(stub);

        Assert.Catch<CryptographicException>(() =>
            client.ApiGetAsync(SearchUrl(), CancellationToken.None).GetAwaiter().GetResult());
    }

    [Test]
    public async Task Sequence_IncrementsAcrossCalls()
    {
        var stub = new JumoServerStub
        {
            Respond = request => EncryptedJson(new { ok = true }, request),
        };
        var client = Create(stub);

        await client.ApiGetAsync(SearchUrl(), CancellationToken.None);
        await client.ApiGetAsync(SearchUrl(), CancellationToken.None);

        Assert.That(stub.Requests.Select(r => Header(r, "X-Jumo-Sequence")), Is.EqualTo(new[] { "1", "2" }));
    }
}
