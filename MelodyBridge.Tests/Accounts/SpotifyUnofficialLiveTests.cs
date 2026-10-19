using MelodyBridge.Infrastructure.Accounts;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Accounts;

/// <summary>
/// The unofficial connection against the real Spotify web API. Needs a
/// live sp_dc cookie: export MB_SPOTIFY_SP_DC (the value from a logged-in
/// browser) and run the Live suite. Without it the test skips with
/// instructions instead of pretending.
/// </summary>
[TestFixture]
[Category("Live")]
public class SpotifyUnofficialLiveTests
{
    private static string? Cookie => Environment.GetEnvironmentVariable("MB_SPOTIFY_SP_DC");

    private string _dbPath = null!;
    private AccountTokenStore _tokens = null!;

    [SetUp]
    public async Task SetUp()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-web-live-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddDbContextFactory<MelodyBridgeDbContext>(o => o.UseSqlite($"Data Source={_dbPath};Pooling=False"));
        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>();
        await using (var db = factory.CreateDbContext())
            await db.Database.EnsureCreatedAsync();
        _tokens = new AccountTokenStore(factory, NullLogger<AccountTokenStore>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
    }

    private SpotifyUnofficialAccountProvider NewProvider()
        => new(_tokens, NullLogger<SpotifyUnofficialAccountProvider>.Instance);

    [Test]
    public async Task LikedSongs_Playlists_AndOnePlaylist_ThroughTheRealWebSession()
    {
        if (string.IsNullOrWhiteSpace(Cookie))
        {
            Assert.Ignore(
                "No MB_SPOTIFY_SP_DC in the environment. Copy the sp_dc cookie value from a "
                + "logged-in open.spotify.com session and export it to run this test.");
        }

        var provider = NewProvider();
        await provider.SaveSettingAsync("cookie", Cookie!);
        await provider.SetEnabledAsync(true);
        Assert.That(await provider.IsConnectedAsync(), Is.True);

        // The session must mint a token and answer with the account's own data.
        TestContext.Out.WriteLine(await provider.TestAsync());

        var liked = await provider.GetLikedPlaylistAsync();
        Assert.That(liked.Tracks, Is.Not.Null);
        Assert.That(liked.Tracks!.Count, Is.GreaterThan(0), "a real account has liked songs");
        Assert.That(liked.Tracks.All(t => t.IsLiked), Is.True);
        Assert.That(liked.Tracks.All(t => t.SongID is { ID.Length: > 0 }), Is.True,
            "every track carries its Spotify id");
        Assert.That(liked.Tracks.All(t => !string.IsNullOrWhiteSpace(t.Title)), Is.True,
            "every track has a title");
        Assert.That(liked.Tracks.Count(t => t.Duration is not null),
            Is.GreaterThan(liked.Tracks.Count / 2), "durations come with the payload");
        Assert.That(liked.Tracks.Count(t => t.CoverUrl is not null),
            Is.GreaterThan(liked.Tracks.Count / 2), "covers come with the payload");

        var playlists = await provider.GetUserPlaylistsAsync();
        Assert.That(playlists.Count, Is.GreaterThan(0), "a real account has playlists");
        Assert.That(playlists.All(p => !string.IsNullOrEmpty(p.Id)), Is.True);

        // One playlist, fetched the way the import dialog does it.
        var first = playlists.First();
        var viaSession = await provider.TryGetPlaylistViaAccountAsync(first.Id);
        Assert.That(viaSession, Is.Not.Null, "the session serves its own playlists");
        Assert.That(viaSession!.Name, Is.Not.Empty);
        Assert.That(viaSession.Tracks, Is.Not.Null);
        Assert.That(viaSession.Tracks!.Count, Is.GreaterThan(0),
            $"playlist '{first.Name}' must arrive with its tracks");
        Assert.That(viaSession.Tracks.All(t => t.SongID is { ID.Length: > 0 }), Is.True);
    }

    [Test]
    public async Task APublicPlaylist_AlsoArrivesThroughTheSession()
    {
        if (string.IsNullOrWhiteSpace(Cookie))
        {
            Assert.Ignore("No MB_SPOTIFY_SP_DC in the environment; see the class comment.");
        }

        var provider = NewProvider();
        await provider.SaveSettingAsync("cookie", Cookie!);
        await provider.SetEnabledAsync(true);

        var playlist = await provider.TryGetPlaylistViaAccountAsync("37i9dQZF1DXcBWIGoYBM5M");

        Assert.That(playlist, Is.Not.Null, "Spotify's own Today's Top Hits playlist");
        Assert.That(playlist!.Tracks, Is.Not.Null);
        Assert.That(playlist.Tracks, Has.Count.GreaterThan(0));
    }

    [Test]
    public async Task AStaleCookie_IsRejectedWithAReadableMessage()
    {
        var provider = NewProvider();
        await provider.SaveSettingAsync("cookie", "AQDdefinitely-not-a-session-cookie");
        await provider.SetEnabledAsync(true);

        var exception = Assert.CatchAsync<InvalidOperationException>(
            () => provider.GetLikedPlaylistAsync());

        Assert.That(exception, Is.Not.Null,
            "a dead cookie must fail loudly, and the message must say what to do");
        TestContext.Out.WriteLine(exception!.Message);
        Assert.That(exception.Message, Does.Contain("cookie"),
            "the error names the cookie as the problem and stays readable");
    }
}
