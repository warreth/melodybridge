using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.Qobuz;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The qobuz-dlp plugin: the native search (bundle + token) for the
/// ranking, and the user's own qobuz-dlp CLI for the download. The CLI
/// is a POSIX shell stub that writes a real (sniffable) audio file, so
/// the whole path runs without Python or a Qobuz account.
/// </summary>
[TestFixture]
public class QobuzDownloaderTests
{
    private readonly List<string> _tempDirs = new();

    [TearDown]
    public void TearDown()
    {
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
        _tempDirs.Clear();
    }

    private string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mb-qobuz-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static QobuzClient.QobuzTrack Track(
        long id, string title, string artist, int duration, string? isrc = null, bool hires = false)
        => new(id, title, artist, TimeSpan.FromSeconds(duration), isrc, hires ? 24 : 16, hires);

    private static QobuzDownloader Create(StubPluginConfig? config = null)
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri("https://www.qobuz.com"),
        };
        return new QobuzDownloader(
            new QobuzClient(http, NullLogger<QobuzClient>.Instance),
            config ?? new StubPluginConfig(),
            NullLogger<QobuzDownloader>.Instance);
    }

    // ── Ranking ──────────────────────────────────────────────────────────

    [Test]
    public void Rank_PicksTheRealMatch_NotTheFirstItem()
    {
        var hit = QobuzDownloader.Rank(new[]
        {
            Track(111, "Around the World (Remix)", "Some DJ", 240),
            Track(222, "Around the World", "Daft Punk", 429, "GBDUW0600009"),
        }, new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any));

        Assert.That(hit, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(hit!.SourceUrl, Is.EqualTo("https://play.qobuz.com/track/222"));
            Assert.That(hit.Artist, Is.EqualTo("Daft Punk"));
        });
    }

    [Test]
    public void Rank_ExactIsrc_WinsOverACloserTitle()
    {
        var hit = QobuzDownloader.Rank(new[]
        {
            Track(111, "Around the World", "Daft Punk", 429, "OTHERISRC001"),
            Track(222, "Around the World (Remastered 2007)", "Daft Punk", 430, "GBDUW0600009"),
        }, new TrackQuery("Daft Punk", "Around the World", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(429), Isrc: "GBDUW0600009"));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo("https://play.qobuz.com/track/222"));
    }

    [Test]
    public void Rank_Empty_IsNull()
        => Assert.That(QobuzDownloader.Rank(Array.Empty<QobuzClient.QobuzTrack>(),
            new TrackQuery("A", "T", DownloadQuality.Any)), Is.Null);

    // ── Mapping helpers ──────────────────────────────────────────────────

    [TestCase(AudioFormat.Flac, "6", "6")]
    [TestCase(AudioFormat.Flac, "27", "27")]
    [TestCase(AudioFormat.Mp3, "6", "5")]
    [TestCase(AudioFormat.Aac, "6", null)]
    [TestCase(AudioFormat.Auto, "7", "7")]
    public void QualityFor_MapsTheQualityBands(
        AudioFormat format, string configured, string? expected)
        => Assert.That(QobuzDownloader.QualityFor(DownloadQuality.Any with { Format = format },
            configured), Is.EqualTo(expected));

    [TestCase("6", "6")]
    [TestCase("27", "27")]
    [TestCase(" 7 ", "7")]
    [TestCase("", "6")]
    [TestCase("99", "6")]
    [TestCase("nonsense", "6")]
    public void NormalizeQuality_FallsBackToCdQuality(string input, string expected)
        => Assert.That(QobuzDownloader.NormalizeQuality(input), Is.EqualTo(expected));

    [Test]
    public void ReadTokenFile_ReadsTheStoredToken_AndHandlesTheEdges()
    {
        var configFolder = Path.Combine(NewDir(), "qobuz-dl");
        Assert.That(QobuzDownloader.ReadTokenFile(configFolder), Is.Null, "no qobuz-dlp config yet");

        Directory.CreateDirectory(configFolder);
        File.WriteAllText(Path.Combine(configFolder, ".qobuz_dl.oauth.txt"), "  token-abc  \n");
        Assert.That(QobuzDownloader.ReadTokenFile(configFolder), Is.EqualTo("token-abc"));

        File.WriteAllText(Path.Combine(configFolder, ".qobuz_dl.oauth.txt"), "   ");
        Assert.That(QobuzDownloader.ReadTokenFile(configFolder), Is.Null, "an empty file is no token");
    }

    // ── Availability ─────────────────────────────────────────────────────

    [Test]
    public async Task IsAvailable_MissingCommand_False()
    {
        var config = new StubPluginConfig();
        config.Set("qobuz", "command", "definitely-not-a-real-command-xyz");
        Assert.That(await Create(config).IsAvailableAsync(), Is.False);
    }

    // ── Download ─────────────────────────────────────────────────────────

    [Test]
    public async Task Download_RunsTheCli_MovesAndTagsTheFile()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var output = NewDir();
        var script = CliStub.Producing(toolDir);
        var config = new StubPluginConfig();
        config.Set("qobuz", "command", script);
        var plugin = Create(config);

        var result = await plugin.DownloadAsync(
            "https://play.qobuz.com/track/5966783", output, "mb-qobuz-1",
            new DownloadQuality(AudioFormat.Flac));

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.FilePath, Is.Not.Null);
            Assert.That(File.Exists(result.FilePath), Is.True);
            Assert.That(Path.GetExtension(result.FilePath), Is.EqualTo(".flac"));

            var args = CliStub.RecordedArguments(script);
            Assert.That(args, Does.Contain("dl"));
            Assert.That(args, Does.Contain("--directory"));
            Assert.That(args, Does.Contain("--quality 6"));
            Assert.That(args, Does.Contain("--no-m3u"));
            Assert.That(args, Does.Contain("--no-db"));
            Assert.That(args, Does.Contain("https://play.qobuz.com/track/5966783"));

            Assert.That(Directory.GetDirectories(output), Is.Empty,
                "the hidden per-track folder is cleaned up");
        });
    }

    [Test]
    public async Task Download_HiResQuality_UsesTheConfiguredTier()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var script = CliStub.Producing(toolDir);
        var config = new StubPluginConfig();
        config.Set("qobuz", "command", script);
        config.Set("qobuz", "quality", "27");

        var result = await Create(config).DownloadAsync(
            "https://play.qobuz.com/track/5966783", NewDir(), "mb-hires",
            new DownloadQuality(AudioFormat.Flac));

        Assert.That(result.Success, Is.True, result.ErrorMessage);
        Assert.That(CliStub.RecordedArguments(script), Does.Contain("--quality 27"));
    }

    [Test]
    public async Task Download_CliFails_ReportsTheStderr()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var config = new StubPluginConfig();
        config.Set("qobuz", "command",
            CliStub.Producing(toolDir, exitCode: 1, stderr: "Login expired"));
        var plugin = Create(config);

        var result = await plugin.DownloadAsync(
            "https://play.qobuz.com/track/5966783", NewDir(), "mb-fail");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("Login expired"));
        });
    }

    [Test]
    public async Task Download_NoQualityFits_FailsWithoutRunningTheCli()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var script = CliStub.Producing(toolDir);
        var config = new StubPluginConfig();
        config.Set("qobuz", "command", script);

        var result = await Create(config).DownloadAsync(
            "https://play.qobuz.com/track/5966783", NewDir(), "mb-aac",
            new DownloadQuality(AudioFormat.Aac));

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("no qobuz-dlp quality fits"));
            Assert.That(CliStub.RecordedArguments(script), Is.Empty, "the CLI never ran");
        });
    }

    [Test]
    public async Task Download_InvalidUrl_Fails()
    {
        var result = await Create().DownloadAsync(
            "https://example.com/not-a-track", NewDir(), "mb-bad");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("not a Qobuz track URL"));
        });
    }
}
