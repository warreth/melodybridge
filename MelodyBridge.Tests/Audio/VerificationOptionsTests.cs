using MelodyBridge.Infrastructure.Audio;

namespace MelodyBridge.Tests.Audio;

/// <summary>
/// The verification knobs the Settings page stores: parsing, clamping and
/// the effective length tolerance. Pure logic, no ffmpeg needed.
/// </summary>
[TestFixture]
public class VerificationOptionsTests
{
    [Test]
    public void Defaults_MatchTheDocumentedNumbers()
    {
        var options = new VerificationOptions();

        Assert.Multiple(() =>
        {
            Assert.That(options.Spectrum, Is.EqualTo(SpectrumMode.Fast));
            Assert.That(options.LengthToleranceSeconds, Is.EqualTo(30));
            Assert.That(options.LengthTolerancePercent, Is.EqualTo(10));
            Assert.That(options.SpectrumClassGap, Is.EqualTo(2));
        });
    }

    [Test]
    public void FromSettings_ParsesStoredValues()
    {
        var options = VerificationOptions.FromSettings("Thorough", "3", "45", "12.5");

        Assert.Multiple(() =>
        {
            Assert.That(options.Spectrum, Is.EqualTo(SpectrumMode.Thorough));
            Assert.That(options.SpectrumClassGap, Is.EqualTo(3));
            Assert.That(options.LengthToleranceSeconds, Is.EqualTo(45));
            Assert.That(options.LengthTolerancePercent, Is.EqualTo(12.5));
        });
    }

    [Test]
    public void FromSettings_ClampsGarbageAndOutOfRange()
    {
        var options = VerificationOptions.FromSettings("nonsense", "99", "-5", "1000");

        Assert.Multiple(() =>
        {
            Assert.That(options.Spectrum, Is.EqualTo(SpectrumMode.Fast), "unknown modes fall back to Fast");
            Assert.That(options.SpectrumClassGap, Is.EqualTo(4), "the gap tops out at 4");
            Assert.That(options.LengthToleranceSeconds, Is.EqualTo(0), "negative seconds clamp to 0");
            Assert.That(options.LengthTolerancePercent, Is.EqualTo(100), "percent tops out at 100");
        });
    }

    [Test]
    public void FromSettings_MissingValues_KeepTheDefaults()
    {
        var options = VerificationOptions.FromSettings(null, null, null, null);

        Assert.That(options, Is.EqualTo(new VerificationOptions()));
    }

    [Test]
    public void FromSettings_OffMode_IsPreserved()
    {
        Assert.That(VerificationOptions.FromSettings("off", null, null, null).Spectrum,
            Is.EqualTo(SpectrumMode.Off));
    }

    [Test]
    public void ToleranceFor_PicksTheLargerOfSecondsAndPercent()
    {
        var options = new VerificationOptions(LengthToleranceSeconds: 30, LengthTolerancePercent: 10);

        Assert.Multiple(() =>
        {
            // Short track: the absolute floor wins.
            Assert.That(options.ToleranceFor(TimeSpan.FromSeconds(60)), Is.EqualTo(TimeSpan.FromSeconds(30)));
            // Long track: the percent window grows past 30 seconds.
            Assert.That(options.ToleranceFor(TimeSpan.FromSeconds(600)), Is.EqualTo(TimeSpan.FromSeconds(60)));
        });
    }
}
