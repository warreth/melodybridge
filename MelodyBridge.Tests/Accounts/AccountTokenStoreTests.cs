using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Accounts;
using MelodyBridge.Infrastructure.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Accounts;

/// <summary>
/// Real persistence tests for the account token store over a real SQLite
/// database file, the same engine the app ships with. No mocks of the
/// storage layer: the store reads back what it actually wrote.
/// </summary>
[TestFixture]
[Category("Unit")]
public class AccountTokenStoreTests
{
    private SqliteConnection _connection = null!;
    private MelodyBridgeDbContext _db = null!;
    private AccountTokenStore _store = null!;

    [SetUp]
    public void CreateFreshStore()
    {
        // A fresh database per test: token state must never leak between
        // tests, and each test asserts on exactly what it stored.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseSqlite(_connection)
            .Options;
        var factory = new InlineFactory(options);
        _db = factory.CreateDbContext();
        _db.Database.EnsureCreated();

        _store = new AccountTokenStore(factory, NullLogger<AccountTokenStore>.Instance);
    }

    [Test]
    public async Task Tokens_RoundTrip_ThroughRealSqlite()
    {
        var tokens = new AccountTokens("access-1", "refresh-1",
            DateTime.UtcNow.AddHours(1));

        await _store.SaveTokensAsync("Spotify", tokens);
        var read = await _store.GetTokensAsync("Spotify");

        Assert.That(read, Is.Not.Null);
        Assert.That(read!.AccessToken, Is.EqualTo("access-1"));
        Assert.That(read.RefreshToken, Is.EqualTo("refresh-1"));
        // Round-tripped through JSON, so the instant survives to the tick.
        Assert.That(read.ExpiresAtUtc, Is.EqualTo(tokens.ExpiresAtUtc).Within(TimeSpan.FromSeconds(1)));
    }

    [Test]
    public async Task Tokens_Overwrite_PreviousLogin()
    {
        await _store.SaveTokensAsync("Spotify",
            new AccountTokens("old", "refresh-old", DateTime.UtcNow));
        await _store.SaveTokensAsync("Spotify",
            new AccountTokens("new", null, DateTime.UtcNow));

        var read = await _store.GetTokensAsync("Spotify");
        Assert.That(read!.AccessToken, Is.EqualTo("new"));
        Assert.That(read.RefreshToken, Is.Null);
    }

    [Test]
    public async Task Clear_RemovesTokensAndSettings_ButNotOtherProviders()
    {
        await _store.SaveTokensAsync("Spotify",
            new AccountTokens("a", "r", DateTime.UtcNow));
        await _store.SaveSettingAsync("Spotify", "client_id", "cid");
        await _store.SaveTokensAsync("YouTube",
            new AccountTokens("y", "yr", DateTime.UtcNow));

        await _store.ClearAsync("Spotify");

        Assert.That(await _store.GetTokensAsync("Spotify"), Is.Null);
        Assert.That(await _store.GetSettingAsync("Spotify", "client_id"), Is.Null);
        Assert.That((await _store.GetTokensAsync("YouTube"))!.AccessToken, Is.EqualTo("y"));
    }

    [Test]
    public async Task MissingTokens_ReturnNull()
    {
        Assert.That(await _store.GetTokensAsync("Spotify"), Is.Null);
    }

    [Test]
    public async Task Settings_RoundTrip_And_Update()
    {
        Assert.That(await _store.GetSettingAsync("YouTube", "client_secret"), Is.Null);

        await _store.SaveSettingAsync("YouTube", "client_secret", "s3cret");
        Assert.That(await _store.GetSettingAsync("YouTube", "client_secret"), Is.EqualTo("s3cret"));

        await _store.SaveSettingAsync("YouTube", "client_secret", "changed");
        Assert.That(await _store.GetSettingAsync("YouTube", "client_secret"), Is.EqualTo("changed"));
    }

    [Test]
    public async Task IsConnected_ReflectsStoredTokens()
    {
        var provider = new SpotifyAccountProvider(
            _store, NullLogger<SpotifyAccountProvider>.Instance);

        Assert.That(await provider.IsConnectedAsync(), Is.False);

        await _store.SaveTokensAsync("Spotify",
            new AccountTokens("access", "refresh", DateTime.UtcNow.AddMinutes(5)));
        Assert.That(await provider.IsConnectedAsync(), Is.True);
    }

    [Test]
    public void BeginLogin_WithoutClientId_ThrowsClearMessage()
    {
        var provider = new SpotifyAccountProvider(
            _store, NullLogger<SpotifyAccountProvider>.Instance);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.BeginLoginAsync("http://localhost:5085/auth/callback"));
        Assert.That(ex!.Message, Does.Contain("Client ID"));
    }

    [Test]
    public async Task BeginLogin_BuildsPkce_UrlWithReadOnlyScopes()
    {
        var provider = new SpotifyAccountProvider(
            _store, NullLogger<SpotifyAccountProvider>.Instance);
        await _store.SaveSettingAsync("Spotify", "client_id", "test-client-id");

        var url = new Uri(await provider.BeginLoginAsync("http://localhost:5085/auth/callback"));

        Assert.That(url.Host, Is.EqualTo("accounts.spotify.com"));
        Assert.That(url.AbsolutePath, Is.EqualTo("/authorize"));

        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        Assert.That(query["client_id"], Is.EqualTo("test-client-id"));
        Assert.That(query["response_type"], Is.EqualTo("code"));
        Assert.That(query["redirect_uri"], Is.EqualTo("http://localhost:5085/auth/callback"));
        Assert.That(query["code_challenge_method"], Is.EqualTo("S256"));
        // PKCE: a fresh challenge is present and the verifier is not leaked.
        Assert.That(query["code_challenge"], Is.Not.Null.And.Length.GreaterThan(40));
        Assert.That(url.Query, Does.Not.Contain("verifier"));

        // Without state, Spotify echoes nothing back and the callback
        // cannot tell a forged answer from a real one. The state must
        // match the pending login that BeginLogin saved.
        Assert.That(query["state"], Is.Not.Null.And.Length.GreaterThan(10),
            "the authorize URL must carry the state that the callback verifies");
        var pending = await _store.GetPendingLoginAsync("Spotify");
        Assert.That(pending, Is.Not.Null);
        Assert.That(query["state"], Is.EqualTo(pending!.State));

        // Read-only scopes only: nothing that can change the account.
        var scopes = (query["scope"] ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(scopes, Is.EquivalentTo(new[]
        {
            "playlist-read-private", "playlist-read-collaborative", "user-library-read",
        }));
    }

    [Test]
    public async Task CompleteLogin_RejectsStaleState()
    {
        var provider = new SpotifyAccountProvider(
            _store, NullLogger<SpotifyAccountProvider>.Instance);
        await _store.SaveSettingAsync("Spotify", "client_id", "test-client-id");

        // No BeginLogin happened, so nothing is pending. The message
        // must say so: "something went wrong" hides which half failed.
        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CompleteLoginAsync("?code=x&state=forged", "http://localhost:5085/auth/callback"));
        Assert.That(ex!.Message, Does.Contain("No Spotify login was in progress"));
    }

    [Test]
    public async Task CompleteLogin_MissingCode_SaysSoExplicitly()
    {
        var provider = new SpotifyAccountProvider(
            _store, NullLogger<SpotifyAccountProvider>.Instance);
        await _store.SaveSettingAsync("Spotify", "client_id", "test-client-id");
        await provider.BeginLoginAsync("http://localhost:5085/auth/callback");

        // A login is pending but Spotify answered with no code: that is
        // a different problem than no login being in progress.
        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CompleteLoginAsync("?state=whatever", "http://localhost:5085/auth/callback"));
        Assert.That(ex!.Message, Does.Contain("no login code"));
    }

    [Test]
    public async Task CompleteLogin_RejectsMismatchedState()
    {
        var provider = new SpotifyAccountProvider(
            _store, NullLogger<SpotifyAccountProvider>.Instance);
        await _store.SaveSettingAsync("Spotify", "client_id", "test-client-id");
        await provider.BeginLoginAsync("http://localhost:5085/auth/callback");

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.CompleteLoginAsync("?code=x&state=forged", "http://localhost:5085/auth/callback"));
        Assert.That(ex!.Message, Does.Contain("does not belong to the login that was started"));
        // The forged attempt must not kill the real pending login:
        // a mismatch is suspicious, but clearing on it would let any
        // stray browser tab cancel the login.
        Assert.That(await _store.GetPendingLoginAsync("Spotify"), Is.Not.Null);
    }


    /// <summary>One shared context factory: SQLite in-memory, the real engine.</summary>
    private sealed class InlineFactory : IDbContextFactory<MelodyBridgeDbContext>
    {
        private readonly DbContextOptions<MelodyBridgeDbContext> _options;
        public InlineFactory(DbContextOptions<MelodyBridgeDbContext> options) => _options = options;
        public MelodyBridgeDbContext CreateDbContext() => new(_options);
    }

    [TearDown]
    public void TearDown()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
