using System.Globalization;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.Tidal;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Integration;

/// <summary>
/// Tidal against the real service, through the user's own tiddl login
/// (tiddl auth login). Without a session the tests skip, like the Qobuz
/// and Deezer live legs: MelodyBridge never owns the Tidal account.
/// </summary>
[TestFixture]
[Category("Live")]
public class TidalLiveTests
{
    private string _outDir = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _outDir = Path.Combine(Path.GetTempPath(), $"mb-tidal-live-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_outDir);
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        try { Directory.Delete(_outDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>The stored tiddl session, or null (then every test skips).</summary>
    private static TidalAuth.Session? StoredSession()
    {
        var appPath = TidalAuth.ResolveAppPath(Environment.GetEnvironmentVariable("TIDDL_PATH"));
        var session = TidalAuth.Read(appPath);
        return session is { Expired: false } ? session : null;
    }

    private static void RequireSession()
    {
        if (StoredSession() is null)
            Assert.Ignore("no logged-in tiddl session; run tiddl auth login first");
    }

    /// <summary>The plugin config the app would hold, fed from the environment.</summary>
    private sealed class EnvPluginConfig : IPluginConfig
    {
        public Task<string> GetAsync(string pluginId, string key, CancellationToken ct = default)
            => Task.FromResult(key switch
            {
                "command" => Environment.GetEnvironmentVariable("TIDDL_COMMAND") ?? "",
                "app_path" => Environment.GetEnvironmentVariable("TIDDL_PATH") ?? "",
                _ => "",
            });

        public Task SetAsync(string pluginId, string key, string? value, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static TidalDownloader Create()
        => new(new TidalClient(new HttpClient { Timeout = TimeSpan.FromMinutes(2) },
                NullLogger<TidalClient>.Instance),
            new EnvPluginConfig(), NullLogger<TidalDownloader>.Instance);

    [Test]
    public async Task IsAvailable_LoggedInTiddl_True()
    {
        RequireSession();
        Assert.That(await Create().IsAvailableAsync(), Is.True,
            "tiddl must be installed and logged in");
    }

    [Test]
    public async Task Search_WellKnownTrack_FindsATidalHit()
    {
        RequireSession();
        var hit = await Create().SearchAsync(new TrackQuery(
            "Daft Punk", "Around the World", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(429)));

        Assert.Multiple(() =>
        {
            Assert.That(hit, Is.Not.Null, "a well-known track must be in the Tidal catalogue");
            Assert.That(hit!.SourceUrl, Does.Contain("tidal.com"));
            Assert.That(hit.MatchConfidence, Is.EqualTo(MatchConfidence.High));
        });
    }

    [Test]
    public async Task Download_RealTrack_ProducesValidFlac()
    {
        RequireSession();
        var downloader = Create();

        var hit = await downloader.SearchAsync(new TrackQuery(
            "Daft Punk", "Around the World", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(429)));
        Assert.That(hit, Is.Not.Null, "precondition: the search finds the track");

        var result = await downloader.DownloadAsync(
            hit!.SourceUrl!, _outDir, "mb-tidal-live",
            new DownloadQuality(AudioFormat.Flac));

        Assert.That(result.Success, Is.True, $"download failed: {result.ErrorMessage}");
        Assert.That(result.FilePath, Is.Not.Null);
        Assert.That(File.Exists(result.FilePath), Is.True);

        var codec = await FfprobeAsync("-v error -show_entries stream=codec_name -of csv=p=0", result.FilePath!);
        Assert.That(codec.Trim(), Is.EqualTo("flac"), "a lossless request must land on FLAC");

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
