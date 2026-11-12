using MelodyBridge.Infrastructure.Audio;

namespace MelodyBridge.Tests.Audio;

/// <summary>
/// ffprobe's exit code alone does not say whether the file or the tool
/// failed. A known parse error is the file's verdict; anything else (a
/// resource limit, a killed process, an I/O error) keeps the file and
/// marks it unverified.
/// </summary>
[TestFixture]
public class FileIntegrityClassificationTests
{
    [TestCase("Failed to read frame size: Could not seek to 1026.", true)]
    [TestCase("Invalid data found when processing input", true)]
    [TestCase("moov atom not found", true)]
    [TestCase("Could not find codec parameters", true)]
    [TestCase("Cannot allocate memory", false)]
    [TestCase("Resource temporarily unavailable", false)]
    [TestCase("Too many open files", false)]
    [TestCase("Killed", false)]
    [TestCase("Input/output error", false)]
    public void ParseSignatures_SeparateTheFileFromTheTool(string stderr, bool parseFailure)
        => Assert.That(FileIntegrity.LooksLikeParseFailure(stderr), Is.EqualTo(parseFailure));
}
