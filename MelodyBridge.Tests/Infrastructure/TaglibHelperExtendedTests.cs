using MelodyBridge.Infrastructure.Tagging;

namespace MelodyBridge.Tests.Infrastructure;

[TestFixture]
public class TaglibHelperExtendedTests
{
    [Test]
    public void WriteMelodyId_NonExistentFile_DoesNotThrow()
    {
        var path = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid() + ".mp3");
        Assert.That(File.Exists(path), Is.False);

        Assert.DoesNotThrow(() => TaglibHelper.WriteMelodyId(path, "test-id"));
    }

    [Test]
    public void ReadMelodyId_NonExistentFile_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid() + ".flac");
        var result = TaglibHelper.ReadMelodyId(path);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void WriteMelodyId_EmptyPath_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => TaglibHelper.WriteMelodyId("", "test-id"));
    }

    [Test]
    public void ReadMelodyId_EmptyFile_ReturnsNull()
    {
        var tempFile = Path.GetTempFileName() + ".mp3";
        try
        {
            // Create an empty file
            File.WriteAllBytes(tempFile, Array.Empty<byte>());

            var result = TaglibHelper.ReadMelodyId(tempFile);
            Assert.That(result, Is.Null);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public void WriteMelodyId_InvalidFileContent_DoesNotThrow()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), "invalid_" + Guid.NewGuid() + ".mp3");
        try
        {
            // Write garbage data
            File.WriteAllBytes(tempFile, new byte[] { 0x00, 0x01, 0x02, 0x03 });
            Assert.DoesNotThrow(() => TaglibHelper.WriteMelodyId(tempFile, "test-id"));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
