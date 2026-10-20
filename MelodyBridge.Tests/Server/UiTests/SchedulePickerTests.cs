using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Server.Components.Shared;
using TestContext = Bunit.TestContext;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The shared schedule editor keeps raw cron text while the user types an
/// incomplete expression. Live validation is opt-in so pages that save on
/// ValueChanged do not write on every valid keystroke.
/// </summary>
[TestFixture]
[Category("UI")]
public class SchedulePickerTests
{
    private TestContext _ctx = null!;
    private ScanSchedule _value;
    private bool _valid = true;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _value = ScanSchedule.FromCron("0 4 * * *");
        _valid = true;
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    private IRenderedComponent<SchedulePicker> RenderPicker(bool liveValidation)
        => _ctx.Render<SchedulePicker>(p => p
            .Add(x => x.Value, _value)
            .Add(x => x.ValueChanged, v => _value = v)
            .Add(x => x.ValidityChanged, v => _valid = v)
            .Add(x => x.LiveValidation, liveValidation));

    private void RerenderWithParent(IRenderedComponent<SchedulePicker> cut, bool liveValidation)
        => cut.Render(p => p
            .Add(x => x.Value, _value)
            .Add(x => x.ValueChanged, v => _value = v)
            .Add(x => x.ValidityChanged, v => _valid = v)
            .Add(x => x.LiveValidation, liveValidation));

    [Test]
    public void LiveValidation_KeepsPartialCronTextWhileTyping()
    {
        var cut = RenderPicker(liveValidation: true);
        cut.Find("select").Change("Cron");

        cut.Find("input").Input("not a cron");
        RerenderWithParent(cut, liveValidation: true);
        Assert.Multiple(() =>
        {
            Assert.That(cut.Find("input").GetAttribute("value"), Is.EqualTo("not a cron"),
                "invalid text stays in the field when the parent rerenders");
            Assert.That(_valid, Is.False, "invalid text is reported as invalid");
            Assert.That(_value.Cron, Is.EqualTo("0 4 * * *"),
                "the last valid schedule remains the emitted value");
        });

        cut.Find("input").Input("5");
        RerenderWithParent(cut, liveValidation: true);
        Assert.That(cut.Find("input").GetAttribute("value"), Is.EqualTo("5"),
            "the next keystroke continues from the partial text");

        cut.Find("input").Input("5 4 * * *");
        RerenderWithParent(cut, liveValidation: true);
        Assert.Multiple(() =>
        {
            Assert.That(cut.Find("input").GetAttribute("value"), Is.EqualTo("5 4 * * *"));
            Assert.That(_valid, Is.True);
            Assert.That(_value.Cron, Is.EqualTo("5 4 * * *"),
                "a complete expression is emitted");
        });
    }

    [Test]
    public void DefaultValidation_EmitsOnlyOnChange()
    {
        var cut = RenderPicker(liveValidation: false);
        cut.Find("select").Change("Cron");
        Assert.That(_value.Cron, Is.EqualTo("0 4 * * *"));

        Assert.Throws<MissingEventHandlerException>(
            () => cut.Find("input").Input("5 4 * * *"),
            "typing alone has no input handler, so it cannot autosave an intermediate expression");

        cut.Find("input").Change("5 4 * * *");
        Assert.That(_value.Cron, Is.EqualTo("5 4 * * *"),
            "blur or change commits the expression");
    }
}
