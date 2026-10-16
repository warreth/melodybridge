using System.Globalization;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// slskd against a real, connected instance: real Soulseek search, real
/// transfer, real file validation with ffprobe. The environment supplies
/// the connection (SLSKD_URL, SLSKD_API_KEY, SLSKD_DOWNLOADS_PATH, where
/// the downloads folder is the path MelodyBridge sees); without it these
/// tests skip, like the media-server live fixtures.
/// </summary>
[TestFixture]
[Category("Live")]
public class SlskdLiveTests
{
    private string _outDir = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _outDir = Path.Combine(Path.GetTempPath(), $"mb-slskd-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outDir);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try { Directory.Delete(_outDir, recursive: true); } catch { /* best effort */ }
    }

    private static string? Url => Environment.GetEnvironmentVariable("SLSKD_URL");
    private static string? ApiKey => Environment.GetEnvironmentVariable("SLSKD_API_KEY");
    private static string? DownloadsPath => Environment.GetEnvironmentVariable("SLSKD_DOWNLOADS_PATH");

    private static void RequireInstance()
    {
        if (string.IsNullOrWhiteSpace(Url) || string.IsNullOrWhiteSpace(ApiKey) ||
            string.IsNullOrWhiteSpace(DownloadsPath))
            Assert.Ignore("SLSKD_URL, SLSKD_API_KEY and SLSKD_DOWNLOADS_PATH must point at a connected slskd instance");
    }

    /// <summary>The plugin config the app would hold, fed from the environment.</summary>
    private sealed class EnvPluginConfig : IPluginConfig
    {
        public Task<string> GetAsync(string pluginId, string key, CancellationToken ct = default)
            => Task.FromResult(key switch
            {
                "base_url" => Url ?? "",
                "api_key" => ApiKey ?? "",
                "downloads_path" => DownloadsPath ?? "",
                _ => "",
            });

        public Task SetAsync(string pluginId, string key, string? value, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static SlskdDownloader Create()
        => new(new HttpClient { Timeout = TimeSpan.FromMinutes(2) },
            new EnvPluginConfig(), NullLogger<SlskdDownloader>.Instance)
        {
            // A live peer can sit in a queue; give it a few minutes.
            TransferTimeout = TimeSpan.FromMinutes(5),
        };

    [Test]
    public async Task IsAvailable_ConnectedInstance_True()
    {
        RequireInstance();
        Assert.That(await Create().IsAvailableAsync(), Is.True,
            "slskd must be connected and logged in to the Soulseek network");
    }

    [Test]
    public async Task Search_WellKnownTrack_FindsAPeerFile()
    {
        RequireInstance();
        var hit = await Create().SearchAsync(new TrackQuery(
            "Daft Punk", "Around the World", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(429)));

        Assert.That(hit, Is.Not.Null, "a well-known track must have Soulseek sources");
        Assert.That(hit!.SourceUrl, Does.StartWith("slskd:"), "the hit carries a transfer reference");
        Assert.That(hit.Title, Is.Not.Empty);
    }

    [Test]
    public async Task Download_RealTransfer_ProducesValidAudio()
    {
        RequireInstance();
        var downloader = Create();

        var hit = await downloader.SearchAsync(new TrackQuery(
            "Daft Punk", "Around the World", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(429)));
        Assert.That(hit, Is.Not.Null, "precondition: the search finds a source");

        var result = await downloader.DownloadAsync(hit!.SourceUrl!, _outDir, "mb-slskd-live");
        Assert.That(result.Success, Is.True, $"download failed: {result.ErrorMessage}");
        Assert.That(result.FilePath, Is.Not.Null);
        Assert.That(File.Exists(result.FilePath), Is.True, "reported file must exist");
        Assert.That(new FileInfo(result.FilePath!).Length, Is.GreaterThan(1024 * 1024),
            "a real peer file is megabytes, not a stub");

        // The bytes must be real audio: ffprobe has the last word.
        var codec = await FfprobeAsync("-v error -show_entries stream=codec_name -of csv=p=0", result.FilePath!);
        Assert.That(codec.Trim(), Is.Not.Empty, "ffprobe must see an audio stream");

        var duration = await FfprobeAsync("-v quiet -show_entries format=duration -of csv=p=0", result.FilePath!);
        Assert.That(double.TryParse(duration.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds),
            Is.True, $"ffprobe duration parse: '{duration}'");
        Assert.That(seconds, Is.InRange(60, 600), "a full-length track, not a preview");
    }

    private static async Task<string> FfprobeAsync(string arguments, string file)
    {
        using var ffprobe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ffprobe",
            Arguments = $"{arguments} \"{file}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var stdout = await ffprobe.StandardOutput.ReadToEndAsync();
        await ffprobe.WaitForExitAsync();
        return stdout;
    }
}
