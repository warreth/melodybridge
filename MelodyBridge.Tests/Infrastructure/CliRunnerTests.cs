using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The shared CLI helper for the deemix and qobuz-dlp plugins: command
/// line splitting, PATH lookup, timeouts and the "find the produced
/// audio file" scan. Runs real processes through POSIX shell stubs where
/// the platform allows it.
/// </summary>
[TestFixture]
public class CliRunnerTests
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
        var dir = Path.Combine(Path.GetTempPath(), $"mb-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    /// <summary>Writes an executable POSIX shell script; Windows skips.</summary>
    private string Script(string directory, string name, string body)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        return path;
    }

    [TestCase("deemix", "deemix", 0)]
    [TestCase("docker exec Deemix deemix", "docker", 3)]
    [TestCase("  \"my tool\"  --flag  ", "my tool", 1)]
    [TestCase("qobuz-dlp", "qobuz-dlp", 0)]
    public void SplitCommand_HandlesPlainLinesAndQuotes(
        string line, string expectedExecutable, int expectedArgCount)
    {
        var split = CliRunner.SplitCommand(line);
        Assert.That(split, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(split!.Value.Executable, Is.EqualTo(expectedExecutable));
            Assert.That(split.Value.Arguments, Has.Count.EqualTo(expectedArgCount));
        });
    }

    [Test]
    public void SplitCommand_EmptyLine_IsNull()
        => Assert.That(CliRunner.SplitCommand("   "), Is.Null);

    [Test]
    public void Exists_FindsShellOnPath_AndRejectsNonsense()
    {
        Assert.Multiple(() =>
        {
            Assert.That(CliRunner.Exists("sh"), Is.True, "sh is on PATH everywhere POSIX");
            Assert.That(CliRunner.Exists("definitely-not-a-real-command-xyz"), Is.False);
            Assert.That(CliRunner.Exists(""), Is.False);
        });
    }

    [Test]
    public async Task RunAsync_CapturesOutputAndExitCode()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var dir = NewDir();
        var script = Script(dir, "ok.sh", "echo hello-out; echo hello-err 1>&2; exit 0");

        var result = await CliRunner.RunAsync(script, Array.Empty<string>(),
            TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.StdOut.Trim(), Is.EqualTo("hello-out"));
            Assert.That(result.StdErr.Trim(), Is.EqualTo("hello-err"));
        });
    }

    [Test]
    public async Task RunAsync_NonZeroExit_IsReported()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var dir = NewDir();
        var script = Script(dir, "fail.sh", "echo 'boom' 1>&2; exit 3");

        var result = await CliRunner.RunAsync(script, Array.Empty<string>(),
            TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.ExitCode, Is.EqualTo(3));
            Assert.That(result.StdErr, Does.Contain("boom"));
        });
    }

    [Test]
    public async Task RunAsync_MissingExecutable_ReportsInsteadOfThrowing()
    {
        var result = await CliRunner.RunAsync("definitely-not-a-real-command-xyz",
            Array.Empty<string>(), TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.StdErr, Does.Contain("not found on PATH"));
        });
    }

    [Test]
    public async Task RunAsync_Timeout_KillsAndReports()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var dir = NewDir();
        var script = Script(dir, "hang.sh", "sleep 30");

        var result = await CliRunner.RunAsync(script, Array.Empty<string>(),
            TimeSpan.FromMilliseconds(500), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.StdErr, Does.Contain("did not finish"));
        });
    }

    [Test]
    public async Task RunAsync_CallerCancellation_KillsTheChild()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var dir = NewDir();
        var marker = Path.Combine(dir, "still-running");
        // The script writes the marker a second in: if the cancellation
        // leaves it running, the marker shows up.
        var script = Script(dir, "cancelled.sh",
            $"sleep 1; touch \"{marker}\"; sleep 30");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Assert.CatchAsync<OperationCanceledException>(async () =>
            await CliRunner.RunAsync(script, Array.Empty<string>(),
                TimeSpan.FromSeconds(30), cts.Token));

        await Task.Delay(1500);
        Assert.That(File.Exists(marker), Is.False,
            "a cancelled run must kill the child instead of leaving it working");
    }

    [Test]
    public async Task RunAsync_PassesEnvironmentEntriesToTheChild()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell stub");

        var dir = NewDir();
        var script = Script(dir, "env.sh", "echo \"data=$MB_TEST_DIR\"; exit 0");

        var result = await CliRunner.RunAsync(script, Array.Empty<string>(),
            TimeSpan.FromSeconds(30), CancellationToken.None,
            new Dictionary<string, string> { ["MB_TEST_DIR"] = "/somewhere" });

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.StdOut.Trim(), Is.EqualTo("data=/somewhere"));
        });
    }

    [Test]
    public void FindNewestAudioFile_IgnoresArtworkAndM3u_PicksTheAudio()
    {
        var dir = NewDir();
        File.WriteAllText(Path.Combine(dir, "cover.jpg"), "not audio");
        File.WriteAllText(Path.Combine(dir, "playlist.m3u"), "#EXTM3U");
        Directory.CreateDirectory(Path.Combine(dir, "Artist - Album"));
        var flac = Path.Combine(dir, "Artist - Album", "01 Track.flac");
        File.WriteAllText(flac, "fLaC");

        Assert.That(CliRunner.FindNewestAudioFile(dir), Is.EqualTo(flac));
    }

    [Test]
    public void FindNewestAudioFile_MissingFolder_IsNull()
        => Assert.That(CliRunner.FindNewestAudioFile("/definitely/not/here"), Is.Null);
}
