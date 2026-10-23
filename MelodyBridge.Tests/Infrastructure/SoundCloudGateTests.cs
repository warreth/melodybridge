using MelodyBridge.Infrastructure.Downloaders;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The SoundCloud plugin's declared floor: a download that reports no
/// audio bitrate is an unverified claim, so it is rejected like a bitrate
/// below the floor. Before the fix a silent null slid past the gate, and
/// the "unknown" wording in the rejection message was unreachable.
/// </summary>
[TestFixture]
public class SoundCloudGateTests
{
    [Test]
    public void NoReportedBitrate_IsRejected()
        => Assert.That(SoundCloudDownloader.RejectsByteSource(null, 128), Is.True,
            "an unknown bitrate must not pass a gate that promises a floor");

    [TestCase(96, true)]
    [TestCase(128, false)]
    [TestCase(320, false)]
    public void ReportedBitrate_IsJudgedAgainstTheFloor(int kbps, bool rejected)
        => Assert.That(SoundCloudDownloader.RejectsByteSource(kbps, 128), Is.EqualTo(rejected));

    [Test]
    public void TheHelper_AppliesTheFloor()
    {
        // 96 kbps is below the plugin's own 128 floor.
        Assert.That(SoundCloudDownloader.RejectsByteSource(96, 128), Is.True, "below the floor");
        Assert.That(SoundCloudDownloader.RejectsByteSource(96, 64), Is.False, "above a lower floor");
    }

    [Test]
    public void TheHelper_RejectsNull()
        => Assert.That(SoundCloudDownloader.RejectsByteSource(null, 128), Is.True,
            "unknown is rejected at any floor");
}
