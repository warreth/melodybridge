using TestContext = Bunit.TestContext;
using AngleSharp.Dom;
using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.MediaServers;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The user picker component: renders the (server default) option, the
/// Test connection button drives the directory service with the connection
/// from the wizard, and the fetched users appear in the dropdown. Servers
/// without a user list (Plex, Navidrome) get the button without the
/// dropdown.
/// </summary>
[TestFixture]
[Category("UI")]
public class MediaServerUserPickerTests
{
    private TestContext _ctx = null!;
    private Mock<IMediaServerDirectory> _directory = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _directory = new Mock<IMediaServerDirectory>();
        _ctx.Services.AddSingleton<IMediaServerDirectory>(_directory.Object);
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    private IRenderedComponent<global::MelodyBridge.Server.Components.Shared.MediaServerUserPicker> Render(
        string url = "http://jf:8096", string key = "k", bool showUserList = true)
        => _ctx.Render<global::MelodyBridge.Server.Components.Shared.MediaServerUserPicker>(p => p
            .Add(c => c.ServerUrl, url)
            .Add(c => c.ApiKey, key)
            .Add(c => c.ShowUserList, showUserList));

    [Test]
    public void Renders_DefaultOption_AndTestButton()
    {
        var cut = Render();

        Assert.That(cut.Markup, Does.Contain("(server default)"));
        Assert.That(cut.Markup, Does.Contain("Test connection"));
        var options = cut.FindAll("option");
        Assert.That(options, Has.Count.EqualTo(1),
            "before a test only the (server default) option exists");
    }

    [Test]
    public void WithoutUserList_RendersOnlyTheTestButton()
    {
        var cut = Render(showUserList: false);

        Assert.That(cut.Markup, Does.Contain("Test connection"));
        Assert.That(cut.FindAll("select"), Has.Count.EqualTo(0),
            "Plex and Navidrome have no user dropdown");
    }

    [Test]
    public void TestConnection_Reachable_ShowsConnectedPill_AndListsUsers()
    {
        _directory
            .Setup(d => d.TestConnectionAsync(
                It.Is<MediaServerConnection>(c => c.BaseUrl == "http://jf:8096" && c.ApiKey == "k"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _directory
            .Setup(d => d.GetUsersAsync(
                It.Is<MediaServerConnection>(c => c.BaseUrl == "http://jf:8096" && c.ApiKey == "k"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MediaServerUserOption>
            {
                new("u1", "Alice"),
                new("u2", "Bob"),
            });

        var cut = Render();
        cut.Find("button").Click();

        Assert.That(cut.Markup, Does.Contain("connected"));
        Assert.That(cut.FindAll("option"), Has.Count.EqualTo(3),
            "default option plus the two server users");
        Assert.That(cut.Markup, Does.Contain("Alice"));
        Assert.That(cut.Markup, Does.Contain("Bob"));
    }

    [Test]
    public void TestConnection_Unreachable_ShowsErrPill_AndKeepsDefaultOnly()
    {
        _directory
            .Setup(d => d.TestConnectionAsync(It.IsAny<MediaServerConnection>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var cut = Render();
        cut.Find("button").Click();

        Assert.That(cut.Markup, Does.Contain("not connected"));
        Assert.That(cut.FindAll("option"), Has.Count.EqualTo(1));
        _directory.Verify(
            d => d.GetUsersAsync(It.IsAny<MediaServerConnection>(), It.IsAny<CancellationToken>()),
            Times.Never, "no user fetch when the server is unreachable");
    }

    [Test]
    public void TestConnection_Navidrome_PassesUsername()
    {
        _directory
            .Setup(d => d.TestConnectionAsync(
                It.Is<MediaServerConnection>(c =>
                    c.BaseUrl == "http://nav:4533" && c.ApiKey == "pw" && c.UserId == "admin"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var cut = _ctx.Render<global::MelodyBridge.Server.Components.Shared.MediaServerUserPicker>(p => p
            .Add(c => c.ServerUrl, "http://nav:4533")
            .Add(c => c.ApiKey, "pw")
            .Add(c => c.Username, "admin")
            .Add(c => c.ShowUserList, false));
        cut.Find("button").Click();

        Assert.That(cut.Markup, Does.Contain("connected"));
    }

    [Test]
    public async Task DirectoryGetUsers_SendsEmbyTokenHeader_AndParsesUsers()
    {
        // Service-level check with a scripted handler: the token header and
        // the /Users route are exactly what Jellyfin expects.
        var handler = new RecordingHandler();
        handler.Respond = url => url == "/Users"
            ? Json("""[{"Id": "u1", "Name": "Alice"}, {"Id": "u2", "Name": "Bob"}]""")
            : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        var directory = new JellyfinUserDirectory(new HttpClient(handler));

        var users = await directory.GetUsersAsync(new MediaServerConnection("http://jellyfin:8096", "tok"));

        Assert.That(handler.Requests, Has.Count.EqualTo(1));
        Assert.That(handler.Requests[0].Url, Does.EndWith("/Users"));
        Assert.That(handler.Requests[0].Token, Is.EqualTo("tok"));
        Assert.That(users.Select(u => u.Id), Is.EqualTo(new[] { "u1", "u2" }));
        Assert.That(users.Select(u => u.Name), Is.EqualTo(new[] { "Alice", "Bob" }));
    }

    [Test]
    public async Task DirectoryTestConnection_GetsSystemInfo_WithToken()
    {
        var handler = new RecordingHandler();
        handler.Respond = url => new HttpResponseMessage(
            url == "/System/Info" ? System.Net.HttpStatusCode.OK : System.Net.HttpStatusCode.NotFound);
        var directory = new JellyfinUserDirectory(new HttpClient(handler));

        var ok = await directory.TestConnectionAsync(new MediaServerConnection("http://jellyfin:8096", "tok-2"));

        Assert.That(ok, Is.True);
        Assert.That(handler.Requests[0].Url, Does.EndWith("/System/Info"));
        Assert.That(handler.Requests[0].Token, Is.EqualTo("tok-2"));
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Url, string? Token)> Requests { get; } = new();
        public Func<string, HttpResponseMessage> Respond { get; set; } =
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.RequestUri!.ToString(),
                request.Headers.TryGetValues("X-Emby-Token", out var t) ? t.FirstOrDefault() : null));
            return Task.FromResult(Respond(request.RequestUri!.PathAndQuery));
        }
    }

    private static HttpResponseMessage Json(string body)
        => new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
}
