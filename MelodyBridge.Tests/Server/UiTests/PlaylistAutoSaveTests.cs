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
/// The playlist settings form auto-saves: text edits debounce 500ms,
/// selects save at once, a status pill narrates Saving/Saved/Failed,
/// and a failed save rolls the fields back to the last persisted
/// values. All against a real SQLite store and real store calls.
/// </summary>
[TestFixture]
[Category("UI")]
public class PlaylistAutoSaveTests
{
    private Bunit.TestContext _ctx = null!;
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-auto-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        using (var db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Playlists.Add(new PlaylistEntity
            {
                Id = "pl-1",
                Name = "Auto save test",
                SourceUrl = "https://example.com/pl",
                TargetDirectory = "/tmp",
                PreferredFormat = "auto",
                Tracks = new List<TrackEntity>
                {
                    new()
                    {
                        MelodyId = "m1",
                        Title = "Song",
                        Artist = "Artist",
                        Position = 0,
                        DownloadStatus = "downloaded",
                    },
                },
            });
            db.SaveChanges();
        }
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(_factory);
        _ctx.Services.AddDownloadPages(_factory);
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(
            _factory, NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
        _ctx.Services.AddSingleton(tokenStore);
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider>.Instance));
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider>.Instance));
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

    private string DbName()
    {
        using var db = _factory.CreateDbContext();
        return db.Playlists.AsNoTracking().First(p => p.Id == "pl-1").Name;
    }

    private string DbSyncMode()
    {
        using var db = _factory.CreateDbContext();
        return db.Playlists.AsNoTracking().First(p => p.Id == "pl-1").SyncMode;
    }

    [Test]
    public async Task TextEdit_Debounces_ThenPersists()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(x => x.PlaylistId, "pl-1"));
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Auto save test")), TimeSpan.FromSeconds(3));

        cut.Find("input[value='Auto save test']").Input("Renamed playlist");

        // Within the debounce window nothing is persisted yet.
        await Task.Delay(150);
        Assert.That(DbName(), Is.EqualTo("Auto save test"),
            "the debounced save has not fired yet");

        // Poll with real delays: the debounce continuation is scheduled on
        // the renderer's sync context, which WaitForAssertion does not pump.
        for (var i = 0; i < 40 && DbName() != "Renamed playlist"; i++)
            await Task.Delay(100);
        Assert.That(DbName(), Is.EqualTo("Renamed playlist"),
            "the debounced save persisted the new name");
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("save-state saved")),
            TimeSpan.FromSeconds(3));
    }

    [Test]
    public void SyncModeSelect_SavesImmediately()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(x => x.PlaylistId, "pl-1"));
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Auto save test")), TimeSpan.FromSeconds(3));

        cut.FindAll("select").Last(s => s.TextContent.Contains("Additive"))
            .Change(PlaylistSyncMode.Mirror.ToString());

        cut.WaitForAssertion(() =>
            Assert.That(DbSyncMode(), Is.EqualTo(PlaylistSyncMode.Mirror.ToString())),
            TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task FailedSave_RollsBackAndShowsThePill()
    {
        // A real store whose first UpdateScheduleAsync throws once:
        // a transient backend error, exactly like a DB hiccup.
        var store = new OneShotFailingStore(_factory);
        var descriptor = _ctx.Services.FirstOrDefault(d => d.ServiceType == typeof(PlaylistStore));
        if (descriptor is not null) _ctx.Services.Remove(descriptor);
        _ctx.Services.AddSingleton<PlaylistStore>(store);

        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(x => x.PlaylistId, "pl-1"));
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Auto save test")), TimeSpan.FromSeconds(3));

        cut.Find("input[value='Auto save test']").Input("Doomed edit");

        // Yield-poll: the debounce continuation is scheduled on the
        // renderer's sync context, which WaitForAssertion does not pump.
        for (var i = 0; i < 40 && !cut.Markup.Contains("Failed to save"); i++)
            await Task.Delay(100);
        Assert.That(cut.Markup, Does.Contain("Failed to save"),
            "the pill says the save failed");
        Assert.That(DbName(), Is.EqualTo("Auto save test"),
            "the database never saw the doomed edit");

        // Rollback: the input shows the persisted name again.
        var input = cut.FindAll("input").First(i => i.GetAttribute("value") is { } v && v.Contains("Auto save"));
        Assert.That(input.GetAttribute("value"), Is.EqualTo("Auto save test"),
            "the field rolled back to the last persisted value");
    }

    [Test]
    public void SuccessfulSave_ShowsTheSavedPill()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(x => x.PlaylistId, "pl-1"));
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Auto save test")), TimeSpan.FromSeconds(3));

        cut.FindAll("select").Last(s => s.TextContent.Contains("Additive"))
            .Change(PlaylistSyncMode.Mirror.ToString());

        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("save-state saved"),
                "the pill reaches the Saved state"), TimeSpan.FromSeconds(3));
    }

    [Test]
    public async Task InitialLoad_TriggersNoSave()
    {
        var cut = _ctx.Render<PlaylistDetails>(p => p.Add(x => x.PlaylistId, "pl-1"));
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("Auto save test")), TimeSpan.FromSeconds(3));

        // Long past any debounce window: no spurious auto-save happened.
        await Task.Delay(700);
        using var db = _factory.CreateDbContext();
        var updated = db.Playlists.AsNoTracking().First(p => p.Id == "pl-1");
        Assert.That(updated.Name, Is.EqualTo("Auto save test"));
        Assert.That(cut.Markup, Does.Not.Contain("save-state"),
            "no save pill appears without an edit");
    }

    /// <summary>
    /// The real store with a single injected transient failure: the first
    /// UpdateScheduleAsync throws, later calls delegate. This simulates a
    /// backend hiccup without faking the logic under test.
    /// </summary>
    private sealed class OneShotFailingStore(TestSqliteFactory factory)
        : PlaylistStore(
            factory,
            Array.Empty<ISourceProvider>(),
            new MelodyBridge.Application.Services.DownloadManager(
                new EmptyRegistry(),
                NullLogger<MelodyBridge.Application.Services.DownloadManager>.Instance),
            NullLogger<PlaylistStore>.Instance)
    {
        private bool _failedOnce;

        public override async Task UpdateScheduleAsync(
            string playlistId, string? name, ScanSchedule schedule,
            string? targetDirectory = null, PlaylistSyncMode? syncMode = null,
            string? preferredFormat = null, CancellationToken ct = default)
        {
            if (!_failedOnce)
            {
                _failedOnce = true;
                throw new InvalidOperationException("simulated transient store failure");
            }
            await base.UpdateScheduleAsync(playlistId, name, schedule, targetDirectory, syncMode, preferredFormat, ct);
        }
    }
}
