using MelodyBridge.Infrastructure.Files;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The two helpers the archive ownership check leans on: the probe that
/// asks a volume whether it folds case, and the lock that keeps two
/// workers off the same file name.
/// </summary>
[TestFixture]
[Category("Dedup")]
public class FileSystemCaseTests
{
    [Test]
    public void TheProbeAgreesWithTheVolume()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mb-case-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // The volume's own answer: does a second spelling of a file
            // name point at the same file?
            var path = Path.Combine(dir, "Probe-Name.txt");
            File.WriteAllText(path, "x");
            var folds = File.Exists(Path.Combine(dir, "probe-name.txt"));

            Assert.That(FileSystemCase.FoldsCase(dir), Is.EqualTo(folds),
                "the probe must say what the volume does");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public void TheProbeLeavesNoFileBehind()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"mb-case-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            FileSystemCase.FoldsCase(dir);

            var leftovers = Directory.GetFiles(dir, ".mb-case-probe-*");
            Assert.That(leftovers, Is.Empty,
                "the probe cleans up after itself");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Test]
    public void AVolumeThatCannotBeAskedIsTreatedAsCaseFolding()
    {
        // A path under a folder that no process can write to. The app
        // cannot ask the volume there, so it takes the safe answer: two
        // spellings of one name are the same file, so a name is never
        // taken by accident.
        var path = OperatingSystem.IsWindows()
            ? @"Z:\mb-case-probe\sub"
            : "/proc/mb-case-probe/sub";

        Assert.That(FileSystemCase.FoldsCase(path), Is.True,
            "a volume that cannot be probed is treated as folding case");
    }

    [Test]
    public async Task TheLockForOneNameHoldsTheOtherWorker()
    {
        var table = new PathLockTable();
        var first = await table.AcquireAsync("/music/song.mp3");

        var released = false;
        var waiter = Task.Run(async () =>
        {
            using (await table.AcquireAsync("/music/song.mp3")) released = true;
        });

        await Task.Delay(100);
        Assert.That(released, Is.False,
            "the second worker waits while the first one holds the name");

        first.Dispose();
        await waiter.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(released, Is.True,
            "the second worker goes on once the name is free");
    }

    [Test]
    public async Task DifferentNamesDoNotWaitForEachOther()
    {
        var table = new PathLockTable();
        using var first = await table.AcquireAsync("/music/one.mp3");

        var second = await table.AcquireAsync("/music/two.mp3")
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(second, Is.Not.Null,
            "a different name has its own lock");
    }

    [Test]
    public async Task TwoSpellingsOfOneNameShareTheLockWhenTheVolumeFoldsCase()
    {
        // On a volume that folds case, "Song.mp3" and "song.mp3" are one
        // file. Two workers that want it under two spellings must wait
        // for each other, or the second one writes over the first one.
        var table = new PathLockTable();
        var first = await table.AcquireAsync("/archive/Song.mp3", foldCase: true);

        var released = false;
        var waiter = Task.Run(async () =>
        {
            using (await table.AcquireAsync("/archive/song.mp3", foldCase: true)) released = true;
        });

        await Task.Delay(100);
        Assert.That(released, Is.False,
            "the second spelling of the name waits for the first one");

        first.Dispose();
        await waiter.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(released, Is.True,
            "the second spelling goes on once the name is free");
    }

    [Test]
    public void TheKeyOfANameFollowsTheRuleOfTheVolume()
    {
        Assert.That(
            PathLockTable.KeyFor("/archive/Song.mp3", foldCase: true),
            Is.EqualTo(PathLockTable.KeyFor("/archive/song.mp3", foldCase: true)),
            "a volume that folds case reads two spellings as one name");
        Assert.That(
            PathLockTable.KeyFor("/archive/Song.mp3", foldCase: false),
            Is.Not.EqualTo(PathLockTable.KeyFor("/archive/song.mp3", foldCase: false)),
            "a volume that keeps the spellings apart has two names");
    }
}
