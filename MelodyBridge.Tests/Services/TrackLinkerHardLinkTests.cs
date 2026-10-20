using MelodyBridge.Infrastructure.Files;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The link attempt behind dedup, against the real filesystem. A pending
/// row can find its own file already in place, or an earlier pass can
/// have made the exact link this one wants; both are success. A genuine
/// failure must carry the OS reason instead of disappearing.
/// </summary>
[TestFixture]
[Category("Dedup")]
public class TrackLinkerHardLinkTests
{
    private TrackLinker _linker = null!;
    private string _dir = null!;

    [SetUp]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mb-linker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _linker = new TrackLinker(
            null!, null!, new HardLinkService(), NullLogger<TrackLinker>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public void ExistingLinkToTheSameFile_SucceedsWithoutAddingAName()
    {
        var target = Path.Combine(_dir, "source.mp3");
        var link = Path.Combine(_dir, "linked.mp3");
        File.WriteAllText(target, "bytes");
        new HardLinkService().Create(link, target);

        var ok = _linker.TryHardLink(link, target, out var failure);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True, "the desired link already exists");
            Assert.That(failure, Is.Null);
            Assert.That(new HardLinkService().LinkCount(target), Is.EqualTo(2),
                "adopting the link must not add a second name");
        });
    }

    [Test]
    public void ExistingPlainFile_IsReplacedByTheLink()
    {
        var target = Path.Combine(_dir, "source.mp3");
        var stale = Path.Combine(_dir, "stale.mp3");
        File.WriteAllText(target, "source bytes");
        File.WriteAllText(stale, "stale bytes");

        var ok = _linker.TryHardLink(stale, target, replaceExisting: true, out var failure);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.True);
            Assert.That(failure, Is.Null);
            Assert.That(File.ReadAllText(stale), Is.EqualTo("source bytes"),
                "the stale file was replaced by a link to the source");
            Assert.That(new HardLinkService().LinkCount(target), Is.EqualTo(2));
        });
    }

    [Test]
    public void ExistingFileWithoutReplacementPermission_Survives()
    {
        var target = Path.Combine(_dir, "source.mp3");
        var occupied = Path.Combine(_dir, "occupied.mp3");
        File.WriteAllText(target, "source bytes");
        File.WriteAllText(occupied, "someone else's bytes");

        var ok = _linker.TryHardLink(occupied, target, out var failure);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(failure, Does.Contain("already exists"));
            Assert.That(File.ReadAllText(occupied), Is.EqualTo("someone else's bytes"),
                "a path another row owns must not be replaced");
        });
    }

    [Test]
    public void Failure_NamesTheOsReason()
    {
        var link = Path.Combine(_dir, "link.mp3");
        var missing = Path.Combine(_dir, "missing.mp3");

        var ok = _linker.TryHardLink(link, missing, out var failure);

        Assert.Multiple(() =>
        {
            Assert.That(ok, Is.False);
            Assert.That(failure, Is.Not.Null.And.Not.Empty,
                "the log line needs the real OS failure");
            Assert.That(File.Exists(link), Is.False);
        });
    }
}
