using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The folder picker's server side: real directories, a real SQLite file
/// for the shortcuts, and the state directory kept out of the list.
/// </summary>
[TestFixture]
public class FolderBrowserTests
{
    private string _root = null!;
    private string _state = null!;
    private TestSqliteFactory _dbFactory = null!;
    private FolderBrowser _browser = null!;

    [SetUp]
    public async Task Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mb-folders-{Guid.NewGuid():N}");
        _state = Path.Combine(_root, "state");
        Directory.CreateDirectory(Path.Combine(_root, "music"));
        Directory.CreateDirectory(Path.Combine(_root, "playlists"));
        Directory.CreateDirectory(_state);

        var dbPath = Path.Combine(_state, "melodybridge.db");
        _dbFactory = new TestSqliteFactory(dbPath);
        await using (var db = _dbFactory.CreateDbContext())
        {
            await db.Database.EnsureCreatedAsync();
            db.DownloaderSettings.Add(new DownloaderSettingEntity
            {
                Key = "music_path",
                Value = Path.Combine(_root, "music"),
            });
            db.DownloaderSettings.Add(new DownloaderSettingEntity
            {
                Key = "playlist_path",
                Value = Path.Combine(_root, "playlists"),
            });
            await db.SaveChangesAsync();
        }

        _browser = new FolderBrowser(_dbFactory);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task Browse_ListsFoldersAndFileNames_AndNamedShortcuts()
    {
        File.WriteAllText(Path.Combine(_root, "notes.txt"), "hello");
        var listing = await _browser.BrowseAsync(_root);

        Assert.Multiple(() =>
        {
            Assert.That(listing.Error, Is.Null);
            Assert.That(listing.Path, Is.EqualTo(Path.GetFullPath(_root)));
            Assert.That(listing.Entries.Select(e => e.Name),
                Does.Contain("music").And.Contain("playlists"));
            Assert.That(listing.Files, Does.Contain("notes.txt"),
                "file names help the user recognize a folder");
            Assert.That(listing.Shortcuts.Select(s => s.Label),
                Does.Contain("Music").And.Contain("Playlists"));
        });
    }

    [Test]
    public async Task Browse_StateDirectory_IsBlocked()
    {
        var listing = await _browser.BrowseAsync(_state);

        Assert.That(listing.Error, Does.Contain("state folder"),
            "the database folder must not become a media path");
        Assert.That(listing.Entries, Is.Empty);
        Assert.That(listing.Files, Is.Empty);
    }

    [Test]
    public async Task Browse_ParentRoot_HidesTheStateDirectory()
    {
        var listing = await _browser.BrowseAsync(_root);

        Assert.That(listing.Entries.Select(e => e.Name), Does.Not.Contain("state"),
            "a blocked folder is not offered as a child either");
    }

    [Test]
    public async Task Browse_MissingFolder_OpensTheNearestExistingParent()
    {
        var listing = await _browser.BrowseAsync(Path.Combine(_root, "music", "new", "deeper"));

        Assert.That(listing.Path, Is.EqualTo(Path.Combine(_root, "music")));
    }

    [Test]
    public async Task Create_MakesTheFolder_AndSelectsIt()
    {
        var listing = await _browser.CreateAsync(Path.Combine(_root, "music"), "2026");

        Assert.That(listing.Error, Is.Null);
        Assert.That(Directory.Exists(Path.Combine(_root, "music", "2026")), Is.True);
        Assert.That(listing.Path, Is.EqualTo(Path.Combine(_root, "music", "2026")));
    }

    [Test]
    public async Task Create_RejectsSeparatorsAndTraversal()
    {
        var parent = Path.Combine(_root, "music");
        var slash = await _browser.CreateAsync(parent, "a/b");
        var traversal = await _browser.CreateAsync(parent, "..");

        Assert.That(slash.Error, Does.Contain("not valid"));
        Assert.That(traversal.Error, Does.Contain("not valid"));
        Assert.That(Directory.Exists(Path.Combine(_root, "a")), Is.False);
    }

    [Test]
    public async Task Browse_WithoutAPath_StartsWithTheFirstShortcut()
    {
        var listing = await _browser.BrowseAsync(null);

        Assert.That(listing.Path, Is.Not.Empty);
        Assert.That(listing.Error, Is.Null);
    }
}
