using MelodyBridge.Core;

namespace MelodyBridge.Tests.Core;

[TestFixture]
public class EnumTests
{
    [Test]
    public void Platform_ContainsExpectedValues()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Enum.IsDefined(typeof(Platform), "Spotify"), Is.True);
            Assert.That(Enum.IsDefined(typeof(Platform), "YouTubeMusic"), Is.True);
            Assert.That(Enum.IsDefined(typeof(Platform), "AppleMusic"), Is.True);
            Assert.That(Enum.IsDefined(typeof(Platform), "Tidal"), Is.True);
            Assert.That(Enum.IsDefined(typeof(Platform), "AmazonMusic"), Is.True);
            Assert.That(Enum.IsDefined(typeof(Platform), "Qobuz"), Is.True);
            Assert.That(Enum.IsDefined(typeof(Platform), "Soundcloud"), Is.True);
            Assert.That(Enum.IsDefined(typeof(Platform), "Deezer"), Is.True);
            Assert.That(Enum.IsDefined(typeof(Platform), "Unknown"), Is.True);
        });
    }

    [Test]
    public void MediaType_ContainsExpectedValues()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Enum.IsDefined(typeof(MediaType), "MP3"), Is.True);
            Assert.That(Enum.IsDefined(typeof(MediaType), "AAC"), Is.True);
            Assert.That(Enum.IsDefined(typeof(MediaType), "FLAC"), Is.True);
            Assert.That(Enum.IsDefined(typeof(MediaType), "WAV"), Is.True);
            Assert.That(Enum.IsDefined(typeof(MediaType), "ALAC"), Is.True);
            Assert.That(Enum.IsDefined(typeof(MediaType), "OGG"), Is.True);
            Assert.That(Enum.IsDefined(typeof(MediaType), "OPUS"), Is.True);
            Assert.That(Enum.IsDefined(typeof(MediaType), "UNKNOWN"), Is.True);
        });
    }

    [Test]
    public void SyncStatus_ContainsExpectedValues()
    {
        Assert.Multiple(() =>
        {
            Assert.That(Enum.IsDefined(typeof(SyncStatus), "Pending"), Is.True);
            Assert.That(Enum.IsDefined(typeof(SyncStatus), "InProgress"), Is.True);
            Assert.That(Enum.IsDefined(typeof(SyncStatus), "Completed"), Is.True);
            Assert.That(Enum.IsDefined(typeof(SyncStatus), "Failed"), Is.True);
        });
    }

    [Test]
    public void Platform_Values_AreDistinct()
    {
        var values = Enum.GetValues<Platform>();
        Assert.That(values.Length, Is.EqualTo(9));
    }
}
