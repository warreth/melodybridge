using MelodyBridge.Infrastructure.MediaServers;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Plex and Navidrome directory behavior against a scripted HTTP boundary:
/// Plex tests a token-bearing GET /, Navidrome pings /rest with the salted
/// token, and both report unreachable servers as false instead of throwing.
/// </summary>
[TestFixture]
public class MediaServerDirectoryTests
{
    [Test]
    public void Kinds_AreStable()
    {
        Assert.That(new PlexDirectory(new HttpClient(new ScriptedHandler())).Kind, Is.EqualTo("Plex"));
        Assert.That(new NavidromeDirectory(new HttpClient(new ScriptedHandler())).Kind, Is.EqualTo("Navidrome"));
    }

    [Test]
    public async Task Plex_TestConnection_SendsTokenAndSucceeds()
    {
        var handler = new ScriptedHandler();
        handler.On("/", """{"MediaContainer":{}}""");
        var dir = new PlexDirectory(new HttpClient(handler));

        Assert.That(await dir.TestConnectionAsync("http://plex:32400", "tok"), Is.True);
    }

    [Test]
    public async Task Plex_TestConnection_ServerDown_ReturnsFalse()
    {
        var handler = new ScriptedHandler(); // everything 404s
        var dir = new PlexDirectory(new HttpClient(handler));

        Assert.That(await dir.TestConnectionAsync("http://plex:32400", "tok"), Is.False);
    }

    [Test]
    public async Task Navidrome_TestConnection_PingsSubsonicWithSaltedToken()
    {
        var handler = new ScriptedHandler();
        handler.On("/rest/ping", """{"subsonic-response":{"status":"ok"}}""");
        var dir = new NavidromeDirectory(new HttpClient(handler));

        Assert.That(await dir.TestConnectionAsync("http://nav:4533", "pw"), Is.True);

        var ping = handler.Requests.Single(r => r.Url.Contains("/rest/ping"));
        Assert.That(ping.Url, Does.Contain("&t="));
        Assert.That(ping.Url, Does.Contain("&s="));
        Assert.That(ping.Url, Does.Not.Contain("pw"), "password never travels in clear");
    }

    [Test]
    public async Task Navidrome_TestConnection_SubsonicFailure_ReturnsFalse()
    {
        var handler = new ScriptedHandler();
        handler.On("/rest/ping", """{"subsonic-response":{"status":"failed"}}""");
        var dir = new NavidromeDirectory(new HttpClient(handler));

        Assert.That(await dir.TestConnectionAsync("http://nav:4533", "bad-pw"), Is.False,
            "auth failure is a failed test, not an exception");
    }

    [Test]
    public async Task Navidrome_TestConnection_ServerDown_ReturnsFalse()
    {
        var handler = new ScriptedHandler();
        var dir = new NavidromeDirectory(new HttpClient(handler));

        Assert.That(await dir.TestConnectionAsync("http://nav:4533", "pw"), Is.False);
    }

    [Test]
    public async Task UserLists_AreEmptyByDesign()
    {
        var plex = new PlexDirectory(new HttpClient(new ScriptedHandler()));
        var nav = new NavidromeDirectory(new HttpClient(new ScriptedHandler()));

        Assert.That(await plex.GetUsersAsync("http://p:32400", "t"), Is.Empty,
            "the Plex token-holder is the only user");
        Assert.That(await nav.GetUsersAsync("http://n:4533", "t"), Is.Empty,
            "Navidrome users are their own credentials");
    }
}
