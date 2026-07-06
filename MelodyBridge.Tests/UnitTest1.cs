using MelodyBridge.Core;

namespace MelodyBridge.Tests;

/// <summary>
/// Placeholder/quick sanity tests for the build pipeline.
/// Full tests are in the Core/, Providers/, Services/, and Infrastructure/ folders.
/// </summary>
[TestFixture]
public class BuildSanityTests
{
    [Test]
    public void CoreAssembly_IsAccessible()
    {
        var quality = new TrackQuality(320, MediaType.MP3);
        Assert.That(quality.Bitrate, Is.EqualTo(320));
    }

    [Test]
    public void EnumValues_AreAccessible()
    {
        Assert.Multiple(() =>
        {
            Assert.That((int)Platform.Spotify, Is.EqualTo(0));
            Assert.That((int)DownloadSource.ytdlp, Is.EqualTo(0));
            Assert.That((int)MediaType.MP3, Is.EqualTo(0));
            Assert.That((int)SyncStatus.Pending, Is.EqualTo(0));
        });
    }
}
