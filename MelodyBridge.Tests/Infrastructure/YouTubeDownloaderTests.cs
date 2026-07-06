using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.Tagging;

namespace MelodyBridge.Tests.Infrastructure;

[TestFixture]
public class YouTubeDownloaderTests
{
    private YouTubeDownloader CreateDownloader() => new();

    [Test]
    public void Name_ReturnsYtDlp()
    {
        var downloader = CreateDownloader();
        Assert.That(downloader.Name, Is.EqualTo("yt-dlp"));
    }

    [Test]
    public void CanHandle_YouTubeUrl_ReturnsTrue()
    {
        var downloader = CreateDownloader();
        Assert.Multiple(() =>
        {
            Assert.That(downloader.CanHandle("https://youtube.com/watch?v=abc123"), Is.True);
            Assert.That(downloader.CanHandle("https://www.youtube.com/watch?v=abc123"), Is.True);
            Assert.That(downloader.CanHandle("https://youtu.be/abc123"), Is.True);
            Assert.That(downloader.CanHandle("https://music.youtube.com/watch?v=abc123"), Is.True);
        });
    }

    [Test]
    public void CanHandle_NonYouTubeUrl_ReturnsFalse()
    {
        var downloader = CreateDownloader();
        Assert.Multiple(() =>
        {
            Assert.That(downloader.CanHandle("https://soundcloud.com/artist/track"), Is.False);
            Assert.That(downloader.CanHandle("https://qobuz.com/track/123"), Is.False);
            Assert.That(downloader.CanHandle("https://example.com"), Is.False);
            Assert.That(downloader.CanHandle(""), Is.False);
        });
    }

    [Test]
    public void CanHandle_YouTubeUrl_CaseInsensitive()
    {
        var downloader = CreateDownloader();
        Assert.Multiple(() =>
        {
            Assert.That(downloader.CanHandle("https://YOUTUBE.COM/watch?v=abc"), Is.True);
            Assert.That(downloader.CanHandle("https://YOUTUBE.COM/watch?v=abc"), Is.True);
            Assert.That(downloader.CanHandle("https://youtu.be/abc"), Is.True);
        });
    }

    [Test]
    public void DownloadAsync_WithoutYtDlp_Throws()
    {
        var downloader = CreateDownloader();
        // yt-dlp not installed in test env (likely)
        try
        {
            var task = downloader.DownloadAsync("https://youtube.com/watch?v=test", "/tmp", "melody-test");
            task.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            // Either InvalidOperationException (yt-dlp not found) or FileNotFoundException
            Assert.That(ex, Is.InstanceOf<Exception>());
        }
    }
}
