using System.Net.Http;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// Live Internet Archive plugin tests: real advancedsearch + metadata
/// API calls, real file download from archive servers, real tag
/// validation. No mocks.
/// </summary>
[TestFixture]
[Category("Live")]
[Category("ArchiveOrg")]
public class ArchiveOrgDownloaderLiveTests
{
    private ArchiveOrgDownloader _downloader = null!;
    private string _outDir = null!;

    private static HttpClient SharedClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Add("User-Agent", "MelodyBridge/1.0 (github.com/warreth/melodybridge)");
        http.Timeout = TimeSpan.FromMinutes(5);
        return http;
    }

    [OneTimeSetUp]
    public void Setup()
    {
        _downloader = new ArchiveOrgDownloader(SharedClient(),
            NullLogger<ArchiveOrgDownloader>.Instance);
        _outDir = Path.Combine(Path.GetTempPath(), $"mb-ia-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outDir);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try { Directory.Delete(_outDir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task Search_WellKnownRecording_FindsDirectMp3Url()
    {
        // A famous 78rpm digitization that is definitely in the archive.
        var hit = await _downloader.SearchAsync("Johanne Stockmarr", "Für Elise", 128);

        Assert.That(hit, Is.Not.Null, "archive.org must find this public-domain recording");
        Assert.That(hit!.SourceUrl, Does.Contain("archive.org/download/"),
            "hit must be a direct download URL we can pass to DownloadAsync");
        Assert.That(hit.SourceUrl, Does.EndWith(".mp3"));
    }

    [Test]
    public async Task Search_NonsenseQuery_ReturnsNull()
    {
        var hit = await _downloader.SearchAsync("zzzxqj", "qqqwwwzzz nonexistent track 999", 128);
        Assert.That(hit, Is.Null, "no result must come back as null, not an exception");
    }

    [Test]
    public async Task Download_DirectUrl_ProducesTaggedMp3()
    {
        var hit = await _downloader.SearchAsync("Johanne Stockmarr", "Für Elise", 128);
        Assert.That(hit, Is.Not.Null, "precondition: search finds the recording");

        var result = await _downloader.DownloadAsync(hit!.SourceUrl!, _outDir, "mb-ia-test-1");

        Assert.That(result.Success, Is.True, $"download failed: {result.ErrorMessage}");
        Assert.That(System.IO.File.Exists(result.FilePath), Is.True);

        var tag = MelodyBridge.Infrastructure.Tagging.TaglibHelper.ReadMelodyId(result.FilePath!);
        Assert.That(tag, Is.EqualTo("mb-ia-test-1"), "MELODY_ID must be in the real file");

        // The quality gate must have let this through: >= 128 kbps MP3.
        using var tf = TagLib.File.Create(result.FilePath!);
        Assert.That(tf.Properties?.AudioBitrate, Is.GreaterThanOrEqualTo(128),
            $"file bitrate {tf.Properties?.AudioBitrate} kbps is below the 128 kbps gate");
        Assert.That(tf.Properties?.Duration.TotalSeconds, Is.GreaterThan(30),
            "a real full recording, not a preview clip");
    }

    [Test]
    public async Task Download_NonArchiveUrl_FailsGracefully()
    {
        var result = await _downloader.DownloadAsync(
            "https://example.com/some/file.mp3", _outDir, "mb-ia-test-2");
        Assert.That(result.Success, Is.False);
        Assert.That(result.ErrorMessage, Does.Contain("archive.org"));
    }
}
