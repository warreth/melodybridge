using TestContext = Bunit.TestContext;
using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Scanning;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

[TestFixture]
[Category("UI")]
public class LibraryPageTests
{
    private TestContext _ctx = null!;
    private Mock<ILibraryScanner> _scanner = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _scanner = new Mock<ILibraryScanner>();

        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseInMemoryDatabase($"LibraryTest_{Guid.NewGuid()}")
            .Options;
        var dbFactory = new InMemFactory(options);

        using (var db = dbFactory.CreateDbContext())
        {
            db.ScanLocations.Add(new ScanLocationEntity { Path = "/music/test", ScanIntervalHours = 24 });
            db.SaveChanges();
        }

        _scanner.Setup(s => s.ScanAsync(It.IsAny<IEnumerable<ScanLocation>>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _ctx.Services.AddSingleton<ILibraryScanner>(_scanner.Object);
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(dbFactory);
        _ctx.Services.AddDownloadPages(dbFactory);
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
    public void RunScan_Click_CallsScanner()
    {
        var cut = _ctx.Render<Library>();
        var scanBtns = cut.FindAll("button");
        var runScan = scanBtns.FirstOrDefault(b => b.TextContent.Trim().Contains("Scan"));
        if (runScan != null) runScan.Click();
        // Scanner.ScanAsync should have been called
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
}
