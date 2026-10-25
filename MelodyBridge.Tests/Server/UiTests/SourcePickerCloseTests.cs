using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Server.Components.Shared;
using MelodyBridge.Server.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The shared picker's closing contract, independent of the playlist page:
/// a landed pick and an already-running pick both close the dialog, while
/// a failed pick stays open with its reason. The host below stands in for
/// the page: it clears the request and re-renders exactly like a page does.
/// </summary>
[TestFixture]
[Category("UI")]
public class SourcePickerCloseTests
{
    private Bunit.TestContext _ctx = null!;

    private sealed class PickerHost : ComponentBase
    {
        [Parameter] public SourcePickerRequest? Request { get; set; }
        public int Closes { get; private set; }

        /// <summary>The page opening the dialog again with a new request.</summary>
        public void OpenRequest(SourcePickerRequest request)
        {
            Request = request;
            StateHasChanged();
        }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<SourcePicker>(0);
            builder.AddAttribute(1, nameof(SourcePicker.Request), Request);
            builder.AddAttribute(2, nameof(SourcePicker.OnClose),
                EventCallback.Factory.Create(this, () =>
                {
                    Closes++;
                    Request = null;
                    StateHasChanged();
                }));
            builder.CloseComponent();
        }
    }

    private sealed class NullMetadataResolver : IUrlMetadataResolver
    {
        public Task<UrlTrackMetadata?> ResolveAsync(string url, CancellationToken ct = default)
            => Task.FromResult<UrlTrackMetadata?>(null);
    }

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddPluginAuthMonitor();
        _ctx.Services.AddSingleton<IDownloaderRegistry>(new ListDownloaderRegistry());
        _ctx.Services.AddSingleton(new NotificationService());
        _ctx.Services.AddSingleton<IUrlMetadataResolver>(new NullMetadataResolver());
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    private static SourcePickerRequest Request(
        Func<DownloadCandidate, Task<SourcePickResult>> download,
        int candidateCount = 1)
        => new(
            Key: Guid.NewGuid().ToString("N"),
            Title: "The Song",
            Artist: "The Artist",
            DurationMs: 120_000,
            TargetQuality: DownloadQuality.Any,
            TargetQualityLabel: "Any quality",
            SearchAsync: (_, _) => Task.FromResult(new SourceSearchOutcome(
                Enumerable.Range(1, candidateCount).Select(i => new DownloadCandidate(
                    $"pick-{i}", $"Pick plugin {i}", $"The Song {i}", "The Artist",
                    TimeSpan.FromSeconds(120), null, $"https://pick/{i}",
                    MatchConfidence.High)).ToArray(),
                Array.Empty<SourceSearchProblem>())),
            ProbeAsync: (_, _) => Task.FromResult<CandidateAudioQuality?>(null),
            DownloadAsync: download);

    private IRenderedComponent<PickerHost> RenderHost(
        Func<DownloadCandidate, Task<SourcePickResult>> download,
        int candidateCount = 1)
    {
        var cut = _ctx.Render<PickerHost>(parameters => parameters
            .Add(host => host.Request, Request(download, candidateCount)));
        cut.WaitForAssertion(() => Assert.That(
            cut.FindAll(".source-candidate"), Has.Count.EqualTo(candidateCount),
            "the search result is in the dialog"), TimeSpan.FromSeconds(5));
        return cut;
    }

    [Test]
    public async Task DownloadedPick_ClosesTheDialog()
    {
        var cut = RenderHost(_ => Task.FromResult(SourcePickResult.Success("saved")));

        await cut.InvokeAsync(() => cut.Find(".source-candidate .btn-modern.primary").Click());

        Assert.Multiple(() =>
        {
            Assert.That(cut.Instance.Closes, Is.EqualTo(1), "the page was told to close");
            Assert.That(cut.FindAll(".source-picker-modal"), Is.Empty,
                "the dialog is gone from the tree");
        });
    }

    [Test]
    public async Task AlreadyRunningPick_ClosesTheDialog()
    {
        var cut = RenderHost(_ => Task.FromResult(SourcePickResult.AlreadyRunning));

        await cut.InvokeAsync(() => cut.Find(".source-candidate .btn-modern.primary").Click());

        Assert.Multiple(() =>
        {
            Assert.That(cut.Instance.Closes, Is.EqualTo(1),
                "a batch owns the track, so there is nothing left to pick");
            Assert.That(cut.FindAll(".source-picker-modal"), Is.Empty);
        });
    }

    [Test]
    public async Task FailedPick_StaysOpenWithTheReason()
    {
        var cut = RenderHost(_ => Task.FromResult(
            SourcePickResult.Failed("the link expired")));

        await cut.InvokeAsync(() => cut.Find(".source-candidate .btn-modern.primary").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Instance.Closes, Is.Zero, "a failure must not hide the picker");
            Assert.That(cut.FindAll(".source-picker-modal"), Has.Count.EqualTo(1));
            Assert.That(cut.Markup, Does.Contain("the link expired"));
        });
    }

    [Test]
    public async Task APickInFlight_MarksItsRow_AndReleasesOnlyAfterItLands()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        var cut = RenderHost(async _ =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return SourcePickResult.Failed("the link expired");
        });

        // The click stays pending while the download runs, like the real one.
        var pick = cut.InvokeAsync(() =>
            cut.Find(".source-candidate .btn-modern.primary").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.FindAll(".source-candidate.busy"), Has.Count.EqualTo(1),
                "the row that is downloading says so");
            Assert.That(cut.FindAll(".source-candidate .btn-modern.primary"), Is.Empty,
                "a pick in flight cannot be started a second time");
        }, TimeSpan.FromSeconds(5));

        gate.SetResult();
        await pick;

        cut.WaitForAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(cut.FindAll(".source-candidate.busy"), Is.Empty,
                    "the marker belongs to the pick that landed");
                Assert.That(cut.FindAll(".source-candidate .btn-modern.primary"), Has.Count.EqualTo(1),
                    "the row offers Download again");
                Assert.That(calls, Is.EqualTo(1),
                    "the same source was downloaded once");
            });
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task ASecondRowClickedWhileOneRuns_DoesNotStartASecondDownload()
    {
        var gate = new TaskCompletionSource();
        var calls = 0;
        var cut = RenderHost(async _ =>
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return SourcePickResult.Failed("the link expired");
        }, candidateCount: 2);

        // Both clicks arrive before the busy render can disable the other
        // row: the code, not only the markup, must refuse the second one.
        var first = cut.InvokeAsync(() => cut.FindAll(".source-candidate")[0]
            .QuerySelector(".btn-modern.primary")!.Click());
        var second = cut.InvokeAsync(() =>
        {
            var other = cut.FindAll(".source-candidate")[1]
                .QuerySelector(".btn-modern.primary");
            if (other is not null) other.Click();
        });
        await Task.WhenAll(first, second);

        gate.SetResult();
        cut.WaitForAssertion(() => Assert.That(
            cut.FindAll(".source-candidate.busy"), Is.Empty), TimeSpan.FromSeconds(5));

        Assert.That(calls, Is.EqualTo(1),
            "two clicks must not start two downloads that share the ticker and the close path");
    }

    [Test]
    public async Task APick_StillCloses_WhenTheParentRebuildsTheSameRequest()
    {
        // A parent that renders a fresh request instance for the same key
        // on every render must not stop the picker from closing on a
        // landed pick: the dialog is the key's session, not the instance.
        var gate = new TaskCompletionSource();
        var request = Request(async _ =>
        {
            await gate.Task;
            return SourcePickResult.Success("saved");
        });
        var cut = _ctx.Render<PickerHost>(parameters => parameters
            .Add(host => host.Request, request));
        cut.WaitForAssertion(() => Assert.That(
            cut.FindAll(".source-candidate"), Has.Count.EqualTo(1)));

        var pick = cut.InvokeAsync(() =>
            cut.Find(".source-candidate .btn-modern.primary").Click());
        cut.WaitForAssertion(() => Assert.That(
            cut.FindAll(".source-candidate.busy"), Has.Count.EqualTo(1)));

        await cut.InvokeAsync(() => cut.Instance.OpenRequest(request with { }));
        gate.SetResult();

        cut.WaitForAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(cut.Instance.Closes, Is.EqualTo(1),
                    "the landed pick closes the dialog it belongs to");
                Assert.That(cut.FindAll(".source-picker-modal"), Is.Empty);
            });
        }, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task APickFromAClosedDialog_DoesNotCloseTheNextOne()
    {
        var gate = new TaskCompletionSource();
        var cut = RenderHost(async _ =>
        {
            await gate.Task;
            return SourcePickResult.Success("saved");
        });

        var pick = cut.InvokeAsync(() =>
            cut.Find(".source-candidate .btn-modern.primary").Click());
        cut.WaitForAssertion(() => Assert.That(
            cut.FindAll(".source-candidate.busy"), Has.Count.EqualTo(1)));

        // Close dialog A while its download is still running.
        await cut.InvokeAsync(() => cut.FindAll("button")
            .Single(b => b.TextContent.Trim() == "Close").Click());
        Assert.That(cut.Instance.Closes, Is.EqualTo(1));

        // Open dialog B with its own request instance.
        var secondRequest = Request(_ => Task.FromResult(
            SourcePickResult.Failed("not now")));
        await cut.InvokeAsync(() => cut.Instance.OpenRequest(secondRequest));
        cut.WaitForAssertion(() => Assert.That(
            cut.FindAll(".source-candidate"), Has.Count.EqualTo(1),
            "the second dialog found its own results"));

        // The first pick lands late: it belongs to the closed dialog.
        gate.SetResult();
        await pick;

        cut.WaitForAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                Assert.That(cut.Instance.Closes, Is.EqualTo(1),
                    "the late pick must not close a dialog it does not own");
                Assert.That(cut.FindAll(".source-picker-modal"), Has.Count.EqualTo(1),
                    "the second dialog stays open");
            });
        }, TimeSpan.FromSeconds(5));
    }
}
