using System.Net;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Deezer;
using MelodyBridge.Infrastructure.Downloaders;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The deemix plugin: Deezer's public search for the ranking, and the
/// user's own deemix CLI for the download. The CLI is a POSIX shell stub
/// that writes a real (sniffable) audio file, so the whole path runs
/// without deemix, Docker or a Deezer account.
/// </summary>
[TestFixture]
public class DeemixDownloaderTests
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
        var dir = Path.Combine(Path.GetTempPath(), $"mb-deemix-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static DeezerClient.DeezerTrack Track(
        long id, string title, string artist, int duration, string? isrc = null)
        => new(id, title, artist, TimeSpan.FromSeconds(duration), isrc);

    private static DeemixDownloader Create(
        StubPluginConfig? config = null, string? dataDir = null, string? downloadsDir = null)
    {
        var http = new HttpClient(new HttpClientHandler())
        {
            BaseAddress = new Uri("https://api.deezer.com"),
        };
        return new DeemixDownloader(
            new DeezerClient(http, NullLogger<DeezerClient>.Instance),
            config ?? new StubPluginConfig(),
            NullLogger<DeemixDownloader>.Instance,
            dataDir, downloadsDir);
    }

    // ── Default folders ─────────────────────────────────────────────────

    [Test]
    public async Task Search_WithAnIsrc_AsksDeezerForTheCodeFirst()
    {
        var handler = new ScriptedHandler()
            .On("/search?q=isrc", _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":222,"title":"The Last Airbender (VIP)","duration":217,"isrc":"USRC17607839","artist":{"name":"SLVL"}}]}""",
                    System.Text.Encoding.UTF8, "application/json"),
            })
            .On("/search?q=", _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":999,"title":"The Last Airbender","duration":300,"isrc":"OTHER00000001","artist":{"name":"Avatar Soundtrack"}}]}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.deezer.com") };
        var plugin = new DeemixDownloader(
            new DeezerClient(http, NullLogger<DeezerClient>.Instance),
            new StubPluginConfig(), NullLogger<DeemixDownloader>.Instance,
            NewDir(), NewDir());

        var hit = await plugin.SearchAsync(new TrackQuery(
            "SLVL", "The Last Airbender (VIP)", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(217), Isrc: "USRC17607839"));

        Assert.Multiple(() =>
        {
            Assert.That(handler.Requests.First().Url, Does.Contain("isrc"),
                "the code is the first request");
            Assert.That(handler.Requests.First().Url, Does.Contain("USRC17607839"));
            Assert.That(hit, Is.Not.Null);
            Assert.That(hit!.SourceUrl, Is.EqualTo("https://www.deezer.com/track/222"));
        });
    }

    [Test]
    public void LocalDataRoot_FallsBackToTheHomeDirectory_WhenLocalAppDataIsEmpty()
    {
        // Linux reports LocalApplicationData as empty until ~/.local/share
        // exists, which used to turn the plugin's folders into paths
        // relative to the working directory.
        Assert.That(DeemixDownloader.LocalDataRoot("", "/home/me"),
            Is.EqualTo(Path.Combine("/home/me", ".local", "share")));
        Assert.That(DeemixDownloader.LocalDataRoot("/local/data", "/home/me"),
            Is.EqualTo("/local/data"), "an OS-provided root wins");
        Assert.That(DeemixDownloader.LocalDataRoot("", ""),
            Is.EqualTo("."), "with no home at all, stay relative to the working directory");
    }

    // ── Ranking ──────────────────────────────────────────────────────────

    [Test]
    public void Rank_PicksTheRealMatch_NotTheFirstItem()
    {
        var hit = DeemixDownloader.Rank(new[]
        {
            Track(111, "Never Gonna Give You Up (Cake Mix)", "Rick Astley", 347),
            Track(222, "Never Gonna Give You Up", "Rick Astley", 213, "GBARL0600786"),
        }, new TrackQuery("Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo("https://www.deezer.com/track/222"));
    }

    [Test]
    public void Rank_ExactIsrc_WinsOverACloserTitle()
    {
        var hit = DeemixDownloader.Rank(new[]
        {
            Track(111, "Never Gonna Give You Up", "Rick Astley", 213, "OTHERISRC001"),
            Track(222, "Never Gonna Give You Up (Remastered 2007)", "Rick Astley", 214, "GBARL0600786"),
        }, new TrackQuery("Rick Astley", "Never Gonna Give You Up", DownloadQuality.Any,
            ExpectedDuration: TimeSpan.FromSeconds(213), Isrc: "GBARL0600786"));

        Assert.That(hit, Is.Not.Null);
        Assert.That(hit!.SourceUrl, Is.EqualTo("https://www.deezer.com/track/222"),
            "an exact ISRC is the recording itself");
    }

    [Test]
    public void Rank_Empty_IsNull()
        => Assert.That(DeemixDownloader.Rank(Array.Empty<DeezerClient.DeezerTrack>(),
            new TrackQuery("A", "T", DownloadQuality.Any)), Is.Null);

    // ── Mapping helpers ──────────────────────────────────────────────────

    [TestCase(AudioFormat.Flac, null, null, "flac")]
    [TestCase(AudioFormat.Mp3, null, null, "320")]
    [TestCase(AudioFormat.Mp3, null, 160, "128")]
    [TestCase(AudioFormat.Mp3, 192, 256, null)]
    [TestCase(AudioFormat.Aac, null, null, null)]
    [TestCase(AudioFormat.Auto, null, null, "flac")]
    public void BitrateFor_MapsTheQualityBands(
        AudioFormat format, int? min, int? max, string? expected)
        => Assert.That(DeemixDownloader.BitrateFor(new DownloadQuality(format, min, max)),
            Is.EqualTo(expected));

    [TestCase("https://www.deezer.com/track/3135556", true, 3135556)]
    [TestCase("https://www.deezer.com/track/3135556?utm=1", true, 3135556)]
    [TestCase("3135556", true, 3135556)]
    [TestCase("https://open.spotify.com/track/abc", false, 0)]
    [TestCase("", false, 0)]
    public void TryExtractTrackId_HandlesUrlsAndIds(string input, bool expected, long expectedId)
    {
        var ok = DeemixDownloader.TryExtractTrackId(input, out var id);
        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.EqualTo(expected));
            Assert.That(id, Is.EqualTo(expectedId));
        });
    }

    [Test]
    public void DescribeFailure_TakesTheLastMeaningfulLine()
    {
        var reason = CliToolDownloader.DescribeFailure(
            new CliRunner.Result(1, "some noise\n", "\nfirst line\nInvalid ARL\n"), "deemix");

        Assert.That(reason, Is.EqualTo("deemix failed (exit 1): Invalid ARL"));
    }

    // ── Availability ─────────────────────────────────────────────────────

    [Test]
    public async Task IsAvailable_MissingCommand_False()
    {
        var config = new StubPluginConfig();
        config.Set("deemix", "command", "definitely-not-a-real-command-xyz");
        Assert.That(await Create(config).IsAvailableAsync(), Is.False);
    }

    [Test]
    public async Task IsAvailable_ConfiguredCommand_True()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var dir = NewDir();
        var config = new StubPluginConfig();
        config.Set("deemix", "command", CliStub.Producing(dir));
        Assert.That(await Create(config).IsAvailableAsync(), Is.True);
    }

    // ── Download ─────────────────────────────────────────────────────────

    [Test]
    public async Task Download_RunsTheCli_MovesAndTagsTheFile()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var downloads = NewDir();
        var output = NewDir();
        var script = CliStub.Producing(toolDir);
        var config = new StubPluginConfig();
        config.Set("deemix", "command", script);
        config.Set("deemix", "downloads_path", downloads);
        var plugin = Create(config);

        var result = await plugin.DownloadAsync(
            "https://www.deezer.com/track/3135556", output, "mb-deemix-1",
            new DownloadQuality(AudioFormat.Flac));

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.FilePath, Is.Not.Null);
            Assert.That(File.Exists(result.FilePath), Is.True);
            Assert.That(Path.GetExtension(result.FilePath), Is.EqualTo(".flac"));
            Assert.That(Path.GetFileName(result.FilePath), Is.EqualTo("Artist - Song.flac"));

            var args = CliStub.RecordedArguments(script);
            Assert.That(args, Does.Contain("-b flac"));
            Assert.That(args, Does.Contain("-p "));
            Assert.That(args, Does.Contain("https://www.deezer.com/track/3135556"));
            Assert.That(args, Does.Contain("mb-deemix-1"), "the per-track download folder");

            Assert.That(Directory.GetDirectories(downloads), Is.Empty,
                "the per-track folder is cleaned up");
        });
    }

    [Test]
    public async Task Download_CliFails_ReportsTheStderr()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var config = new StubPluginConfig();
        config.Set("deemix", "command", CliStub.Producing(toolDir, exitCode: 1, stderr: "Invalid ARL"));
        config.Set("deemix", "downloads_path", NewDir());

        var result = await Create(config).DownloadAsync(
            "https://www.deezer.com/track/3135556", NewDir(), "mb-fail");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("Invalid ARL"));
        });
    }

    [Test]
    public async Task Download_SuccessWithoutAFile_Fails()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var config = new StubPluginConfig();
        config.Set("deemix", "command", CliStub.Silent(toolDir));
        config.Set("deemix", "downloads_path", NewDir());

        var result = await Create(config).DownloadAsync(
            "https://www.deezer.com/track/3135556", NewDir(), "mb-empty");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("no audio file"));
        });
    }

    [Test]
    public async Task Download_WithoutAConfiguredFolder_UsesThePluginsOwnFolder()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var ownFolder = Path.Combine(NewDir(), "deemix-downloads");
        var script = CliStub.Producing(toolDir);
        var config = new StubPluginConfig();
        config.Set("deemix", "command", script);
        var plugin = Create(config, dataDir: null, downloadsDir: ownFolder);

        var result = await plugin.DownloadAsync(
            "https://www.deezer.com/track/3135556", NewDir(), "mb-own");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(CliStub.RecordedArguments(script), Does.Contain(ownFolder),
                "without a configured folder the plugin uses its own");
        });
    }

    [Test]
    public async Task Download_NoBitrateFits_FailsWithoutRunningTheCli()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var script = CliStub.Producing(toolDir);
        var config = new StubPluginConfig();
        config.Set("deemix", "command", script);
        config.Set("deemix", "downloads_path", NewDir());

        var result = await Create(config).DownloadAsync(
            "https://www.deezer.com/track/3135556", NewDir(), "mb-band",
            new DownloadQuality(AudioFormat.Aac));

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("no deemix bitrate fits"));
            Assert.That(CliStub.RecordedArguments(script), Is.Empty, "the CLI never ran");
        });
    }

    [Test]
    public async Task Download_WithArl_SeedsDeemixLoginAndEnvironment()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var dataDir = Path.Combine(NewDir(), "deemix-data");
        var downloads = NewDir();
        var output = NewDir();
        var script = CliStub.Custom(toolDir, "cli.sh",
            """
            echo "data=$DEEMIX_DATA_DIR" > "$(dirname "$0")/record.txt"
            cat "$DEEMIX_DATA_DIR/login.json" >> "$(dirname "$0")/record.txt"
            dir=""
            prev=""
            for a in "$@"; do
              if [ "$prev" = "-p" ]; then dir="$a"; fi
              prev="$a"
            done
            mkdir -p "$dir"
            printf 'fLaC' > "$dir/Artist - Song.flac"
            exit 0
            """);
        var config = new StubPluginConfig();
        config.Set("deemix", "command", script);
        config.Set("deemix", "downloads_path", downloads);
        config.Set("deemix", "arl", "test-arl-123");
        var plugin = Create(config, dataDir);

        var result = await plugin.DownloadAsync(
            "https://www.deezer.com/track/3135556", output, "mb-arl");

        var record = File.ReadAllText(Path.Combine(toolDir, "record.txt"));
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(record, Does.Contain($"data={dataDir}"),
                "the CLI must be pointed at the seeded config folder");
            Assert.That(record, Does.Contain("\"arl\":\"test-arl-123\""),
                "the ARL must reach deemix's login file");
        });
    }

    [Test]
    public async Task Download_WithoutArl_LeavesTheToolsOwnLoginAlone()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var dataDir = Path.Combine(NewDir(), "deemix-data");
        var config = new StubPluginConfig();
        config.Set("deemix", "command", CliStub.Custom(toolDir, "cli.sh",
            """
            echo "data=$DEEMIX_DATA_DIR" > "$(dirname "$0")/record.txt"
            dir=""
            prev=""
            for a in "$@"; do
              if [ "$prev" = "-p" ]; then dir="$a"; fi
              prev="$a"
            done
            mkdir -p "$dir"
            printf 'fLaC' > "$dir/Artist - Song.flac"
            exit 0
            """));
        config.Set("deemix", "downloads_path", NewDir());
        var plugin = Create(config, dataDir);

        var result = await plugin.DownloadAsync(
            "https://www.deezer.com/track/3135556", NewDir(), "mb-nolar");

        var record = File.ReadAllText(Path.Combine(toolDir, "record.txt"));
        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(record.Trim(), Is.EqualTo("data="), "no env override without an ARL");
            Assert.That(Directory.Exists(dataDir), Is.False, "no config folder is created");
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
            Assert.That(result.ErrorMessage, Does.Contain("not a Deezer track URL"));
        });
    }
}
