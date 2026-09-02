using AngleSharp.Dom;
using Bunit;
using MelodyBridge.Server.Components.Shared;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// QualityPicker floor/ceiling behaviour: parsing the combined value into
/// the two selects, emitting every band shape the store accepts, and the
/// min/max auto-adjust rule. The store (PlaylistStore.ParseQuality) is the
/// contract: "mp3:320" is a cap, "mp3:192-" is a floor, "mp3:192-320" a band.
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

    [Test]
    public void Band_RendersAsFloorAndCeiling()
    {
        var cut = _ctx.Render<QualityPicker>(p => p.Add(p => p.Value, "mp3:192-320"));

        var selects = cut.FindAll("select");
        Assert.That(selects.Count, Is.EqualTo(3),
            "container, floor and ceiling are three dropdowns");
        Assert.That(SelectedValue(selects[0]), Is.EqualTo("mp3"));
        Assert.That(SelectedValue(selects[1]), Is.EqualTo("192"));
        Assert.That(SelectedValue(selects[2]), Is.EqualTo("320"));
    }

    [Test]
    public void MaxOnly_ParsesAsCeilingOnly()
    {
        var cut = _ctx.Render<QualityPicker>(p => p.Add(p => p.Value, "mp3:320"));

        var selects = cut.FindAll("select");
        Assert.That(SelectedValue(selects[1]), Is.EqualTo(""),
            "a single number is a cap, so the floor stays empty");
        Assert.That(SelectedValue(selects[2]), Is.EqualTo("320"));
    }

    [Test]
    public void MinOnly_ParsesAsFloorOnly()
    {
        var cut = _ctx.Render<QualityPicker>(p => p.Add(p => p.Value, "mp3:192-"));

        var selects = cut.FindAll("select");
        Assert.That(SelectedValue(selects[1]), Is.EqualTo("192"));
        Assert.That(SelectedValue(selects[2]), Is.EqualTo(""),
            "an open band has no ceiling");
    }

    [Test]
    public void ChangingFloor_EmitsCombinedBand()
    {
        string? emitted = null;
        var cut = _ctx.Render<QualityPicker>(p => p
            .Add(p => p.Value, "mp3:192-320")
            .Add(p => p.ValueChanged, v => emitted = v));

        cut.FindAll("select")[1].Change("256");
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

        cut.FindAll("select")[1].Change("");
        cut.FindAll("select")[2].Change("");
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
        Assert.That(SelectedValue(selects[1]), Is.EqualTo("128"));
        Assert.That(SelectedValue(selects[2]), Is.EqualTo("128"));
        selects[1].Change("320");

        selects = cut.FindAll("select");
        Assert.That(SelectedValue(selects[1]), Is.EqualTo("320"));
        Assert.That(SelectedValue(selects[2]), Is.EqualTo("320"),
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
        Assert.That(SelectedValue(selects[1]), Is.EqualTo("256"));
        selects[2].Change("128");

        selects = cut.FindAll("select");
        Assert.That(SelectedValue(selects[1]), Is.EqualTo("128"),
            "lowering the ceiling below the floor lowers the floor too");
        Assert.That(SelectedValue(selects[2]), Is.EqualTo("128"));
        Assert.That(emitted, Is.EqualTo("mp3:128-128"));
    }

    [Test]
    public void LosslessContainer_LocksBothSelects()
    {
        var cut = _ctx.Render<QualityPicker>(p => p.Add(p => p.Value, "flac"));

        var selects = cut.FindAll("select");
        Assert.That(selects[1].HasAttribute("disabled"), Is.True,
            "FLAC has no meaningful bitrate, the floor locks");
        Assert.That(selects[2].HasAttribute("disabled"), Is.True,
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
