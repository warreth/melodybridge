using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Accounts;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Accounts;

/// <summary>
/// Verifies the independence contract: the public playlist fetcher keeps
/// working when no account is connected, when the account fetch fails, or
/// when account settings are garbage. These tests run the real provider
/// chain against the real store; the network boundary is a local stub at
/// the HTTP layer only.
/// </summary>
[TestFixture]
[Category("Unit")]
public class AccountIndependenceTests
{
    private SqliteConnection _connection = null!;
    private AccountTokenStore _tokens = null!;

    [SetUp]
    public void CreateFreshStore()
    {
        // Fresh database per test: account state never leaks between tests.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseSqlite(_connection)
            .Options;
        var factory = new InlineFactory(options);
        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
        _tokens = new AccountTokenStore(factory, NullLogger<AccountTokenStore>.Instance);
    }

    [Test]
    public async Task DisconnectedAccount_FallsBackToPublicFetcher()
    {
        // No Spotify account stored at all.
        var spotify = new SpotifyAccountProvider(_tokens, NullLogger<SpotifyAccountProvider>.Instance);

        var viaAccount = await spotify.TryGetPlaylistViaAccountAsync("37i9dQZF1DXcBWIGoYBM5M");
        Assert.That(viaAccount, Is.Null, "a disconnected account must yield nothing");
    }

    [Test]
    public async Task DeadAccessToken_FallsBackToPublicFetcher()
    {
        // Tokens exist but expired with no refresh token: any account call
        // must end in a null result, never an exception into the caller.
        await _tokens.SaveTokensAsync("Spotify",
            new AccountTokens("definitely-not-a-token", null, DateTime.UtcNow.AddHours(-2)));

        var spotify = new SpotifyAccountProvider(_tokens, NullLogger<SpotifyAccountProvider>.Instance);

        var viaAccount = await spotify.TryGetPlaylistViaAccountAsync("37i9dQZF1DXcBWIGoYBM5M");
        Assert.That(viaAccount, Is.Null, "a broken account must not break playlist adds");
    }

    [Test]
    public async Task Store_Clear_LeavesPublicSettingsAlone()
    {
        // Logout must only forget the account: public fetcher settings
        // (keys outside the account prefix) survive.
        await _tokens.SaveSettingAsync("Spotify", "client_id", "cid");
        await _tokens.SaveTokensAsync("Spotify",
            new AccountTokens("a", "r", DateTime.UtcNow));

        await _tokens.ClearAsync("Spotify");

        Assert.That(await _tokens.GetTokensAsync("Spotify"), Is.Null);
        Assert.That(await _tokens.GetSettingAsync("Spotify", "client_id"), Is.Null);
    }

    [Test]
    public async Task GetUserPlaylists_WithoutConnection_ThrowsClearError()
    {
        var youtube = new YouTubeAccountProvider(_tokens, NullLogger<YouTubeAccountProvider>.Instance);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => youtube.GetUserPlaylistsAsync());
        Assert.That(ex!.Message, Does.Contain("connected"));
    }

    [Test]
    public async Task BeginLoginYouTube_WithoutClientConfig_ThrowsClearError()
    {
        var youtube = new YouTubeAccountProvider(_tokens, NullLogger<YouTubeAccountProvider>.Instance);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => youtube.BeginLoginAsync("http://localhost:5085/auth/callback"));
        Assert.That(ex!.Message, Does.Contain("OAuth"));
    }

    [Test]
    public async Task BeginLoginYouTube_BuildsGoogleUrlWithReadOnlyScope()
    {
        await _tokens.SaveSettingAsync("YouTube", "client_id", "yt-client.apps.googleusercontent.com");
        await _tokens.SaveSettingAsync("YouTube", "client_secret", "yt-secret");

        var youtube = new YouTubeAccountProvider(_tokens, NullLogger<YouTubeAccountProvider>.Instance);
        var url = new Uri(await youtube.BeginLoginAsync("http://localhost:5085/auth/callback"));

        Assert.That(url.Host, Is.EqualTo("accounts.google.com"));
        Assert.That(url.AbsolutePath, Is.EqualTo("/o/oauth2/v2/auth"));

        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        Assert.That(query["client_id"], Is.EqualTo("yt-client.apps.googleusercontent.com"));
        Assert.That(query["redirect_uri"], Is.EqualTo("http://localhost:5085/auth/callback"));
        Assert.That(query["response_type"], Is.EqualTo("code"));
        Assert.That(query["scope"], Is.EqualTo("https://www.googleapis.com/auth/youtube.readonly"));
        Assert.That(query["state"], Is.Not.Null.And.Length.GreaterThan(10));
        // prompt=consent guarantees a refresh token is issued.
        Assert.That(query["prompt"], Is.EqualTo("consent"));
    }

    [TearDown]
    public void TearDown() => _connection.Dispose();

    private sealed class InlineFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InlineFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }
}
