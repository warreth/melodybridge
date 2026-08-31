using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// PlaylistDetails page quality UI: format + bitrate range editing, and the
/// match-quality warning pill next to tracks.
/// </summary>
[TestFixture]
[Category("UI")]
public class PlaylistDetailsQualityTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-q-{Guid.NewGuid():N}.db");
        var factory = new Factory(_dbPath);
        using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-1",
                Name = "Quality UI test",
                SourceUrl = "https://example.com/pl",
                TargetDirectory = "/tmp",
                PreferredFormat = "mp3:192-320",
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "m1",
                        Title = "Fine track",
                        Artist = "Artist",
                        Position = 0,
                        DownloadStatus = "downloaded",
                    },
                    new()
                    {
                        MelodyId = "m2",
                        Title = "Doubtful track",
                        Artist = "Artist",
                        Position = 1,
                        DownloadStatus = "downloaded",
                        Warning = "bitrate looks inflated: spectral ceiling 12.2 kHz: matches a 128 kbps source",
                    },
                },
            });
            db.SaveChanges();
        }
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);
        var downloadManager = new Application.Services.DownloadManager(
            new EmptyRegistry(),
            NullLogger<Application.Services.DownloadManager>.Instance);
        _ctx.Services.AddSingleton<IDownloadManager>(downloadManager);
        _ctx.Services.AddSingleton(new PlaylistStore(
            factory,
            Array.Empty<ISourceProvider>(),
            downloadManager,
            NullLogger<PlaylistStore>.Instance));
    }

    [TearDown]
    public void TearDown()
    {
        _ctx.Dispose();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
        }
    }

    [Test]
    public void QualityEditor_ShowsFormatAndRange_FromStoredValue()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-1"));

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("File format")), TimeSpan.FromSeconds(3));
        Assert.That(cut.Markup, Does.Contain("Bitrate range"));
        Assert.That(cut.Find("select").OuterHtml, Does.Contain("selected"),
            "the stored format must be selected");

        var range = cut.Find("input[placeholder='e.g. 192-320']");
        Assert.That(range.GetAttribute("value"), Is.EqualTo("192-320"),
            "the stored bitrate range must land in the input");
    }

    [Test]
    public void TrackTable_ShowsWarningPill_ForFlaggedTrack()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(p => p.PlaylistId, "pl-1"));

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Doubtful track")), TimeSpan.FromSeconds(3));

        var warned = cut.FindAll("span.pill.warning");
        Assert.That(warned.Count, Is.EqualTo(1),
            "exactly the flagged track shows the check pill");
        Assert.That(warned[0].GetAttribute("title"), Does.Contain("inflated"),
            "hovering the pill explains the doubt");
    }



    private sealed class Factory(string dbPath) : IDbContextFactory<MelodyBridgeDbContext>
    {
        public MelodyBridgeDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;
            return new MelodyBridgeDbContext(options);
        }

        Task<MelodyBridgeDbContext> IDbContextFactory<MelodyBridgeDbContext>.CreateDbContextAsync(CancellationToken ct)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class EmptyRegistry : IDownloaderRegistry
    {
        public IReadOnlyList<IDownloader> GetAll() => Array.Empty<IDownloader>();
        public IDownloader? Get(string id) => null;
        public IReadOnlyList<IDownloader> GetEnabled() => Array.Empty<IDownloader>();
        public Task SetEnabledAsync(string id, bool enabled) => Task.CompletedTask;
        public bool IsEnabled(string id) => true;
        public Task<int> GetPriorityAsync(string id, CancellationToken ct = default) => Task.FromResult(0);
        public Task SetPriorityAsync(string id, int priority, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetOrderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct = default) => Task.CompletedTask;
    }
}
