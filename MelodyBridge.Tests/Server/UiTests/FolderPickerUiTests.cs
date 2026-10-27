using Bunit;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using MelodyBridge.Server.Components.Shared;
using MelodyBridge.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The folder picker against real folders: shortcuts, navigation into a
/// child and the value handed back to the path field.
/// </summary>
[TestFixture]
[Category("UI")]
public class FolderPickerUiTests
{
    private Bunit.TestContext _ctx = null!;
    private string _root = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _root = Path.Combine(Path.GetTempPath(), $"mb-picker-ui-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, "music"));
        Directory.CreateDirectory(Path.Combine(_root, "music", "2026"));
        Directory.CreateDirectory(Path.Combine(_root, "state"));
        var factory = new TestSqliteFactory(Path.Combine(_root, "state", "state.db"));
        using (var db = factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
        }
        _ctx.Services.AddSingleton(new FolderBrowser(factory));
    }

    [TearDown]
    public void TearDown()
    {
        _ctx.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public void Picker_NavigatesAndReturnsTheFolder()
    {
        string? selected = null;
        var cut = _ctx.Render<FolderPickerDialog>(p => p
            .Add(dialog => dialog.Show, true)
            .Add(dialog => dialog.InitialPath, _root)
            .Add(dialog => dialog.OnSelected, (string path) => selected = path));

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("music")), TimeSpan.FromSeconds(5));

        cut.FindAll("button.folder-row").Single(button => button.TextContent.Contains("music")).Click();
        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Find(".folder-path-bar input").GetAttribute("value"),
                Does.Contain(Path.Combine("music")));
            Assert.That(cut.Markup, Does.Contain("2026"));
        }, TimeSpan.FromSeconds(5));

        cut.FindAll("button").Single(button => button.TextContent.Contains("Use this folder")).Click();

        Assert.That(selected, Is.EqualTo(Path.Combine(_root, "music")));
    }

    [Test]
    public void Picker_ShortcutRail_IsRendered()
    {
        var cut = _ctx.Render<FolderPickerDialog>(p => p
            .Add(dialog => dialog.Show, true)
            .Add(dialog => dialog.InitialPath, _root));

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Shortcuts"));
            Assert.That(cut.Markup, Does.Contain("Home"));
            Assert.That(cut.Markup, Does.Contain("Root"));
        }, TimeSpan.FromSeconds(5));
    }
}
