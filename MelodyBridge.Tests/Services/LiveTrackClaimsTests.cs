using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The claims that are live in this process. A download holds its track
/// until it ends, and a track can be claimed twice: the last claim to
/// end frees it.
/// </summary>
[TestFixture]
public class LiveTrackClaimsTests
{
    [Test]
    public void AClaimIsLiveUntilItsScopeEnds()
    {
        var claims = new LiveTrackClaims();

        Assert.That(claims.IsLive(7), Is.False, "nothing holds the track yet");

        var scope = claims.Claim(7);
        Assert.That(claims.IsLive(7), Is.True, "the download holds the track now");

        scope.Dispose();
        Assert.That(claims.IsLive(7), Is.False, "the download ended, the claim is gone");
    }

    [Test]
    public void TwoClaimsOnOneTrackFreeItOnlyWhenBothEnd()
    {
        var claims = new LiveTrackClaims();

        var first = claims.Claim(3);
        var second = claims.Claim(3);

        first.Dispose();
        Assert.That(claims.IsLive(3), Is.True,
            "the second download still holds the track");

        second.Dispose();
        Assert.That(claims.IsLive(3), Is.False);
    }

    [Test]
    public void ADisposedScopeIsReleasedOnlyOnce()
    {
        var claims = new LiveTrackClaims();

        var scope = claims.Claim(1);
        scope.Dispose();
        scope.Dispose();

        Assert.That(claims.IsLive(1), Is.False);
        Assert.That(claims.Count(1), Is.Zero);
    }

    [Test]
    public void AClaimThatArrivesWhileTheLastOneEndsIsNotLost()
    {
        // The race that matters: the last claim ends at the same moment
        // a new one starts. The track must stay claimed.
        var claims = new LiveTrackClaims();

        var first = claims.Claim(1);
        var second = claims.Claim(1);
        second.Dispose();

        // The new claim arrives while the first one ends.
        var third = claims.Claim(1);
        first.Dispose();

        Assert.That(claims.IsLive(1), Is.True,
            "the new claim is still held after the old one ended");
        third.Dispose();
        Assert.That(claims.IsLive(1), Is.False);
    }

    [Test]
    public void ActiveCount_CountsTheClaimedTracks()
    {
        var claims = new LiveTrackClaims();
        Assert.That(claims.ActiveCount, Is.Zero);

        using var first = claims.Claim(1);
        using var second = claims.Claim(2);
        using var again = claims.Claim(1);
        Assert.That(claims.ActiveCount, Is.EqualTo(2),
            "two scopes on one track are still one claimed track");

        first.Dispose();
        again.Dispose();
        Assert.That(claims.ActiveCount, Is.EqualTo(1));
    }
}
