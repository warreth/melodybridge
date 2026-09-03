using TestContext = Bunit.TestContext;
using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Scanning;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

[TestFixture]
[Category("UI")]
public class LibraryPageTests
{
    private TestContext _ctx = null!;
    private Mock<ILibraryScanner> _scanner = null!;
    private InMemFactory _dbFactory = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _scanner = new Mock<ILibraryScanner>();

        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"LibraryTest_{Guid.NewGuid()}")
            .Options;
        _dbFactory = new InMemFactory(options);

        using (var db = _dbFactory.CreateDbContext())
        {
            db.ScanLocations.Add(new ScanLocationEntity { Path = "/music/test", ScanIntervalHours = 24 });
            db.SaveChanges();
        }

        _scanner.Setup(s => s.ScanAsync(It.IsAny<IEnumerable<ScanLocation>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _ctx.Services.AddSingleton<ILibraryScanner>(_scanner.Object);
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(_dbFactory);
        _ctx.Services.AddDownloadPages(_dbFactory);
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    [Test]
    public void Library_Renders_Title()
    {
        var cut = _ctx.Render<Library>();
        Assert.That(cut.Markup, Does.Contain("Library"));
    }

    [Test]
    public void Library_ShowsLocation()
    {
        var cut = _ctx.Render<Library>();
        Assert.That(cut.Markup, Does.Contain("/music/test"));
    }

    [Test]
    public void Library_HasAddLocationButton()
    {
        var cut = _ctx.Render<Library>();
        var buttons = cut.FindAll("button");
        Assert.That(buttons.Any(b => b.TextContent.Trim().Contains("Add location")), Is.True);
    }

    [Test]
    public async Task RunScan_Click_ScansRealFolder_AndRegistersTaggedFile()
    {
        // Real scanner, real file on disk, real DB behind the page: the
        // click must leave a track row and a success line, not a promise.
        var dir = Path.Combine(Path.GetTempPath(), $"mb-libscan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "song.mp3");
            await System.IO.File.WriteAllBytesAsync(path, SilenceMp3());
            MelodyBridge.Infrastructure.Tagging.TaglibHelper.WriteMelodyId(path, "mel-ui-scan-1");
            MelodyBridge.Infrastructure.Tagging.TaglibHelper.WriteTags(path,
                title: "Ui Scan Song", artist: "Ui Scan Artist", album: "Ui Scan Album");

            using (var db = _dbFactory.CreateDbContext())
            {
                db.ScanLocations.Add(new ScanLocationEntity { Path = dir, ScanIntervalHours = 24 });
                db.SaveChanges();
            }

            // The page gets the production scanner wired to the same DB.
            var scannerDescriptor = _ctx.Services.FirstOrDefault(
                d => d.ServiceType == typeof(ILibraryScanner));
            if (scannerDescriptor is not null) _ctx.Services.Remove(scannerDescriptor);
            _ctx.Services.AddSingleton<ILibraryScanner>(
                new LibraryScanner(_dbFactory.CreateDbContext(),
                    NullLogger<LibraryScanner>.Instance));

            var cut = _ctx.Render<Library>();
            cut.FindAll("button").First(b => b.TextContent.Trim() == "Run scan").Click();

            cut.WaitForAssertion(() =>
                Assert.That(cut.Markup, Does.Contain("location(s) successfully."),
                    "the page reports the scan result"), TimeSpan.FromSeconds(5));

            using (var db = _dbFactory.CreateDbContext())
            {
                var track = db.Tracks.AsNoTracking().SingleOrDefault(t => t.MelodyId == "mel-ui-scan-1");
                Assert.That(track, Is.Not.Null, "the click really scanned: the tagged file became a track row");
                Assert.That(track!.Title, Is.EqualTo("Ui Scan Song"));
                Assert.That(track.CurrentPath, Is.EqualTo(path));
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public void Library_ShowsEmptyState_WhenNoLocations()
    {
        using var emptyCtx = new TestContext();
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"LibraryEmpty_{Guid.NewGuid()}")
            .Options;
        var emptyFactory = new InMemFactory(options);

        emptyCtx.Services.AddSingleton<ILibraryScanner>(_scanner.Object);
        emptyCtx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(emptyFactory);
        emptyCtx.Services.AddDownloadPages(emptyFactory);

        var cut = emptyCtx.Render<Library>();
        Assert.That(cut.Markup, Does.Contain("No scan locations"));
    }

    private class InMemFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InMemFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }

    /// <summary>The same minimal valid MP3 the scanner tests use: ID3
    /// header plus silent frames, so TagLib can read and write it.</summary>
    private static byte[] SilenceMp3()
    {
        var id3Header = new byte[]
        {
            0x49, 0x44, 0x33, 0x03, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        };
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        var bytes = new byte[id3Header.Length + frame.Length * 40];
        id3Header.CopyTo(bytes, 0);
        for (var i = 0; i < 40; i++)
            frame.CopyTo(bytes, id3Header.Length + frame.Length * i);
        return bytes;
    }
}
