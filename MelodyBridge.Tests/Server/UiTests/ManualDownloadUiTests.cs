using Bunit;
using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Tagging;
using MelodyBridge.Server.Components.Pages;
using MelodyBridge.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The manual download page against a real SQLite settings store and a
/// stub plugin that writes a real MP3: a search opens the shared picker,
/// the pick saves a tagged and named file, and a link downloads straight
/// from the form. The saved card reads the path the service returned.
/// </summary>
[TestFixture]
[Category("UI")]
public class ManualDownloadUiTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;
    private string _dir = null!;

    private sealed class StubPlugin : IDownloader
    {
        public string Id => "manual";
        public string Name => "Manual source";
        public PluginCapabilities Capabilities => PluginCapabilities.Any;
        public string HitTitle { get; init; } = "The Real Song";
        public int DownloadCalls { get; private set; }

        public Task<bool> IsAvailableAsync(CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<DownloaderSearchHit?> SearchAsync(
            TrackQuery query, CancellationToken ct = default)
            => Task.FromResult<DownloaderSearchHit?>(new DownloaderSearchHit(
                HitTitle, "The Artist", "https://manual.example/1",
                TimeSpan.FromSeconds(2)));

        public Task<DownloaderDownloadResult> DownloadAsync(
            string sourceUrl, string outputDirectory, string? melodyId,
            DownloadQuality? quality = null, CancellationToken ct = default)
        {
            DownloadCalls++;
            Directory.CreateDirectory(outputDirectory);
            var path = Path.Combine(outputDirectory, $"picked-{melodyId}.mp3");
            File.WriteAllBytes(path, TestAudio.MinimalMp3());
            return Task.FromResult(new DownloaderDownloadResult(true, path, null));
        }
    }

    private sealed class StubMetadataResolver(UrlTrackMetadata? metadata) : IUrlMetadataResolver
    {
        public Task<UrlTrackMetadata?> ResolveAsync(string url, CancellationToken ct = default)
            => Task.FromResult(metadata);
    }

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        _ctx.Services.AddPluginAuthMonitor();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-manual-ui-{Guid.NewGuid():N}.db");
        _dir = Path.Combine(Path.GetTempPath(), $"mb-manual-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    [TearDown]
    public void TearDown()
    {
        _ctx.Dispose();
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
        }
    }

    private IRenderedComponent<ManualDownload> RenderPage(
        IDownloader plugin, IUrlMetadataResolver? metadata = null)
    {
        var factory = new TestSqliteFactory(_dbPath);
        using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.DownloaderSettings.Add(new DownloaderSettingEntity
            {
                Key = "music_path",
                Value = _dir,
            });
            db.DownloaderSettings.Add(new DownloaderSettingEntity
            {
                Key = "default_quality",
                Value = "auto",
            });
            db.SaveChanges();
        }

        _ctx.Services.AddDownloadPages(factory, plugin);
        _ctx.Services.AddSingleton(metadata ?? new StubMetadataResolver(null));
        _ctx.Services.AddSingleton(sp => new ManualDownloadService(
            sp.GetRequiredService<IDownloadManager>(),
            sp.GetRequiredService<IDownloaderRegistry>(),
            sp.GetRequiredService<IUrlMetadataResolver>(),
            NullLogger<ManualDownloadService>.Instance));

        var cut = _ctx.Render<ManualDownload>();
        cut.WaitForAssertion(() => Assert.That(
            cut.FindAll("form"), Has.Count.EqualTo(2)), TimeSpan.FromSeconds(5));
        return cut;
    }

    [Test]
    public async Task Search_ThenPick_SavesATaggedFileAndShowsItsPath()
    {
        var plugin = new StubPlugin();
        var cut = RenderPage(plugin);

        cut.Find("input[placeholder='Artist - Title, or just the title']")
            .Change("The Artist - The Real Song");
        await cut.InvokeAsync(() => cut.FindAll("form")[0].Submit());

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.FindAll(".source-candidate"), Has.Count.EqualTo(1),
                "the search result is in the shared picker");
            Assert.That(cut.Markup, Does.Contain("Manual source"));
            Assert.That(cut.Markup, Does.Contain("nothing is downloaded until you choose one"),
                "a manual search did not come from an automatic match");
            Assert.That(cut.Markup, Does.Not.Contain("automatic match refused"));
        }, TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => cut.Find(".source-candidate .btn-modern.primary").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.FindAll(".source-picker-modal"), Is.Empty,
                "the picker closes when the pick lands");
            Assert.That(cut.Markup, Does.Contain("Saved"));
            Assert.That(cut.Markup, Does.Contain("The Artist - The Real Song.mp3"));
        }, TimeSpan.FromSeconds(10));

        var path = Path.Combine(_dir, "The Artist - The Real Song.mp3");
        Assert.Multiple(() =>
        {
            Assert.That(plugin.DownloadCalls, Is.EqualTo(1));
            Assert.That(File.Exists(path), Is.True);
            Assert.That(TaglibHelper.ReadMelodyId(path), Does.StartWith("manual-"));
        });

        using var tags = TagLib.File.Create(path);
        Assert.Multiple(() =>
        {
            Assert.That(tags.Tag.Title, Is.EqualTo("The Real Song"));
            Assert.That(tags.Tag.Performers, Does.Contain("The Artist"));
        });
    }

    [Test]
    public async Task DownloadLink_WithoutArtistOrTitle_UsesTheResolvedMetadata()
    {
        var plugin = new StubPlugin();
        var metadata = new StubMetadataResolver(new UrlTrackMetadata(
            "Link Artist", "Link Title", TimeSpan.FromSeconds(2)));
        var cut = RenderPage(plugin, metadata);

        cut.Find("input[placeholder='https://www.youtube.com/watch?v=...']")
            .Change("https://example.com/the-link");
        await cut.InvokeAsync(() => cut.FindAll("form")[1].Submit());

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Saved"));
            Assert.That(cut.Markup, Does.Contain("Link Artist - Link Title.mp3"));
        }, TimeSpan.FromSeconds(10));

        var path = Path.Combine(_dir, "Link Artist - Link Title.mp3");
        Assert.Multiple(() =>
        {
            Assert.That(plugin.DownloadCalls, Is.EqualTo(1));
            Assert.That(File.Exists(path), Is.True);
            Assert.That(TaglibHelper.ReadMelodyId(path), Does.StartWith("manual-"));
        });
    }

    [Test]
    public void LinkForm_RejectsSomethingThatIsNotALink()
    {
        var cut = RenderPage(new StubPlugin());

        cut.Find("input[placeholder='https://www.youtube.com/watch?v=...']")
            .Change("not a link");
        cut.FindAll("form")[1].Submit();

        cut.WaitForAssertion(() => Assert.That(
            cut.Markup, Does.Contain("Paste a full http or https link.")));
        Assert.That(cut.FindAll(".source-candidate"), Is.Empty);
    }
}
