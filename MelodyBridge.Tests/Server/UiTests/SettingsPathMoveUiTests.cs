using Bunit;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// Saving a changed Music path on the Settings page moves the library, not
/// just the setting: the files land under the new root, the playlist and
/// scan location follow, against a real SQLite database and real files.
/// </summary>
[TestFixture]
[Category("UI")]
public class SettingsPathMoveUiTests
{
    private Bunit.TestContext _ctx = null!;
    private string _root = null!;
    private string _dbPath = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _root = Path.Combine(Path.GetTempPath(), $"mb-pathmove-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "settings.db");
        var factory = new TestSqliteFactory(_dbPath);
        using (var db = factory.CreateDbContext()) db.Database.EnsureCreated();

        _ctx.Services.AddSingleton<IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        _ctx.Services.AddDownloadPages(factory);
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(
            factory, NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
        _ctx.Services.AddSingleton(tokenStore);
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider>.Instance));
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider>.Instance));
        var collector = new MelodyBridge.Server.Services.LogCollector();
        _ctx.Services.AddSingleton<MelodyBridge.Core.Logging.ILogCollector>(collector);
        _ctx.Services.AddSingleton(new MelodyBridge.Server.Services.LogExporter(collector));
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [TearDown]
    public void TearDown()
    {
        _ctx.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public async Task SavingANewMusicPath_MovesTheFilesAndTheRowsThatFollowIt()
    {
        var oldRoot = Path.Combine(_root, "music-old");
        var newRoot = Path.Combine(_root, "music-new");
        var playlistDir = Path.Combine(oldRoot, "My Mix");
        Directory.CreateDirectory(playlistDir);
        var file = Path.Combine(playlistDir, "song.mp3");
        await File.WriteAllTextAsync(file, "audio bytes");

        await using (var db = await new TestSqliteFactory(_dbPath).CreateDbContextAsync())
        {
            db.DownloaderSettings.Add(new DownloaderSettingEntity { Key = "music_path", Value = oldRoot });
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-1",
                Name = "My Mix",
                SourceUrl = "stub:pl",
                TargetDirectory = oldRoot,
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "m1", Title = "Song", Artist = "A", Position = 0,
                        DownloadStatus = "downloaded", CurrentPath = file,
                    },
                },
            });
            db.ScanLocations.Add(new ScanLocationEntity { Path = oldRoot });
            db.SaveChanges();
        }

        var cut = _ctx.Render<Settings>();
        cut.FindAll("button.tab-link").First(b => b.TextContent == "Default paths").Click();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("Music path")), TimeSpan.FromSeconds(3));

        cut.FindAll("label")
            .Single(l => l.TextContent.Contains("Music path"))
            .QuerySelector("input")!
            .Change(newRoot);
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Save settings").Click();

        var expected = Path.Combine(newRoot, "My Mix", "song.mp3");
        cut.WaitForAssertion(() =>
            Assert.That(File.Exists(expected), Is.True,
                "saving the new music path moved the library"), TimeSpan.FromSeconds(5));
        Assert.That(File.Exists(file), Is.False);

        await using var check = await new TestSqliteFactory(_dbPath).CreateDbContextAsync();
        Assert.That((await check.Tracks.AsNoTracking().SingleAsync()).CurrentPath, Is.EqualTo(expected));
        Assert.That((await check.Playlists.AsNoTracking().SingleAsync()).TargetDirectory, Is.EqualTo(newRoot));
        Assert.That((await check.ScanLocations.AsNoTracking().SingleAsync()).Path, Is.EqualTo(newRoot));
        Assert.That(await check.DownloaderSettings.AsNoTracking()
            .Where(s => s.Key == "music_path").Select(s => s.Value).FirstAsync(), Is.EqualTo(newRoot));
    }
}
