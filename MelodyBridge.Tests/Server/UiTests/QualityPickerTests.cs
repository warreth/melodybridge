using AngleSharp.Dom;
using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Server.Components.Shared;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// QualityPicker behaviour through the real component: the preset dropdown
/// offers the shared presets with their blurbs, raw format strings open the
/// advanced accordion, and the floor/ceiling selects keep the contract the
/// store persists ("mp3:320" is a cap, "mp3:192-" a floor, "mp3:192-320"
/// a band), including the min/max auto-adjust rule.
/// </summary>
[TestFixture]
[Category("UI")]
public class QualityPickerTests
{
    private Bunit.TestContext _ctx = null!;

    [SetUp]
    public void Setup() => _ctx = new Bunit.TestContext();

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    private static string SelectedValue(IElement select)
        => select.QuerySelector("option:checked")!.GetAttribute("value") ?? "";

    /// <summary>Opens the advanced accordion by moving the preset dropdown to it.</summary>
    private static void OpenAdvanced(IRenderedComponent<QualityPicker> cut)
    {
        var presetSelect = cut.FindAll("select")[0];
        presetSelect.Change("__advanced__");
        cut.Render();
    }

    [Test]
    public void PresetDropdown_OffersTheSharedPresets()
    {
        var cut = _ctx.Render<QualityPicker>(p => p.Add(p => p.Value, "auto"));

        var presetSelect = cut.FindAll("select")[0];
        // The four presets with their short descriptions, plus advanced.
        foreach (var preset in QualityPresets.All)
        {
            Assert.That(presetSelect.TextContent, Does.Contain(preset.Label),
                $"the dropdown offers {preset.Label}");
            Assert.That(presetSelect.TextContent, Does.Contain(preset.Blurb),
                $"{preset.Label} carries its short description");
        }
        Assert.That(presetSelect.TextContent, Does.Contain("Advanced filters"),
            "the escape hatch to raw filters is named in the dropdown");
    }

    [TestCase("preset:saver", "Space Saver")]
    [TestCase("preset:high", "High Quality")]
    [TestCase("preset:lossless", "Lossless")]
    [TestCase("auto", "Auto")]
    public void StoredPreset_SelectsItself(string stored, string label)
    {
        var cut = _ctx.Render<QualityPicker>(p => p.Add(p => p.Value, stored));

        var presetSelect = cut.FindAll("select")[0];
        Assert.That(presetSelect.TextContent, Does.Contain(label),
            $"{stored} shows as the {label} preset");
        Assert.That(cut.FindAll("select").Count, Is.EqualTo(1),
            "the advanced selects stay hidden for a preset value");
    }

    [TestCase("preset:saver", "160")]
    [TestCase("preset:high", "320")]
    [TestCase("preset:lossless", "lossy")]
    public void Preset_ShowsItsPlainLanguageBlurb(string stored, string mustContain)
    {
        var cut = _ctx.Render<QualityPicker>(p => p.Add(p => p.Value, stored));
        Assert.That(cut.Markup, Does.Contain(mustContain),
            "the preset describes what it accepts in plain words");
    }

    [Test]
    public void PickingAPreset_EmitsThePresetString()
    {
        string? emitted = null;
        var cut = _ctx.Render<QualityPicker>(p => p
            .Add(p => p.Value, "auto")
            .Add(p => p.ValueChanged, v => emitted = v));

        cut.FindAll("select")[0].Change("preset:saver");
        Assert.That(emitted, Is.EqualTo("preset:saver"),
            "the preset dropdown emits the stored preset string");
    }

    [Test]
    public void AdvancedAccordion_WarnsAboutNoTranscoding()
    {
        var cut = _ctx.Render<QualityPicker>(p => p.Add(p => p.Value, "mp3:192-320"));

        Assert.That(cut.Markup, Does.Contain("does not transcode"),
            "the advanced block warns that strict filters can fail");
        Assert.That(cut.Markup, Does.Contain("Advanced quality filters"),
            "the accordion names itself");
    }

    [Test]
    public void Band_RendersAsFloorAndCeiling()
    {
        var cut = _ctx.Render<QualityPicker>(p => p.Add(p => p.Value, "mp3:192-320"));

        var selects = cut.FindAll("select");
        Assert.That(selects.Count, Is.EqualTo(4),
            "preset dropdown plus container, floor and ceiling");
        Assert.That(SelectedValue(selects[1]), Is.EqualTo("mp3"));
        Assert.That(SelectedValue(selects[2]), Is.EqualTo("192"));
        Assert.That(SelectedValue(selects[3]), Is.EqualTo("320"));
    }

    [Test]
    public void MaxOnly_ParsesAsCeilingOnly()
    {
        var cut = _ctx.Render<QualityPicker>(p => p.Add(p => p.Value, "mp3:320"));

        var selects = cut.FindAll("select");
        Assert.That(SelectedValue(selects[2]), Is.EqualTo(""),
            "a single number is a cap, so the floor stays empty");
        Assert.That(SelectedValue(selects[3]), Is.EqualTo("320"));
    }

    [Test]
    public void MinOnly_ParsesAsFloorOnly()
    {
        var cut = _ctx.Render<QualityPicker>(p => p.Add(p => p.Value, "mp3:192-"));

        var selects = cut.FindAll("select");
        Assert.That(SelectedValue(selects[2]), Is.EqualTo("192"));
        Assert.That(SelectedValue(selects[3]), Is.EqualTo(""),
            "an open band has no ceiling");
    }

    [Test]
    public void ChangingFloor_EmitsCombinedBand()
    {
        string? emitted = null;
        var cut = _ctx.Render<QualityPicker>(p => p
            .Add(p => p.Value, "mp3:192-320")
            .Add(p => p.ValueChanged, v => emitted = v));

        cut.FindAll("select")[2].Change("256");
        Assert.That(emitted, Is.EqualTo("mp3:256-320"),
            "the floor change emits the combined band the store persists");
    }

    [Test]
    public void ClearingBoth_EmitsBareContainer()
    {
        string? emitted = null;
        var cut = _ctx.Render<QualityPicker>(p => p
            .Add(p => p.Value, "mp3:192-320")
            .Add(p => p.ValueChanged, v => emitted = v));

        cut.FindAll("select")[2].Change("");
        cut.FindAll("select")[3].Change("");
        Assert.That(emitted, Is.EqualTo("mp3"),
            "no floor and no ceiling emit just the container");
    }

    [Test]
    public void FloorAboveCeiling_RaisesCeiling()
    {
        string? emitted = null;
        var cut = _ctx.Render<QualityPicker>(p => p
            .Add(p => p.Value, "mp3:128-128")
            .Add(p => p.ValueChanged, v => emitted = v));

        // Pick 320 as floor while the ceiling is 128: the ceiling must follow.
        var selects = cut.FindAll("select");
        Assert.That(SelectedValue(selects[2]), Is.EqualTo("128"));
        Assert.That(SelectedValue(selects[3]), Is.EqualTo("128"));
        selects[2].Change("320");

        selects = cut.FindAll("select");
        Assert.That(SelectedValue(selects[2]), Is.EqualTo("320"));
        Assert.That(SelectedValue(selects[3]), Is.EqualTo("320"),
            "raising the floor above the ceiling raises the ceiling too");
        Assert.That(emitted, Is.EqualTo("mp3:320-320"));
    }

    [Test]
    public void CeilingBelowFloor_LowersFloor()
    {
        string? emitted = null;
        var cut = _ctx.Render<QualityPicker>(p => p
            .Add(p => p.Value, "mp3:256-")
            .Add(p => p.ValueChanged, v => emitted = v));

        // Pick 128 as ceiling while the floor is 256: the floor must follow.
        var selects = cut.FindAll("select");
        Assert.That(SelectedValue(selects[2]), Is.EqualTo("256"));
        selects[3].Change("128");

        selects = cut.FindAll("select");
        Assert.That(SelectedValue(selects[2]), Is.EqualTo("128"),
            "lowering the ceiling below the floor lowers the floor too");
        Assert.That(SelectedValue(selects[3]), Is.EqualTo("128"));
        Assert.That(emitted, Is.EqualTo("mp3:128-128"));
    }

    [Test]
    public void LosslessContainer_LocksBothSelects()
    {
        var cut = _ctx.Render<QualityPicker>(p => p.Add(p => p.Value, "flac"));

        var selects = cut.FindAll("select");
        Assert.That(selects[2].HasAttribute("disabled"), Is.True,
            "FLAC has no meaningful bitrate, the floor locks");
        Assert.That(selects[3].HasAttribute("disabled"), Is.True,
            "the ceiling locks too");
        Assert.That(cut.Markup, Does.Contain("Lossless/Not applicable"),
            "the locked selects say why");
    }

    [Test]
    public void Guidance_MentionsFloorRejectingJunk()
    {
        var cut = _ctx.Render<QualityPicker>(p => p.Add(p => p.Value, "mp3"));
        Assert.That(cut.Markup, Does.Contain("floor rejects junk rips"),
            "the MP3 guidance explains what the floor does");
    }

    [Test]
    public void Labels_NameFloorAndCeiling()
    {
        var cut = _ctx.Render<QualityPicker>(p => p.Add(p => p.Value, "mp3"));
        Assert.That(cut.Markup, Does.Contain("Bitrate floor"));
        Assert.That(cut.Markup, Does.Contain("Bitrate ceiling"));
    }
}
