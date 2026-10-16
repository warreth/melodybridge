using MelodyBridge.Server.Services;

namespace MelodyBridge.Tests.Server;

/// <summary>
/// Log text leaves the app through the Logs page, clipboard copies and
/// export files. Credentials that travel as query parameters (Navidrome's
/// t= and s=, Jellyfin's api_key=) must be masked in all three.
/// </summary>
[TestFixture]
public class LogRedactorTests
{
    [Test]
    public void NavidromeTokenAndSalt_AreMasked_OtherParametersSurvive()
    {
        const string line =
            "GET http://127.0.0.1:59999/rest/search3?u=admin&t=3da76178cb6ad1a55db546aebaa61944&s=09d7d34d705f&v=1.16.1&query=Miles";

        var redacted = LogRedactor.Redact(line);

        Assert.That(redacted, Does.Contain("u=admin"), "usernames stay readable for debugging");
        Assert.That(redacted, Does.Contain("v=1.16.1"));
        Assert.That(redacted, Does.Contain("query=Miles"));
        Assert.That(redacted, Does.Not.Contain("3da76178"));
        Assert.That(redacted, Does.Not.Contain("09d7d34d"));
        Assert.That(redacted, Does.Contain("t=***"));
        Assert.That(redacted, Does.Contain("s=***"));
    }

    [TestCase("https://jellyfin.local/Items?api_key=SECRETVALUE&Limit=10")]
    [TestCase("https://plex.local/library?X-Plex-Token=SECRETVALUE")]
    [TestCase("https://x.local/?token=SECRETVALUE")]
    [TestCase("https://x.local/?password=SECRETVALUE")]
    [TestCase("https://x.local/?client_secret=SECRETVALUE")]
    [TestCase("https://x.local/?sp_dc=SECRETVALUE")]
    public void KnownSecretParameters_AreMasked(string line)
    {
        var redacted = LogRedactor.Redact(line);

        Assert.That(redacted, Does.Not.Contain("SECRETVALUE"));
        Assert.That(redacted, Does.Contain("***"));
    }

    [Test]
    public void TextWithoutParameters_IsUntouched()
    {
        const string line = "Download run for abc finished: 4 of 5 done, 1 failed";
        Assert.That(LogRedactor.Redact(line), Is.EqualTo(line));
    }

    [Test]
    public void NullAndEmpty_AreSafe()
    {
        Assert.That(LogRedactor.Redact(null), Is.Empty);
        Assert.That(LogRedactor.Redact(""), Is.Empty);
    }

    [Test]
    public void TheParameterName_StaysReadable()
    {
        // The value is the secret; the name is the debugging aid.
        var redacted = LogRedactor.Redact("?api_key=abc123");
        Assert.That(redacted, Is.EqualTo("?api_key=***"));
    }
}
