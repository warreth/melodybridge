using TestContext = Bunit.TestContext;
using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// Layout regression tests for the wizard refactor: the unified error
/// region, the footer action group, the step container and the card
/// footer toolbar. These assert real rendered markup after the same
/// UI events the visual review exercised; they fail if the structure
/// the CSS depends on goes missing.
/// </summary>
[TestFixture]
[Category("UI")]
public class SyncJobsLayoutUiTests
{
    private TestContext _ctx = null!;
    private Mock<ISyncJobRunner> _jobRunner = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _jobRunner = new Mock<ISyncJobRunner>();

        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"SyncJobsLayout_{Guid.NewGuid()}")
            .Options;
        var dbFactory = new InMemFactory(options);

        using (var db = dbFactory.CreateDbContext())
        {
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "src-1",
                Name = "Layout Source",
                SourceUrl = "stub:src-1",
            });
            db.SyncJobs.Add(new SyncJobEntity
            {
                Id = "j1",
                Name = "Layout Job",
                SourceId = "src-1",
                SearchLocationPaths = "[]",
                Schedule = "Daily",
                OutputTarget = "M3uFile",
                M3uOutputPath = "/app/playlists/layout.m3u",
                LastRunAt = DateTime.UtcNow.AddDays(-1),
                LastRunStatus = "Completed",
            });
            db.SaveChanges();
        }

        _ctx.Services.AddSingleton<ISyncJobRunner>(_jobRunner.Object);
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(dbFactory);
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    private IRenderedComponent<SyncJobs> OpenWizard()
    {
        var cut = _ctx.Render<SyncJobs>();
        cut.FindAll("button").First(b => b.TextContent.Trim().Contains("New sync job")).Click();
        return cut;
    }

    [Test]
    public void Wizard_StepBody_HasStepContainer()
    {
        // every step body sits in one .wizard-step so the grid gap
        // governs spacing; the CSS rhythm depends on this container
        var cut = OpenWizard();
        Assert.That(cut.FindAll(".wizard-step").Count, Is.EqualTo(1));
    }

    [Test]
    public void Wizard_Validation_FailsWithoutSource_ErrorInUnifiedRegion()
    {
        var cut = OpenWizard();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Next").Click();
        var err = cut.FindAll(".wizard-error");
        Assert.That(err.Count, Is.EqualTo(1), "exactly one unified error region");
        Assert.That(err[0].TextContent, Does.Contain("Choose a folder or a playlist"));
        Assert.That(cut.FindAll(".hint-box.wizard-error").Count, Is.EqualTo(1),
            "error region must reuse the hint-box styling with the wizard-error modifier");
    }

    [Test]
    public void Wizard_Validation_M3uStep_RequiresPathBeforeAdvance()
    {
        var cut = OpenWizard();
        // pick the saved playlist source, then advance to step 3
        var sourceSelect = cut.FindAll("select")[1];
        cut.InvokeAsync(() => sourceSelect.Change("src-1"));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Next").Click();
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Next").Click();
        Assert.That(cut.Find(".wizard-header .eyebrow").TextContent, Does.Contain("Step 3"));
        // no path filled: Next must stay on step 3 and show the error
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Next").Click();
        Assert.That(cut.Find(".wizard-header .eyebrow").TextContent, Does.Contain("Step 3"));
        Assert.That(cut.Find(".wizard-error").TextContent, Does.Contain("M3U output path"));
    }

    [Test]
    public void Wizard_Footer_ActionsGroupedRightOfBack()
    {
        var cut = OpenWizard();
        var footer = cut.Find(".wizard-footer");
        // step 0 has no Back; the only action is Next inside .wizard-actions
        var actions = footer.QuerySelector(".wizard-actions");
        Assert.That(actions, Is.Not.Null, "primary action must sit in the .wizard-actions group");
        Assert.That(actions.TextContent, Does.Contain("Next"));
        Assert.That(footer.QuerySelector("span:empty"), Is.Null,
            "the old empty-span spacer must be gone");
    }

    [Test]
    public void JobCard_Actions_FormOneFooterToolbar()
    {
        var cut = _ctx.Render<SyncJobs>();
        var card = cut.FindAll(".panel-card").First(c => c.TextContent.Contains("Layout Job"));
        var footer = card.QuerySelector(".job-footer");
        Assert.That(footer, Is.Not.Null, "card actions must live in a .job-footer toolbar");
        var labels = footer.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).ToList();
        Assert.That(labels, Is.EqualTo(new[] { "Run now", "Log", "Edit", "Delete" }));
    }

    [Test]
    public async Task JobCard_RunNow_ShowsRunningState_ThenRecovers()
    {
        // gate the runner so the run is observably in flight when asserted
        var gate = new TaskCompletionSource<SyncJobRunLog>();
        _jobRunner.Setup(r => r.RunJobAsync(It.IsAny<SyncJob>(), It.IsAny<CancellationToken>()))
            .Returns(gate.Task);
        var cut = _ctx.Render<SyncJobs>();
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Layout Job")), TimeSpan.FromSeconds(3));

        var run = cut.FindAll("button").Single(b => b.TextContent.Trim() == "Run now");
        var click = cut.InvokeAsync(() => run.Click());

        // while gated: the label flips and the button carries the disabled
        // attribute in the rendered markup (fresh query each poll; bUnit
        // replaces DOM nodes on re-render)
        cut.WaitForAssertion(() =>
        {
            var inFlight = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Trim() == "Running...");
            Assert.That(inFlight, Is.Not.Null, "label flips to Running... while the run is in flight");
            Assert.That(inFlight!.GetAttribute("disabled"), Is.Not.Null,
                "Run now must disable itself while the job runs");
        }, TimeSpan.FromSeconds(3));

        gate.SetResult(new SyncJobRunLog(DateTime.UtcNow, SyncStatus.Completed, "stub", 0, 0));
        await click;
        cut.WaitForAssertion(() =>
        {
            var back = cut.FindAll("button").FirstOrDefault(b => b.TextContent.Trim() == "Run now");
            Assert.That(back, Is.Not.Null, "label returns to Run now after completion");
            Assert.That(back!.GetAttribute("disabled"), Is.Null,
                "Run now re-enables after the run completes");
        }, TimeSpan.FromSeconds(3));
    }

    private class InMemFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InMemFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }
}