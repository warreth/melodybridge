using MelodyBridge.Infrastructure.Accounts;
using MelodyBridge.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Accounts;

/// <summary>
/// The pending OAuth login (PKCE verifier + state) must live in the
/// database, not in process memory: the app can restart between the
/// redirect to Spotify or Google and the callback, and a verifier that
/// died with the process forces the user to log in twice. Every read
/// here goes through a fresh store instance to prove nothing is cached
/// in RAM between calls.
/// </summary>
[TestFixture]
public class PendingLoginPersistenceTests
{
    private static Task<IDbContextFactory<MelodyBridgeDbContext>> NewDbFactory(string dbPath)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<MelodyBridgeDbContext>(o => o.UseSqlite($"Data Source={dbPath}"));
        var sp = services.BuildServiceProvider();
        return Task.FromResult(sp.GetRequiredService<IDbContextFactory<MelodyBridgeDbContext>>());
    }

    private static async Task<AccountTokenStore> NewStoreAsync(string dbPath)
    {
        var factory = await NewDbFactory(dbPath);
        await using var db = factory.CreateDbContext();
        await db.Database.EnsureCreatedAsync();
        return new AccountTokenStore(factory,
            NullLogger<AccountTokenStore>.Instance);
    }

    [Test]
    public async Task PendingLogin_SurvivesAFreshStoreInstance()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"mb-pending-{Guid.NewGuid():N}.db");
        try
        {
            var begin = await NewStoreAsync(dbPath);
            await begin.SavePendingLoginAsync("Spotify",
                new AccountTokenStore.PendingLogin("verifier-123", "state-abc", DateTime.UtcNow));

            // A brand new store: same situation as an app restart, the
            // process memory is gone and only the database remains.
            var complete = await NewStoreAsync(dbPath);
            var pending = await complete.GetPendingLoginAsync("Spotify");

            Assert.That(pending, Is.Not.Null,
                "the login started before the restart must still be finishable");
            Assert.That(pending!.Verifier, Is.EqualTo("verifier-123"));
            Assert.That(pending.State, Is.EqualTo("state-abc"));
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task PendingLogin_ClearsAfterExchange()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"mb-pending-{Guid.NewGuid():N}.db");
        try
        {
            var store = await NewStoreAsync(dbPath);
            await store.SavePendingLoginAsync("Spotify",
                new AccountTokenStore.PendingLogin("v", "s", DateTime.UtcNow));
            await store.ClearPendingLoginAsync("Spotify");

            var fresh = await NewStoreAsync(dbPath);
            Assert.That(await fresh.GetPendingLoginAsync("Spotify"), Is.Null,
                "a completed or failed login must not leave stale state behind");
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task PendingLogin_OlderThanAnHour_IsDropped()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"mb-pending-{Guid.NewGuid():N}.db");
        try
        {
            var store = await NewStoreAsync(dbPath);
            // The authorize code is long dead by then; the user should
            // simply start a fresh login instead of failing the exchange.
            await store.SavePendingLoginAsync("Spotify",
                new AccountTokenStore.PendingLogin("v", "s", DateTime.UtcNow.AddHours(-2)));

            var pending = await store.GetPendingLoginAsync("Spotify");
            Assert.That(pending, Is.Null,
                "a stale login must be discarded, not exchanged");
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* best effort */ }
        }
    }

    [Test]
    public async Task PendingLogin_IsPerProvider()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"mb-pending-{Guid.NewGuid():N}.db");
        try
        {
            var store = await NewStoreAsync(dbPath);
            await store.SavePendingLoginAsync("Spotify",
                new AccountTokenStore.PendingLogin("v-sp", "s-sp", DateTime.UtcNow));
            await store.SavePendingLoginAsync("YouTube",
                new AccountTokenStore.PendingLogin("", "s-yt", DateTime.UtcNow));

            var fresh = await NewStoreAsync(dbPath);
            Assert.That((await fresh.GetPendingLoginAsync("Spotify"))!.State, Is.EqualTo("s-sp"));
            Assert.That((await fresh.GetPendingLoginAsync("YouTube"))!.State, Is.EqualTo("s-yt"));
        }
        finally
        {
            try { File.Delete(dbPath); } catch { /* best effort */ }
        }
    }
}
