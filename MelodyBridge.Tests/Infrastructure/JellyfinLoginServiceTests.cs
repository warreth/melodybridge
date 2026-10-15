using System.Net;
using MelodyBridge.Infrastructure.MediaServers;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The Jellyfin sign-in against a scripted handler: the request shape (the
/// modern Authorization header, the username and password body) and the
/// outcomes the wizard shows. No real network.
/// </summary>
[TestFixture]
public class JellyfinLoginServiceTests
{
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public List<(string Method, string Url, string Body, string? Authorization)> Requests { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } =
            """{"AccessToken":"tok-1","User":{"Id":"user-1","Name":"mbadmin"}}""";
        /// <summary>Per-URL bodies for flows with more than one call.</summary>
        public Func<string, string>? BodyFor { get; set; }
        public Exception? Throw { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (Throw is not null) throw Throw;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            request.Headers.TryGetValues("Authorization", out var authorization);
            Requests.Add((request.Method.Method, request.RequestUri!.ToString(), body,
                authorization?.FirstOrDefault()));
            return new HttpResponseMessage(Status)
            {
                Content = new StringContent(
                    BodyFor?.Invoke(request.RequestUri!.ToString()) ?? Body,
                    System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }

    private static JellyfinLoginService NewService(ScriptedHandler handler)
        => new(new HttpClient(handler), NullLogger<JellyfinLoginService>.Instance);

    [Test]
    public async Task Login_Success_ReturnsTokenAndUser()
    {
        var handler = new ScriptedHandler();
        var result = await NewService(handler).LoginAsync("jellyfin:8096", "mbadmin", "secret");

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True, result.Error);
            Assert.That(result.Token, Is.EqualTo("tok-1"));
            Assert.That(result.UserId, Is.EqualTo("user-1"));
            Assert.That(result.UserName, Is.EqualTo("mbadmin"));
        });

        var request = handler.Requests.Single();
        Assert.Multiple(() =>
        {
            Assert.That(request.Method, Is.EqualTo("POST"));
            Assert.That(request.Url, Is.EqualTo("http://jellyfin:8096/Users/AuthenticateByName"),
                "a bare host gets the http scheme");
            Assert.That(request.Body, Does.Contain("\"username\":\"mbadmin\""),
                "JsonContent serializes camelCase; Jellyfin binds it case-insensitively");
            Assert.That(request.Body, Does.Contain("\"pw\":\"secret\""));
            Assert.That(request.Authorization, Does.StartWith("MediaBrowser "),
                "the modern header: current Jellyfin ignores the legacy one");
        });
    }

    [Test]
    public async Task Login_WrongPassword_IsHonest()
    {
        var handler = new ScriptedHandler
        {
            Status = HttpStatusCode.Unauthorized,
            Body = "Error processing request.",
        };
        var result = await NewService(handler).LoginAsync("http://jellyfin:8096", "mbadmin", "wrong");

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Error, Does.Contain("rejected"));
        });
    }

    [Test]
    public async Task Login_Unreachable_ReturnsTheError()
    {
        var handler = new ScriptedHandler { Throw = new HttpRequestException("connection refused") };
        var result = await NewService(handler).LoginAsync("http://jellyfin:8096", "mbadmin", "secret");

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Error, Does.Contain("connection refused"));
        });
    }

    [Test]
    public async Task Login_MissingInputs_DoesNotCallTheServer()
    {
        var handler = new ScriptedHandler();
        var service = NewService(handler);

        var noUrl = await service.LoginAsync("", "mbadmin", "secret");
        var noUser = await service.LoginAsync("http://jellyfin:8096", "", "secret");

        Assert.Multiple(() =>
        {
            Assert.That(noUrl.Succeeded, Is.False);
            Assert.That(noUser.Succeeded, Is.False);
            Assert.That(handler.Requests, Is.Empty, "nothing is sent without a URL and a username");
        });
    }

    // ── Quick Connect ───────────────────────────────────────────────

    [Test]
    public async Task QuickConnect_Disabled_IsHonest()
    {
        var handler = new ScriptedHandler { Body = "false" };
        var result = await NewService(handler).StartQuickConnectAsync("http://jellyfin:8096");

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Error, Does.Contain("turned off"));
            Assert.That(handler.Requests, Has.Count.EqualTo(1), "the initiate call is skipped");
            Assert.That(handler.Requests[0].Url, Does.EndWith("/QuickConnect/Enabled"));
        });
    }

    [Test]
    public async Task QuickConnect_Initiate_ReturnsCodeAndSecret()
    {
        var handler = new ScriptedHandler
        {
            BodyFor = url => url.Contains("/QuickConnect/Enabled")
                ? "true"
                : """{"Secret":"secret-1","Code":"ABCDEF"}""",
        };
        var result = await NewService(handler).StartQuickConnectAsync("jellyfin:8096");

        Assert.Multiple(() =>
        {
            Assert.That(result.Succeeded, Is.True, result.Error);
            Assert.That(result.Secret, Is.EqualTo("secret-1"));
            Assert.That(result.Code, Is.EqualTo("ABCDEF"));
            Assert.That(handler.Requests.Select(r => r.Url), Is.EqualTo(new[]
            {
                "http://jellyfin:8096/QuickConnect/Enabled",
                "http://jellyfin:8096/QuickConnect/Initiate",
            }));
            Assert.That(handler.Requests.All(r => r.Authorization!.StartsWith("MediaBrowser ")), Is.True,
                "every Quick Connect call carries the client header");
        });
    }

    [Test]
    public async Task QuickConnect_Pending_ReturnsNull()
    {
        var handler = new ScriptedHandler { Body = """{"Authenticated":false,"Secret":"secret-1"}""" };
        var result = await NewService(handler).FinishQuickConnectAsync("http://jellyfin:8096", "secret-1");

        Assert.That(result, Is.Null, "still waiting for the user");
        Assert.That(handler.Requests.Single().Url, Does.Contain("/QuickConnect/Connect?secret=secret-1"));
    }

    [Test]
    public async Task QuickConnect_Approved_ExchangesTheSecretForAToken()
    {
        var handler = new ScriptedHandler
        {
            BodyFor = url => url.Contains("/QuickConnect/Connect")
                ? """{"Authenticated":true,"Secret":"secret-1"}"""
                : """{"AccessToken":"tok-qc","User":{"Id":"user-1","Name":"mbadmin"}}""",
        };
        var result = await NewService(handler).FinishQuickConnectAsync("http://jellyfin:8096", "secret-1");

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Succeeded, Is.True, result.Error);
            Assert.That(result.Token, Is.EqualTo("tok-qc"));
            Assert.That(result.UserName, Is.EqualTo("mbadmin"));
        });
        var exchange = handler.Requests.Last();
        Assert.Multiple(() =>
        {
            Assert.That(exchange.Url, Does.EndWith("/Users/AuthenticateWithQuickConnect"));
            Assert.That(exchange.Body, Does.Contain("secret-1"));
        });
    }

    [Test]
    public async Task QuickConnect_ExpiredCode_IsHonest()
    {
        var handler = new ScriptedHandler { Status = HttpStatusCode.NotFound, Body = "Unknown secret" };
        var result = await NewService(handler).FinishQuickConnectAsync("http://jellyfin:8096", "gone");

        Assert.Multiple(() =>
        {
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Succeeded, Is.False);
            Assert.That(result.Error, Does.Contain("expired"));
        });
    }
}
