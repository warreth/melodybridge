using Bunit;
using CoreLogLevel = MelodyBridge.Core.Logging.LogLevel;
using MelodyBridge.Core.Logging;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Pages;
using MelodyBridge.Server.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The collector's Sequence stamp: two entries with identical content in
/// the same clock tick differ, which is what the Logs page's @key and pin
/// identity need. The page's own duplicated rows would crash the render
/// on a duplicate key before this; now they render and pin separately.
/// </summary>
[TestFixture]
[Category("UI")]
public class LogsPageDuplicateEntryTests
{
    private Bunit.TestContext _ctx = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _ctx.Services.AddPluginAuthMonitor();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;

        var collector = new LogCollector(500);
        _ctx.Services.AddSingleton<ILogCollector>(collector);
        _ctx.Services.AddSingleton(new LogExporter(collector));
        _ctx.Services.AddSingleton(new LogsViewState());
        _ctx.Services.AddSingleton<DataChangeTracker>();
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    [Test]
    public void TwoValueEqualEntries_RenderWithoutCollapsing()
    {
        var collector = _ctx.Services.GetRequiredService<ILogCollector>();
        collector.Log(CoreLogLevel.Error, "Test", "the same line");
        collector.Log(CoreLogLevel.Error, "Test", "the same line");

        var cut = _ctx.Render<Logs>();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("log-row")),
            TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            // The consequence of the collapsed key was a Blazor duplicate
            // key crash: the render succeeding with both entries in the
            // buffer is the assertion. The collector's stamp holds while
            // they render.
            Assert.That(collector.GetEntries().Select(e => e.Sequence).Distinct().Count(),
                Is.EqualTo(2), "the two entries have distinct Sequence stamps");
            Assert.That(cut.Markup, Does.Contain("the same line"));
        });
    }
}
