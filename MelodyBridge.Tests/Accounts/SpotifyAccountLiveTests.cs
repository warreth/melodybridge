using System.Runtime.CompilerServices;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Accounts;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Accounts;

/// <summary>
/// Live account-flow tests, honestly gated on a real Spotify login. These
/// exercise the full chain: stored PKCE tokens → Spotify API → the real
/// track mapping. They run only when the environment carries a real token
/// set from an actual login (see README auth section); otherwise they skip
/// with instructions instead of pretending.
///
/// To enable: log in once in the app (or run the device-free PKCE flow),
/// then copy the token JSON from the settings row
/// account:spotify:tokens into these three environment variables:
///   MB_SPOTIFY_ACCESS  - the current access token
///   MB_SPOTIFY_REFRESH - the refresh token
///   MB_SPOTIFY_EXPIRES - expiry UTC, ISO 8601
/// </summary>
[TestFixture]
[Category("Live")]
public class SpotifyAccountLiveTests
{
    private static bool HaveLiveCredentials =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MB_SPOTIFY_ACCESS"))
        && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MB_SPOTIFY_REFRESH"))
        && DateTime.TryParse(Environment.GetEnvironmentVariable("MB_SPOTIFY_EXPIRES"), out _);

    private static string NewDbPath([CallerMemberName] string test = "")
        => Path.Combine(Path.GetTempPath(), $"mb-account-{test}-{Guid.NewGuid():N}.db");

    private static async Task<AccountTokenStore> NewStoreAsync(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<MelodyBridge.Infrastructure.Data.MelodyBridgeDbContext>(
            o => o.UseSqlite($"Data Source={dbPath}"));
        var sp = services.BuildServiceProvider();
        var factory = sp.GetRequiredService<IDbContextFactory<MelodyBridge.Infrastructure.Data.MelodyBridgeDbContext>>();
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        return new AccountTokenStore(factory, NullLogger<AccountTokenStore>.Instance);
    }

    [Test]
    public async Task UserPlaylists_And_LikedSongs_ThroughRealSpotifyAccount()
    {
        if (!HaveLiveCredentials)
        {
            Assert.Ignore(
                "No MB_SPOTIFY_ACCESS/MB_SPOTIFY_REFRESH/MB_SPOTIFY_EXPIRES in the environment. " +
                "Log in once in the app, copy the token JSON from the settings row " +
                "account:spotify:tokens, and export the three variables to run this test.");
        }

        var dbPath = NewDbPath();
        try
        {
            var tokens = await NewStoreAsync(dbPath);
            await tokens.SaveTokensAsync("Spotify", new AccountTokens(
                Environment.GetEnvironmentVariable("MB_SPOTIFY_ACCESS")!,
                Environment.GetEnvironmentVariable("MB_SPOTIFY_REFRESH"),
                DateTime.Parse(Environment.GetEnvironmentVariable("MB_SPOTIFY_EXPIRES")!)));

            var provider = new SpotifyAccountProvider(tokens, NullLogger<SpotifyAccountProvider>.Instance);

            var playlists = await provider.GetUserPlaylistsAsync();
            Assert.That(playlists.Count, Is.GreaterThan(0), "a real account has playlists");
            Assert.That(playlists.All(p => !string.IsNullOrEmpty(p.Id)), Is.True);

            var liked = await provider.GetLikedPlaylistAsync();
            Assert.That(liked.Id, Is.EqualTo("spotify-liked"));
            Assert.That(liked.Tracks.Count, Is.GreaterThan(0), "a real account has liked songs");
            Assert.That(liked.Tracks.All(t => t.IsLiked), Is.True,
                "every liked track is flagged for Jellyfin favorites");
            Assert.That(liked.Tracks.All(t => t.SourcePlatform == Platform.Spotify), Is.True);

            // A private playlist (if the account has one) comes back whole.
            // The track assertions are the regression guard for Spotify's
            // playlist item rename: the account used to return the playlist
            // with every track missing while the count looked fine.
            var privateish = playlists.FirstOrDefault(p => p.TrackCount > 0)
                ?? playlists.First();
            var viaAccount = await provider.TryGetPlaylistViaAccountAsync(privateish.Id);
            Assert.That(viaAccount, Is.Not.Null, "the account path returns its own playlists");
            if (privateish.TrackCount > 0)
            {
                Assert.That(viaAccount!.Tracks, Is.Not.Null,
                    "the playlist carries its tracks");
                Assert.That(viaAccount.Tracks.Count, Is.EqualTo(privateish.TrackCount),
                    "every track of the playlist arrives through the account path");
            }
        }
        finally
        {
            File.Delete(dbPath);
        }
    }

    [Test]
    public async Task ExpiredAccessToken_RefreshesViaRealSpotify()
    {
        if (!HaveLiveCredentials)
        {
            Assert.Ignore(
                "No MB_SPOTIFY_REFRESH in the environment; see the test class comment " +
                "for how to export real login tokens.");
        }

        var dbPath = NewDbPath();
        try
        {
            var tokens = await NewStoreAsync(dbPath);
            // The refresh call needs the app's client id, exactly like the
            // real settings store carries it.
            var clientId = Environment.GetEnvironmentVariable("MB_SPOTIFY_CLIENTID");
            if (string.IsNullOrWhiteSpace(clientId))
            {
                Assert.Ignore(
                    "No MB_SPOTIFY_CLIENTID in the environment; it is the Client ID of " +
                    "the app the tokens came from (see the settings row account:spotify:client_id).");
            }
            await tokens.SaveSettingAsync("Spotify", "client_id", clientId);
            // Expired on purpose: the provider must use the refresh token.
            await tokens.SaveTokensAsync("Spotify", new AccountTokens(
                "surely-expired-now", Environment.GetEnvironmentVariable("MB_SPOTIFY_REFRESH"),
                DateTime.UtcNow.AddHours(-3)));

            var provider = new SpotifyAccountProvider(tokens, NullLogger<SpotifyAccountProvider>.Instance);
            var playlists = await provider.GetUserPlaylistsAsync();

            Assert.That(playlists.Count, Is.GreaterThan(0), "refresh yielded a working client");

            // The refreshed token must have been persisted for next time.
            var stored = await tokens.GetTokensAsync("Spotify");
            Assert.That(stored!.AccessToken, Is.Not.EqualTo("surely-expired-now"));
        }
        finally
        {
            File.Delete(dbPath);
        }
    }
}
