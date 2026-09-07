using System.IO;
using MelodyBridge.Infrastructure.Files;

namespace MelodyBridge.Tests.Files;

/// <summary>
/// HardLinkService against the real filesystem: no mocks, no fakes. A
/// created link must be a second name for the same bytes (the link count
/// rises, writing through one name is visible through the other), a
/// cross-device attempt must throw the honest IOException, and deleting
/// one name must never destroy the shared file.
/// </summary>
[TestFixture]
public class HardLinkServiceTests
{
    private HardLinkService _svc = null!;
    private string _dir = null!;

    [SetUp]
    public void Setup()
    {
        _svc = new HardLinkService();
        _dir = Path.Combine(Path.GetTempPath(), $"mb-hl-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    [Test]
    public void IsSupported_True_OnCurrentPlatform()
        => Assert.That(_svc.IsSupported, Is.True, "CI runs on Windows/Linux/macOS, all supported");

    [Test]
    public void Create_SameDirectory_MakesSecondNameForSameBytes()
    {
        var target = Path.Combine(_dir, "a.mp3");
        var link = Path.Combine(_dir, "b.mp3");
        File.WriteAllText(target, "hello");

        _svc.Create(link, target);

        Assert.That(File.Exists(link), Is.True, "the link path must exist after Create");
        Assert.That(_svc.LinkCount(target), Is.EqualTo(2),
            "two names now point at the same file");
        // Same inode: what is written through one name is readable
        // through the other, immediately, with no copy involved.
        File.AppendAllText(link, " world");
        Assert.That(File.ReadAllText(target), Is.EqualTo("hello world"),
            "writes through the link must land in the shared file");
    }

    [Test]
    public void LinkCount_OneForPlainFile_GrowsWithEveryLink()
    {
        var target = Path.Combine(_dir, "plain.mp3");
        File.WriteAllText(target, "x");

        Assert.That(_svc.LinkCount(target), Is.EqualTo(1));

        _svc.Create(Path.Combine(_dir, "l1.mp3"), target);
        _svc.Create(Path.Combine(_dir, "l2.mp3"), target);
        Assert.That(_svc.LinkCount(target), Is.EqualTo(3),
            "every created link raises the count");
    }

    [Test]
    public void DeleteOneName_OtherNameStillWorks()
    {
        var target = Path.Combine(_dir, "keep.mp3");
        var link = Path.Combine(_dir, "alias.mp3");
        File.WriteAllText(target, "data");
        _svc.Create(link, target);

        File.Delete(target);

        Assert.That(File.Exists(link), Is.True,
            "removing one name must never destroy the shared bytes");
        Assert.That(_svc.LinkCount(link), Is.EqualTo(1),
            "the surviving name is now the only one");
        Assert.That(File.ReadAllText(link), Is.EqualTo("data"));
    }

    [Test]
    public void SameFileSystem_SameDirectory_True()
    {
        var a = Path.Combine(_dir, "a.mp3");
        var b = Path.Combine(_dir, "b.mp3");
        File.WriteAllText(a, "1");
        File.WriteAllText(b, "2");
        Assert.That(_svc.SameFileSystem(a, b), Is.True);
    }

    [Test]
    public void Create_MissingTarget_ThrowsIOException()
    {
        var missing = Path.Combine(_dir, "nope.mp3");
        var link = Path.Combine(_dir, "link.mp3");
        var ex = Assert.Throws<IOException>(() => _svc.Create(link, missing));
        Assert.That(ex!.Message, Does.Contain("errno").Or.Contains("failed"),
            "the raw OS failure must be visible in the message");
    }

    /// <summary>/dev/shm is a different device on Linux when it exists.</summary>
    private static string? CrossDeviceDir()
    {
        if (!OperatingSystem.IsLinux()) return null;
        return Directory.Exists("/dev/shm") ? "/dev/shm" : null;
    }

    [Test]
    public void Create_CrossDevice_ThrowsHonestError()
    {
        var other = CrossDeviceDir();
        if (other is null) { Assert.Ignore("needs a second filesystem: Linux with /dev/shm"); return; }

        var target = Path.Combine(_dir, "a.mp3");
        File.WriteAllText(target, "x");
        var linkPath = Path.Combine(other, $"mb-link-{Guid.NewGuid():N}.mp3");

        Assert.That(_svc.SameFileSystem(target, other), Is.False,
            "the guard must see the two paths on different devices");
        var ex = Assert.Throws<IOException>(() => _svc.Create(linkPath, target));
        Assert.That(ex!.Message, Does.Contain("cross-device"),
            "the error must name the real cause so callers can fall back");
        Assert.That(File.Exists(linkPath), Is.False,
            "a failed create must leave nothing behind");
    }

    [Test]
    public void SameFileSystem_CrossDevice_False()
    {
        var other = CrossDeviceDir();
        if (other is null) { Assert.Ignore("needs a second filesystem: Linux with /dev/shm"); return; }

        var target = Path.Combine(_dir, "a.mp3");
        File.WriteAllText(target, "x");
        Assert.That(_svc.SameFileSystem(target, other), Is.False);
    }

    [Test]
    public void LinkCount_MissingFile_Null()
    {
        Assert.That(_svc.LinkCount(Path.Combine(_dir, "ghost.mp3")), Is.Null);
    }
}
