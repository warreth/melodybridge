using System.ComponentModel;
using MelodyBridge.Infrastructure.Downloaders;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The yt-dlp updater's decision logic against a scripted runner: which
/// command is tried when, what the UI gets told, and that a missing
/// program falls through to the next candidate. No real process runs.
/// </summary>
[TestFixture]
public class YtDlpUpdaterTests
{
    private sealed class ScriptedRunner
    {
        public List<string> Commands { get; } = new();
        public Func<string, IReadOnlyList<string>, (int Exit, string Stdout, string Stderr)> Respond { get; set; } =
            (_, _) => (0, "", "");

        public Task<(int Exit, string Stdout, string Stderr)> Run(
            string program, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            Commands.Add(program + " " + string.Join(' ', arguments));
            var (exit, stdout, stderr) = Respond(program, arguments);
            return Task.FromResult((exit, stdout, stderr));
        }
    }

    private static YtDlpUpdater NewUpdater(ScriptedRunner runner, string? binary)
        => new(NullLogger<YtDlpUpdater>.Instance, () => binary, runner.Run);

    [Test]
    public async Task GetVersion_NullWhenNotInstalled()
    {
        var runner = new ScriptedRunner();
        var updater = NewUpdater(runner, binary: null);

        Assert.That(await updater.GetVersionAsync(), Is.Null);
        Assert.That(runner.Commands, Is.Empty, "nothing runs when no binary is on PATH");
    }

    [Test]
    public async Task GetVersion_AFailingBinaryIsNotProbedAgain()
    {
        var runner = new ScriptedRunner { Respond = (_, _) => (1, "", "boom") };
        var updater = NewUpdater(runner, "/usr/local/bin/yt-dlp");

        Assert.That(await updater.GetVersionAsync(), Is.Null);
        Assert.That(await updater.GetVersionAsync(), Is.Null);
        Assert.That(runner.Commands, Has.Count.EqualTo(1),
            "a broken binary must not be started on every page visit");
    }

    [Test]
    public async Task GetVersion_ReadsTheBinary()
    {
        var runner = new ScriptedRunner { Respond = (_, _) => (0, "2026.08.19\n", "") };
        var updater = NewUpdater(runner, "/usr/local/bin/yt-dlp");

        Assert.That(await updater.GetVersionAsync(), Is.EqualTo("2026.08.19"));
        Assert.That(runner.Commands.Single(), Is.EqualTo("/usr/local/bin/yt-dlp --version"));
    }

    [Test]
    public async Task GetVersion_SecondRead_ComesFromCache()
    {
        var runner = new ScriptedRunner { Respond = (_, _) => (0, "2026.08.19\n", "") };
        var updater = NewUpdater(runner, "/usr/local/bin/yt-dlp");

        Assert.That(await updater.GetVersionAsync(), Is.EqualTo("2026.08.19"));
        Assert.That(await updater.GetVersionAsync(), Is.EqualTo("2026.08.19"));
        Assert.That(runner.Commands, Has.Count.EqualTo(1),
            "a version read must not start a second yt-dlp process");
    }

    [Test]
    public async Task Update_ReplacesTheCachedVersion()
    {
        var current = "2026.08.19";
        var runner = new ScriptedRunner
        {
            Respond = (_, args) =>
            {
                if (args.Contains("--version")) return (0, current + "\n", "");
                current = "2026.10.14";
                return (0, "yt-dlp is up to date", "");
            },
        };
        var updater = NewUpdater(runner, "/usr/local/bin/yt-dlp");

        Assert.That(await updater.GetVersionAsync(), Is.EqualTo("2026.08.19"));

        var result = await updater.UpdateAsync();
        var version = await updater.GetVersionAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Message, Does.Contain("2026.10.14"));
            Assert.That(version, Is.EqualTo("2026.10.14"),
                "the old cached version must not survive an update");
        });
    }

    [Test]
    public async Task Update_SelfUpdateWins()
    {
        var runner = new ScriptedRunner
        {
            Respond = (_, args) => args.Contains("--version")
                ? (0, "2026.10.14\n", "")
                : (0, "yt-dlp is up to date", ""),
        };
        var updater = NewUpdater(runner, "/usr/local/bin/yt-dlp");

        var result = await updater.UpdateAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Updated, Is.True);
            Assert.That(result.Message, Does.Contain("updated itself"));
            Assert.That(result.Message, Does.Contain("2026.10.14"));
            Assert.That(runner.Commands.Any(c => c.Contains("pip")), Is.False,
                "pip is not tried after a successful self-update");
        });
    }

    [Test]
    public async Task Update_PipInstall_FallsThroughToPip()
    {
        var runner = new ScriptedRunner
        {
            Respond = (program, args) =>
            {
                if (args.Contains("--version")) return (0, "2026.10.14\n", "");
                if (args.Contains("-U")) return (1, "", "You installed yt-dlp with pip; use pip to update");
                return program == "python3" ? (0, "Successfully installed yt-dlp-2026.10.14", "") : (0, "", "");
            },
        };
        var updater = NewUpdater(runner, "/usr/local/bin/yt-dlp");

        var result = await updater.UpdateAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Updated, Is.True);
            Assert.That(result.Message, Does.Contain("python3 -m pip"));
            Assert.That(runner.Commands.Any(c => c.StartsWith("python3 -m pip install --upgrade yt-dlp")), Is.True);
        });
    }

    [Test]
    public async Task Update_ExternallyManagedEnvironment_RetriesWithTheFlag()
    {
        var runner = new ScriptedRunner
        {
            Respond = (_, args) =>
            {
                if (args.Contains("--version")) return (0, "2026.10.14\n", "");
                if (args.Contains("-U")) return (1, "", "pip");
                if (args.Contains("--break-system-packages")) return (0, "Successfully installed", "");
                return (1, "", "error: externally-managed-environment");
            },
        };
        var updater = NewUpdater(runner, "/usr/local/bin/yt-dlp");

        var result = await updater.UpdateAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Updated, Is.True);
            Assert.That(runner.Commands.Any(c => c.Contains("--break-system-packages")), Is.True,
                "the distro flag is retried when pip asks for it");
        });
    }

    [Test]
    public async Task Update_MissingPython3_FallsThroughToPython()
    {
        var runner = new ScriptedRunner
        {
            Respond = (program, args) =>
            {
                if (args.Contains("-U")) return (1, "", "pip");
                if (program == "python3") throw new Win32Exception("python3 not found");
                if (args.Contains("--version")) return (0, "2026.10.14\n", "");
                return (0, "Successfully installed", "");
            },
        };
        var updater = NewUpdater(runner, "/usr/local/bin/yt-dlp");

        var result = await updater.UpdateAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Updated, Is.True);
            Assert.That(runner.Commands.Any(c => c.StartsWith("python -m pip")), Is.True,
                "python is tried when python3 is missing");
        });
    }

    [Test]
    public async Task Update_NoBinaryAtAll_StillTriesPip()
    {
        var runner = new ScriptedRunner
        {
            Respond = (_, args) => args.Contains("--version") ? (0, "", "") : (0, "Successfully installed", ""),
        };
        var updater = NewUpdater(runner, binary: null);

        var result = await updater.UpdateAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Updated, Is.True);
            Assert.That(runner.Commands.Any(c => c.StartsWith("python3 -m pip")), Is.True,
                "pip can install yt-dlp when the binary is not on PATH");
        });
    }

    [Test]
    public async Task Update_NothingAvailable_IsHonest()
    {
        var runner = new ScriptedRunner
        {
            Respond = (_, args) =>
            {
                if (args.Contains("-U")) return (1, "", "pip");
                throw new Win32Exception("not found");
            },
        };
        var updater = NewUpdater(runner, "/usr/local/bin/yt-dlp");

        var result = await updater.UpdateAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Updated, Is.False);
            Assert.That(result.Message, Does.Contain("Nothing to update with"));
        });
    }

    [Test]
    public async Task RunProcess_CallerCancellation_KillsTheChild()
    {
        if (OperatingSystem.IsWindows()) Assert.Ignore("POSIX shell script");

        var dir = Path.Combine(Path.GetTempPath(), $"mb-updater-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var marker = Path.Combine(dir, "still-running");
            var script = Path.Combine(dir, "slow.sh");
            // Writes the marker a second in: if the cancellation leaves the
            // child running, the marker shows up.
            File.WriteAllText(script, $"#!/bin/sh\nsleep 1; touch \"{marker}\"; sleep 30\n");
            File.SetUnixFileMode(script,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            Assert.CatchAsync<OperationCanceledException>(async () =>
                await YtDlpUpdater.RunProcessAsync(script, Array.Empty<string>(), cts.Token));

            await Task.Delay(1500);
            Assert.That(File.Exists(marker), Is.False,
                "a cancelled update must kill yt-dlp or pip instead of leaving it working");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task Update_FailureMessage_IsBounded()
    {
        var longError = new string('x', 1000) + "BOOM";
        var runner = new ScriptedRunner
        {
            Respond = (_, args) => args.Contains("-U") ? (1, "", "pip") : (1, "", longError),
        };
        var updater = NewUpdater(runner, "/usr/local/bin/yt-dlp");

        var result = await updater.UpdateAsync();

        Assert.Multiple(() =>
        {
            Assert.That(result.Updated, Is.False);
            Assert.That(result.Message, Does.EndWith("BOOM"));
            Assert.That(result.Message.Length, Is.LessThanOrEqualTo(400));
        });
    }
}
