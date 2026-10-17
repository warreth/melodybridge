using MelodyBridge.Core;
using MelodyBridge.Infrastructure.MediaServers;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Plex and Subsonic directory behavior against a scripted HTTP boundary:
/// Plex tests a token-bearing GET /, Subsonic pings /rest with the salted
/// token for the given username, and both report unreachable servers as
/// false instead of throwing.
/// </summary>
[TestFixture]
public class MediaServerDirectoryTests
{
    [Test]
    public void Kinds_AreStable()
    {
        Assert.That(new PlexDirectory(new HttpClient(new ScriptedHandler())).Kind, Is.EqualTo("Plex"));
        Assert.That(new SubsonicDirectory(new HttpClient(new ScriptedHandler())).Kind, Is.EqualTo("Subsonic"));
    }

    [Test]
    public async Task Plex_TestConnection_SendsTokenAndSucceeds()
    {
        var handler = new ScriptedHandler();
        handler.On("/", """{"MediaContainer":{}}""");
        var dir = new PlexDirectory(new HttpClient(handler));

        Assert.That(await dir.TestConnectionAsync(new MediaServerConnection("http://plex:32400", "tok")), Is.True);
    }

    [Test]
    public async Task Plex_TestConnection_ServerDown_ReturnsFalse()
    {
        var handler = new ScriptedHandler(); // everything 404s
        var dir = new PlexDirectory(new HttpClient(handler));

        Assert.That(await dir.TestConnectionAsync(new MediaServerConnection("http://plex:32400", "tok")), Is.False);
    }

    [Test]
    public async Task Subsonic_TestConnection_PingsSubsonicWithSaltedToken()
    {
        var handler = new ScriptedHandler();
        handler.On("/rest/ping", """{"subsonic-response":{"status":"ok"}}""");
        var dir = new SubsonicDirectory(new HttpClient(handler));

        var connection = new MediaServerConnection("http://nav:4533", "pw", "admin");
        Assert.That(await dir.TestConnectionAsync(connection), Is.True);

        var ping = handler.Requests.Single(r => r.Url.Contains("/rest/ping"));
        Assert.That(ping.Url, Does.Contain("u=admin"), "the username the sync will use");
        Assert.That(ping.Url, Does.Contain("&t="));
        Assert.That(ping.Url, Does.Contain("&s="));
        Assert.That(ping.Url, Does.Not.Contain("pw"), "password never travels in clear");
    }

    [Test]
    public async Task Subsonic_TestConnection_SubsonicFailure_ReturnsFalse()
    {
        var handler = new ScriptedHandler();
        handler.On("/rest/ping", """{"subsonic-response":{"status":"failed"}}""");
        var dir = new SubsonicDirectory(new HttpClient(handler));

        Assert.That(await dir.TestConnectionAsync(new MediaServerConnection("http://nav:4533", "bad-pw", "admin")), Is.False,
            "auth failure is a failed test, not an exception");
    }

    [Test]
    public async Task Subsonic_TestConnection_ServerDown_ReturnsFalse()
    {
        var handler = new ScriptedHandler();
        var dir = new SubsonicDirectory(new HttpClient(handler));

        Assert.That(await dir.TestConnectionAsync(new MediaServerConnection("http://nav:4533", "pw", "admin")), Is.False);
    }

    [Test]
    public async Task UserLists_AreEmptyByDesign()
    {
        var plex = new PlexDirectory(new HttpClient(new ScriptedHandler()));
        var nav = new SubsonicDirectory(new HttpClient(new ScriptedHandler()));

        Assert.That(await plex.GetUsersAsync(new MediaServerConnection("http://p:32400", "t")), Is.Empty,
            "the Plex token-holder is the only user");
        Assert.That(await nav.GetUsersAsync(new MediaServerConnection("http://n:4533", "t")), Is.Empty,
            "Subsonic users are their own credentials");
    }

    // ── Kind-based resolution ────────────────────────────────────────────

    private static MediaServerDirectoryFactory Factory()
        => new(new IMediaServerDirectory[]
        {
            new JellyfinUserDirectory(new HttpClient(new ScriptedHandler())),
            new PlexDirectory(new HttpClient(new ScriptedHandler())),
            new SubsonicDirectory(new HttpClient(new ScriptedHandler())),
        });

    [Test]
    public void Factory_ResolvesEveryKind()
    {
        var factory = Factory();

        Assert.Multiple(() =>
        {
            Assert.That(factory.All, Has.Count.EqualTo(3));
            Assert.That(factory.For("Jellyfin")!.Kind, Is.EqualTo("Jellyfin"));
            Assert.That(factory.For("plex")!.Kind, Is.EqualTo("Plex"), "kind matching is case-insensitive");
            Assert.That(factory.For("Subsonic")!.Kind, Is.EqualTo("Subsonic"));
        });
    }

    [Test]
    public void Factory_KnowsTheSubsonicAliases()
    {
        var factory = Factory();

        Assert.Multiple(() =>
        {
            foreach (var alias in new[] { "Navidrome", "OpenSubsonic", "Gonic", "Airsonic", "Ampache", "Funkwhale" })
                Assert.That(factory.For(alias)!.Kind, Is.EqualTo("Subsonic"),
                    $"{alias} speaks the Subsonic API");
        });
    }

    [Test]
    public void Factory_UnknownKindOrNoKind_IsNull()
    {
        var factory = Factory();

        Assert.Multiple(() =>
        {
            Assert.That(factory.For("Koel"), Is.Null, "a kind nothing speaks resolves to nothing");
            Assert.That(factory.For(null), Is.Null, "several directories and no kind is ambiguous");
            Assert.That(factory.For("  "), Is.Null);
        });
    }

    [Test]
    public void Factory_NoKindWithASingleDirectory_FallsBackToIt()
    {
        var only = new PlexDirectory(new HttpClient(new ScriptedHandler()));
        var factory = new MediaServerDirectoryFactory(new IMediaServerDirectory[] { only });

        Assert.That(factory.For(null), Is.SameAs(only),
            "a trimmed-down composition keeps working without a kind");
    }
}
