using MelodyBridge.Application.Services;

namespace MelodyBridge.Tests.ApplicationServices;

/// <summary>
/// The circuit breaker behind the waterfall: three strikes open it, the
/// cooldown half-opens it for a single probe, a success closes it again.
/// The clock is injected so the cooldown is tested, not waited for.
/// </summary>
[TestFixture]
public class ProviderHealthTests
{
    private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-16T12:00:00Z");
    private ProviderHealth _health = null!;

    [SetUp]
    public void Setup() => _health = new ProviderHealth(() => _now);

    [Test]
    public void UnknownProvider_IsHealthy()
    {
        Assert.Multiple(() =>
        {
            Assert.That(_health.ShouldSkip("x"), Is.False);
            Assert.That(_health.IsDegraded("x"), Is.False);
            Assert.That(_health.Score("x"), Is.EqualTo(1.0));
        });
    }

    [Test]
    public void ThreeConsecutiveFailures_OpenTheCircuit()
    {
        _health.RecordFailure("x");
        _health.RecordFailure("x");
        Assert.That(_health.ShouldSkip("x"), Is.False, "two strikes are not enough");

        _health.RecordFailure("x");
        Assert.That(_health.ShouldSkip("x"), Is.True, "three strikes open the circuit");
    }

    [Test]
    public void SuccessResetsTheStrikes()
    {
        _health.RecordFailure("x");
        _health.RecordFailure("x");
        _health.RecordSuccess("x");
        _health.RecordFailure("x");
        _health.RecordFailure("x");

        Assert.That(_health.ShouldSkip("x"), Is.False, "the strike counter restarted at the success");
    }

    [Test]
    public void CooldownPassed_AllowsOneProbePerInterval()
    {
        for (var i = 0; i < 3; i++) _health.RecordFailure("x");
        Assert.That(_health.ShouldSkip("x"), Is.True);

        _now += TimeSpan.FromMinutes(5);
        Assert.That(_health.ShouldSkip("x"), Is.False, "the cooldown passed: a probe is allowed");
        Assert.That(_health.ShouldSkip("x"), Is.True, "a second caller does not probe within the interval");

        // A caller that never reported back cannot wedge the circuit: after
        // the probe interval another probe is allowed.
        _now += TimeSpan.FromSeconds(31);
        Assert.That(_health.ShouldSkip("x"), Is.False);
    }

    [Test]
    public void ProbeFailure_ReopensTheCircuit()
    {
        for (var i = 0; i < 3; i++) _health.RecordFailure("x");
        _now += TimeSpan.FromMinutes(5);
        Assert.That(_health.ShouldSkip("x"), Is.False);

        _health.RecordFailure("x"); // the probe failed
        Assert.That(_health.ShouldSkip("x"), Is.True, "the probe failure re-opened the circuit");
    }

    [Test]
    public void ProbeSuccess_ClosesTheCircuit()
    {
        for (var i = 0; i < 3; i++) _health.RecordFailure("x");
        _now += TimeSpan.FromMinutes(5);
        Assert.That(_health.ShouldSkip("x"), Is.False);

        _health.RecordSuccess("x");
        Assert.Multiple(() =>
        {
            Assert.That(_health.ShouldSkip("x"), Is.False);
            Assert.That(_health.Score("x"), Is.EqualTo(0.25), "one success in a four-attempt window");
        });
    }

    [Test]
    public void MostlyFailingWindow_IsDegraded()
    {
        // Below four samples there is not enough evidence.
        _health.RecordFailure("x");
        _health.RecordFailure("x");
        Assert.That(_health.IsDegraded("x"), Is.False, "two samples are not enough to judge");

        _health.RecordFailure("x");
        _health.RecordSuccess("x");
        Assert.That(_health.IsDegraded("x"), Is.True, "three of four failed: degraded");

        _health.RecordSuccess("x");
        _health.RecordSuccess("x");
        _health.RecordSuccess("x");
        Assert.That(_health.IsDegraded("x"), Is.False, "three of seven is no longer a majority");
    }

    [Test]
    public void WindowKeepsOnlyRecentOutcomes()
    {
        for (var i = 0; i < 20; i++) _health.RecordSuccess("x");
        _health.RecordFailure("x");
        Assert.That(_health.Score("x"), Is.EqualTo(0.95), "the oldest success fell out of the window");
    }
}
