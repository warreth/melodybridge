using MelodyBridge.Infrastructure.Tagging;

namespace MelodyBridge.Tests.Infrastructure;

[TestFixture]
public class TaglibHelperTests
{
    [Test]
    public void WriteMelodyId_NonExistentFile_DoesNotThrow()
    {
        // Should gracefully handle missing files
        Assert.DoesNotThrow(() =>
            TaglibHelper.WriteMelodyId("/nonexistent/path/to/file.mp3", "test-melody-id"));
    }

    [Test]
    public void ReadMelodyId_NonExistentFile_ReturnsNull()
    {
        var result = TaglibHelper.ReadMelodyId("/nonexistent/path/to/file.mp3");
        Assert.That(result, Is.Null);
    }

    [Test]
    public void ReadMelodyId_EmptyFilePath_ReturnsNull()
    {
        var result = TaglibHelper.ReadMelodyId("");
        Assert.That(result, Is.Null);
    }

    [Test]
    public void ReadMelodyId_InvalidFile_ReturnsNull()
    {
        // Create a temp file with garbage content
        var tempFile = Path.GetTempFileName() + ".mp3";
        try
        {
            File.WriteAllBytes(tempFile, new byte[] { 0x00, 0x01, 0x02 });
            var result = TaglibHelper.ReadMelodyId(tempFile);
            Assert.That(result, Is.Null);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Test]
    public void WriteMelodyId_InvalidFile_DoesNotThrow()
    {
        var tempFile = Path.GetTempFileName() + ".mp3";
        try
        {
            File.WriteAllBytes(tempFile, new byte[] { 0x00 });
            Assert.DoesNotThrow(() =>
                TaglibHelper.WriteMelodyId(tempFile, "test-id"));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }
}
