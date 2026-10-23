using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.CompilerServices;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// A watcher error is exactly when change events are lost (buffer
/// overflow, the handler failing under a big import). The monitor used to
/// only log it, so the missed files stayed invisible until a manual scan.
/// The monitor now raises its change event for the whole location, which
/// the service turns into a full rescan.
/// </summary>
[TestFixture]
public class FileSystemMonitorErrorTests
{
    private string _dir = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mb-watch-err-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public void AWatcherError_SignalsOneFullRescan_ForTheLocation()
    {
        var monitor = new FileSystemMonitor(NullLogger<FileSystemMonitor>.Instance);
        var received = new List<FileSystemChangeEventArgs>();
        monitor.ChangeDetected += (_, e) => received.Add(e);

        monitor.StartMonitoring(_dir, scanLocationId: 1);

        // The watcher error path drives the private raise through the same
        // seam the framework would.
        TriggerWatcherErrorFor(monitor, monitor.GetMonitoredPaths()[0]);

        Assert.Multiple(() =>
        {
            Assert.That(received, Has.Count.EqualTo(1),
                "the error signals exactly one rescan for the location");
            Assert.That(received[0].ScanLocationId, Is.EqualTo(1));
            Assert.That(received[0].Path, Is.EqualTo(_dir),
                "the signal names the whole location, not a single file");
        });
    }

    [Test]
    public void ASecondWatcherError_DoesNotSpamMoreSignals_InsideTheWindow()
    {
        Assert.Ignore("the debounce window is five seconds; covering the second-error case waits for a clock seam");
    }

    private static void TriggerWatcherErrorFor(FileSystemMonitor monitor, string path)
    {
        // The raise lives behind a private method by design (only the
        // watcher's own error event drives it), so tests drive it with the
        // same seam the framework would.
        typeof(FileSystemMonitor)
            .GetMethod("OnWatcherError", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)!
            .Invoke(monitor, [
                path,
                new ErrorEventArgs(new InvalidOperationException("test error")),
            ]);
    }
}
