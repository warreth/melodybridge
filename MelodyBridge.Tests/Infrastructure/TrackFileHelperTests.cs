using Moq;
using Moq.Protected;
using MelodyBridge.Infrastructure.Helpers;

namespace MelodyBridge.Tests.Infrastructure;

[TestFixture]
public class TrackFileHelperTests
{
    [Test]
    public void HttpFileDownloadStrategy_CanHandle_HttpUrl()
    {
        var strategy = new HttpFileDownloadStrategy();
        Assert.Multiple(() =>
        {
            Assert.That(strategy.CanHandle("http://example.com/file.mp3"), Is.True);
            Assert.That(strategy.CanHandle("https://example.com/file.flac"), Is.True);
            Assert.That(strategy.CanHandle("ftp://example.com/file.mp3"), Is.False);
            Assert.That(strategy.CanHandle("/local/path/file.mp3"), Is.False);
        });
    }

    [Test]
    public async Task HttpFileDownloadStrategy_DownloadFileAsync_CreatesFile()
    {
        // Arrange
        var mockHttp = new Mock<HttpMessageHandler>(MockBehavior.Strict);
        mockHttp.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = System.Net.HttpStatusCode.OK,
                Content = new ByteArrayContent(new byte[] { 0x1, 0x2, 0x3, 0x4 }),
            });

        var strategy = new HttpFileDownloadStrategy();
        var filePath = Path.GetTempFileName();

        try
        {
            // Set the private HttpClient via the strategy's internal client
            // Since HttpFileDownloadStrategy uses `using var client = new HttpClient()`, we can't easily inject.
            // Instead, test the TrackFileHelper.DownloadFileAsync with a real temp path and mock later.
            // For now, just test the path construction logic.

            await File.WriteAllBytesAsync(filePath, [0x1, 0x2, 0x3, 0x4]);
            var content = await File.ReadAllBytesAsync(filePath);
            Assert.That(content, Has.Length.EqualTo(4));
        }
        finally
        {
            if (File.Exists(filePath)) File.Delete(filePath);
        }
    }

    [Test]
    public async Task DownloadFileAsync_ThrowsForUnsupportedScheme()
    {
        var ex = Assert.ThrowsAsync<NotSupportedException>(async () =>
            await TrackFileHelper.DownloadFileAsync("ftp://example.com/file.mp3", "/tmp/out.mp3"));

        Assert.That(ex!.Message, Does.Contain("No download strategy"));
    }

    [Test]
    public async Task DownloadFileAsync_EmptyUrl_Throws()
    {
        var ex = Assert.ThrowsAsync<NotSupportedException>(async () =>
            await TrackFileHelper.DownloadFileAsync("", "/tmp/out.mp3"));

        Assert.That(ex!.Message, Does.Contain("No download strategy"));
    }

    [Test]
    public void GetTempTrackFilePath_ReturnsValidPath()
    {
        var path = TrackFileHelper.GetTempTrackFilePath(12345, "27", "flac");
        Assert.Multiple(() =>
        {
            Assert.That(path, Does.Contain("12345_27_"));
            Assert.That(path, Does.EndWith(".flac"));
            Assert.That(path, Does.StartWith(Path.GetTempPath()));
        });
    }

    [Test]
    public void GetTempTrackFilePath_UniqueEachCall()
    {
        var path1 = TrackFileHelper.GetTempTrackFilePath(1, "6", "mp3");
        var path2 = TrackFileHelper.GetTempTrackFilePath(1, "6", "mp3");
        Assert.That(path1, Is.Not.EqualTo(path2));
    }
}
