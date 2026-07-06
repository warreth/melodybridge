using MelodyBridge.Infrastructure.Helpers;

namespace MelodyBridge.Tests.Infrastructure;

[TestFixture]
public class TrackFileHelperExtendedTests
{
    [Test]
    public void HttpFileDownloadStrategy_CanHandle_HttpUrl_ReturnsTrue()
    {
        var strategy = new HttpFileDownloadStrategy();
        Assert.Multiple(() =>
        {
            Assert.That(strategy.CanHandle("http://example.com/file.mp3"), Is.True);
            Assert.That(strategy.CanHandle("https://example.com/file.flac"), Is.True);
        });
    }

    [Test]
    public void HttpFileDownloadStrategy_CanHandle_NonHttpUrl_ReturnsFalse()
    {
        var strategy = new HttpFileDownloadStrategy();
        Assert.Multiple(() =>
        {
            Assert.That(strategy.CanHandle("ftp://example.com/file.mp3"), Is.False);
            Assert.That(strategy.CanHandle("file:///local/file.mp3"), Is.False);
            Assert.That(strategy.CanHandle(""), Is.False);
        });
    }

    [Test]
    public void GetTempTrackFilePath_ReturnsValidPath()
    {
        var path = TrackFileHelper.GetTempTrackFilePath(12345, "24", "flac");
        Assert.Multiple(() =>
        {
            Assert.That(path, Is.Not.Null.And.Not.Empty);
            Assert.That(path, Does.EndWith(".flac"));
            Assert.That(path, Does.Contain("12345"));
            Assert.That(path, Does.Contain("24"));
        });
    }

    [Test]
    public void GetTempTrackFilePath_UniquePerCall()
    {
        var path1 = TrackFileHelper.GetTempTrackFilePath(1, "320", "mp3");
        var path2 = TrackFileHelper.GetTempTrackFilePath(1, "320", "mp3");
        Assert.That(path1, Is.Not.EqualTo(path2));
    }

    [Test]
    public void DownloadFile_UnsupportedUrl_ThrowsNotSupported()
    {
        Assert.Throws<NotSupportedException>(() =>
            TrackFileHelper.DownloadFile("ftp://unsupported.com/file.mp3", "/tmp/out.bin"));
    }
}
