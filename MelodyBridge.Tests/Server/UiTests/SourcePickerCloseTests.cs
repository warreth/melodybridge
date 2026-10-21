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
        Func<DownloadCandidate, Task<SourcePickResult>> download)
        => new(
            Key: Guid.NewGuid().ToString("N"),
            Title: "The Song",
            Artist: "The Artist",
            DurationMs: 120_000,
            TargetQuality: DownloadQuality.Any,
            TargetQualityLabel: "Any quality",
            SearchAsync: (_, _) => Task.FromResult(new SourceSearchOutcome(
                new[]
                {
                    new DownloadCandidate(
                        "pick", "Pick plugin", "The Song", "The Artist",
                        TimeSpan.FromSeconds(120), null, "https://pick/1",
                        MatchConfidence.High),
                },
                Array.Empty<SourceSearchProblem>())),
            ProbeAsync: (_, _) => Task.FromResult<CandidateAudioQuality?>(null),
            DownloadAsync: download);

    private IRenderedComponent<PickerHost> RenderHost(
        Func<DownloadCandidate, Task<SourcePickResult>> download)
    {
        var cut = _ctx.Render<PickerHost>(parameters => parameters
            .Add(host => host.Request, Request(download)));
        cut.WaitForAssertion(() => Assert.That(
            cut.FindAll(".source-candidate"), Has.Count.EqualTo(1),
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
}
