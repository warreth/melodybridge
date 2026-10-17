using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Downloaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The shared CLI download flow, through a minimal subclass: the request
/// gate (an error never touches the command or the disk), the real POSIX
/// stub run, cleanup of the work folder whatever happens, and the
/// per-instance run gate. No real tool is involved.
/// </summary>
[TestFixture]
public class CliToolDownloaderTests
{
    private sealed class StubTool : CliToolDownloader
    {
        private readonly string _workDirectory;

        /// <summary>Arguments for the run; "&lt;workdir&gt;" is replaced with the real folder.</summary>
        public CliToolRequest Request { get; set; } = new(["--directory", "<workdir>"]);

        public bool Prepared { get; private set; }

        public StubTool(IPluginConfig config, string workDirectory)
            : base(config, NullLogger<StubTool>.Instance)
        {
            _workDirectory = workDirectory;
        }

        public override string Id => "stubtool";
        public override string Name => "Stub tool";
        public override PluginCapabilities Capabilities => PluginCapabilities.Any;

        protected override string ToolLabel => "stubtool";
        protected override string DefaultCommandLine => "definitely-not-a-real-command-xyz";
        protected override string CommandConfigKey => "command";
        protected override TimeSpan RunTimeout => TimeSpan.FromSeconds(30);

        public override Task<bool> IsAvailableAsync(CancellationToken ct = default)
            => Task.FromResult(true);

        public override Task<DownloaderSearchHit?> SearchAsync(
            TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(null);

        protected override Task<string> WorkDirectoryAsync(
            string outputDirectory, string? melodyId, CancellationToken ct)
            => Task.FromResult(Path.Combine(outputDirectory, _workDirectory));

        protected override Task<CliToolRequest> BuildRequestAsync(
            string sourceUrl, DownloadQuality quality, string workDirectory, CancellationToken ct)
        {
            if (Request.Error is not null)
                return Task.FromResult(Request);

            var arguments = Request.Arguments
                .Select(a => a == "<workdir>" ? workDirectory : a)
                .ToList();
            return Task.FromResult(Request with { Arguments = arguments });
        }

        protected override Task<IReadOnlyDictionary<string, string>?> PrepareEnvironmentAsync(
            CancellationToken ct)
        {
            Prepared = true;
            return Task.FromResult<IReadOnlyDictionary<string, string>?>(null);
        }
    }

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
        var dir = Path.Combine(Path.GetTempPath(), $"mb-clitool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    [Test]
    public async Task DownloadAsync_RequestError_StopsBeforeCommandOrDisk()
    {
        var output = NewDir();
        var tool = new StubTool(new StubPluginConfig(), ".mb-stub")
        {
            Request = new CliToolRequest([], "not a stub tool URL"),
        };

        var result = await tool.DownloadAsync("https://example.com/x", output, "mb-1");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Is.EqualTo("not a stub tool URL"));
            Assert.That(tool.Prepared, Is.False, "no config seeding when the request cannot run");
            Assert.That(Directory.GetDirectories(output), Is.Empty, "no work folder is created");
        });
    }

    [Test]
    public async Task DownloadAsync_MissingCommand_FailsWithTheToolLabel()
    {
        var tool = new StubTool(new StubPluginConfig(), ".mb-stub");

        var result = await tool.DownloadAsync(
            "https://example.com/x", NewDir(), "mb-2");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Is.EqualTo(
                "the stubtool command is not configured or not on PATH"));
        });
    }

    [Test]
    public async Task DownloadAsync_CliFailure_ReportsTheStderrAndCleansUp()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var output = NewDir();
        var config = new StubPluginConfig();
        config.Set("stubtool", "command",
            CliStub.Custom(toolDir, "cli.sh", "echo 'login expired' 1>&2\nexit 3\n"));
        var tool = new StubTool(config, ".mb-stub");

        var result = await tool.DownloadAsync("https://example.com/x", output, "mb-3");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ErrorMessage, Does.Contain("stubtool failed (exit 3)"));
            Assert.That(result.ErrorMessage, Does.Contain("login expired"));
            Assert.That(tool.Prepared, Is.True, "the run reached config seeding");
            Assert.That(Directory.GetDirectories(output), Is.Empty,
                "the work folder is cleaned up after a failure");
        });
    }

    [Test]
    public async Task DownloadAsync_Success_MovesSniffsAndTagsTheFile()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var toolDir = NewDir();
        var output = NewDir();
        var config = new StubPluginConfig();
        config.Set("stubtool", "command", CliStub.Producing(toolDir));
        var tool = new StubTool(config, ".mb-stub");

        var result = await tool.DownloadAsync("https://example.com/x", output, "mb-4");

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True, result.ErrorMessage);
            Assert.That(result.FilePath, Does.EndWith(".flac"));
            Assert.That(File.Exists(result.FilePath), Is.True);
            Assert.That(Path.GetFileName(result.FilePath), Is.EqualTo("Artist - Song.flac"));
            Assert.That(Directory.GetDirectories(output), Is.Empty,
                "the work folder is cleaned up after a success too");
        });
    }

    [Test]
    public async Task DownloadAsync_TwoInstances_DoNotShareTheRunGate()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        // One instance starts a slow run; a second, independent instance
        // must not wait behind it. A shared instance gate would make the
        // second call take at least as long as the first.
        var toolDir = NewDir();
        var slowScript = CliStub.Custom(toolDir, "slow.sh",
            """
            dir=""
            prev=""
            for a in "$@"; do
              if [ "$prev" = "--directory" ]; then dir="$a"; fi
              prev="$a"
            done
            sleep 2
            mkdir -p "$dir"
            printf 'fLaC' > "$dir/Artist - Song.flac"
            exit 0
            """);
        var fastScript = CliStub.Producing(toolDir, name: "fast.sh");

        var slowConfig = new StubPluginConfig();
        slowConfig.Set("stubtool", "command", slowScript);
        var fastConfig = new StubPluginConfig();
        fastConfig.Set("stubtool", "command", fastScript);

        var slow = new StubTool(slowConfig, ".mb-slow");
        var fast = new StubTool(fastConfig, ".mb-fast");

        var slowRun = slow.DownloadAsync("https://example.com/x", NewDir(), "mb-slow");
        // Let the slow run take its gate.
        await Task.Delay(300);
        var started = DateTimeOffset.UtcNow;
        var fastResult = await fast.DownloadAsync("https://example.com/x", NewDir(), "mb-fast");
        var elapsed = DateTimeOffset.UtcNow - started;

        var slowResult = await slowRun;

        Assert.Multiple(() =>
        {
            Assert.That(fastResult.Success, Is.True, fastResult.ErrorMessage);
            Assert.That(slowResult.Success, Is.True, slowResult.ErrorMessage);
            Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(2)),
                "a second instance must not wait on the first instance's gate");
        });
    }
}
