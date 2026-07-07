using TestContext = Bunit.TestContext;
using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Server.Components.Pages;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

[TestFixture]
[Category("UI")]
public class AccountsPageTests
{
    private TestContext _ctx = null!;
    private Mock<IMusicSourceManager> _sourceMgr = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new TestContext();
        _sourceMgr = new Mock<IMusicSourceManager>();

        _sourceMgr.Setup(s => s.GetAllSourcesAsync())
            .ReturnsAsync(new List<MusicSource>
            {
                new() { Id = "s1", Name = "My Favorites", Platform = Platform.YouTubeMusic, SourceUrl = "https://youtube.com/playlist?list=abc", AutoSyncEnabled = true },
                new() { Id = "s2", Name = "Chill Vibes", Platform = Platform.Spotify, AutoSyncEnabled = false },
            });

        _ctx.Services.AddSingleton<IMusicSourceManager>(_sourceMgr.Object);
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    [Test]
    public void Accounts_Renders_Title()
    {
        var cut = _ctx.Render<Accounts>();
        Assert.That(cut.Markup, Does.Contain("Accounts & sources"));
    }

    [Test]
    public void Accounts_ShowsSourceList()
    {
        var cut = _ctx.Render<Accounts>();
        Assert.That(cut.Markup, Does.Contain("My Favorites"));
        Assert.That(cut.Markup, Does.Contain("Chill Vibes"));
    }

    [Test]
    public void Accounts_HasAddSourceButton()
    {
        var cut = _ctx.Render<Accounts>();
        var btn = cut.Find("button.btn-modern.primary");
        Assert.That(btn.TextContent.Trim(), Is.EqualTo("Add source"));
    }

    [Test]
    public void Accounts_ShowsAutoSyncBadge()
    {
        var cut = _ctx.Render<Accounts>();
        Assert.That(cut.Markup, Does.Contain("Auto-sync"));
        Assert.That(cut.Markup, Does.Contain("Manual"));
    }

    [Test]
    public void AddSourceForm_OpensOnClick()
    {
        var cut = _ctx.Render<Accounts>();
        Assert.That(cut.Markup, Does.Not.Contain("Add a music source"));

        var addBtn = cut.Find("button.btn-modern.primary");
        addBtn.Click();

        Assert.That(cut.Markup, Does.Contain("Add a music source"));
    }

    [Test]
    public void AddSource_SavesViaManager()
    {
        _sourceMgr.Setup(s => s.AddSourceAsync(It.IsAny<MusicSource>()))
            .ReturnsAsync((MusicSource src) => src);

        var cut = _ctx.Render<Accounts>();
        // Open the add form
        cut.Find("button.btn-modern.primary").Click();

        // Click the save button inside the form wizard (second .btn-modern.primary)
        cut.InvokeAsync(() =>
        {
            var btns = cut.FindAll("button.btn-modern.primary");
            btns[1].Click();
        });

        _sourceMgr.Verify(s => s.AddSourceAsync(It.IsAny<MusicSource>()), Times.Once);
    }

    [Test]
    public void DeleteSource_CallsRemoveOnManager()
    {
        var cut = _ctx.Render<Accounts>();
        var removeBtns = cut.FindAll("button.btn-modern.ghost");
        Assert.That(removeBtns.Count, Is.GreaterThanOrEqualTo(1));
        removeBtns[0].Click();

        _sourceMgr.Verify(s => s.RemoveSourceAsync("s2"), Times.Once);
    }

    [Test]
    public void Accounts_ShowsEmptyState_WhenNoSources()
    {
        _sourceMgr.Setup(s => s.GetAllSourcesAsync())
            .ReturnsAsync(new List<MusicSource>());

        var cut = _ctx.Render<Accounts>();
        Assert.That(cut.Markup, Does.Contain("No sources yet"));
    }
}
