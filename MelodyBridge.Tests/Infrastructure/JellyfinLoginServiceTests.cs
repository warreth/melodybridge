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
                Content = new StringContent(Body, System.Text.Encoding.UTF8, "application/json"),
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
}
