using MelodyBridge.Core;

namespace MelodyBridge.Tests.Core;

/// <summary>
/// The quality preset table: the picker label is the one name the UI may
/// use for a stored value, so notes and badges link back to it ("No
/// filter", not "auto").
/// </summary>
[TestFixture]
public class QualityPresetsTests
{
    [TestCase("auto", "No filter")]
    [TestCase("preset:saver", "Space Saver")]
    [TestCase("preset:high", "High Quality")]
    [TestCase("preset:lossless", "Lossless")]
    public void LabelFor_Preset_ReturnsThePickerName(string stored, string expected)
        => Assert.That(QualityPresets.LabelFor(stored), Is.EqualTo(expected));

    [Test]
    public void LabelFor_AdvancedValue_ReadsAsWords()
        => Assert.That(QualityPresets.LabelFor("mp3:192-320"), Is.EqualTo("mp3 · 192-320"));

    [Test]
    public void LabelFor_NullOrEmpty_IsTheOpenPreset()
    {
        Assert.That(QualityPresets.LabelFor(null), Is.EqualTo("No filter"));
        Assert.That(QualityPresets.LabelFor(""), Is.EqualTo("No filter"));
    }

    [Test]
    public void EveryPreset_Label_IsNonEmptyAndUnique()
    {
        var labels = QualityPresets.All.Select(p => p.Label).ToList();
        Assert.That(labels, Has.All.Not.Empty);
        Assert.That(labels.Distinct().Count(), Is.EqualTo(labels.Count));
    }
}
