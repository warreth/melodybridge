using Bunit;
using MelodyBridge.Core.Logging;
using CoreLogLevel = MelodyBridge.Core.Logging.LogLevel;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Application.Services;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Components.Pages;
using MelodyBridge.Server.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The dashboard's "recent errors" panel renders collected log lines.
/// Those can carry request URLs with credentials (Navidrome's t= and s=,
/// Jellyfin's api_key=); every surface that shows log text redacts, and
/// the one an operator is most likely to screenshot joined late.
/// </summary>
[TestFixture]
[Category("UI")]
public class DashboardLogRedactionTests
{
    private Bunit.TestContext _ctx = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        _ctx.Services.AddPluginAuthMonitor();
        var dbFactory = new TestSqliteFactory(Path.Combine(
            Path.GetTempPath(), $"mb-home-redact-{Guid.NewGuid():N}.db"));
        using (var db = dbFactory.CreateDbContext())
            db.Database.EnsureCreated();
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var options = new DbContextOptionsBuilder<MelodyBridgeDbContext>()
            .UseSqlite($"Data Source=file:memdb{Guid.NewGuid():N}?mode=memory&cache=shared")
            .Options;
        using (var init = new MelodyBridgeDbContext(options))
            init.Database.EnsureCreated();
        var factory = new SingleContextFactory(options);
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);
        // The dashboard body (and the recent-errors panel with it) shows
        // only after the intro is dismissed; the panel is the subject. The
        // write happens on its own store, before anything is resolved.
        new SettingsStore(dbFactory).SetAsync("intro_dismissed", "true")
            .GetAwaiter().GetResult();
        _ctx.Services.AddSingleton(new SettingsStore(dbFactory));
        _ctx.Services.AddSingleton<ILogCollector>(new LogCollector(500));
        _ctx.Services.AddSingleton<MelodyBridge.Infrastructure.Services.DataChangeTracker>();
        _ctx.Services.AddSingleton<IDownloaderRegistry>(new EmptyDownloaderRegistry());
        var tokenStore = new MelodyBridge.Infrastructure.Accounts.AccountTokenStore(
            factory, NullLogger<MelodyBridge.Infrastructure.Accounts.AccountTokenStore>.Instance);
        _ctx.Services.AddSingleton(tokenStore);
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.SpotifyAccountProvider>.Instance));
        _ctx.Services.AddSingleton(new MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider(
            tokenStore, NullLogger<MelodyBridge.Infrastructure.Accounts.YouTubeAccountProvider>.Instance));
        _ctx.Services.AddSingleton<MelodyBridge.Infrastructure.MediaServers.IJellyfinSettings>(
            new MelodyBridge.Infrastructure.MediaServers.ConfigJellyfinSettings(
                new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));
        _ctx.Services.AddSingleton<MelodyBridge.Infrastructure.MediaServers.IPlexSettings>(
            new TestServices.FixedPlexSettings());
        _ctx.Services.AddSingleton<MelodyBridge.Infrastructure.MediaServers.INavidromeSettings>(
            new TestServices.FixedNavidromeSettings());
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    [Test]
    public void RecentErrors_AreRedactedOnTheDashboard()
    {
        var collector = _ctx.Services.GetRequiredService<ILogCollector>();
        collector.Log(CoreLogLevel.Error, "MelodyBridge.Infrastructure.MediaServers.SubsonicSync",
            "GET http://127.0.0.1:59999/rest/search3?u=admin&t=3da76178cb6ad1a55db546aebaa61944&s=09d7d34d705f&f=json failed");

        var cut = _ctx.Render<Home>();
        cut.WaitForAssertion(() => Assert.That(cut.Markup, Does.Contain("log-row err")),
            TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(cut.Markup, Does.Contain("u=admin"), "the user name stays readable");
            Assert.That(cut.Markup, Does.Not.Contain("3da76178cb"), "the token is masked");
            Assert.That(cut.Markup, Does.Not.Contain("09d7d34d"), "the salt is masked");
            Assert.That(cut.Markup, Does.Contain("t=***"), "the masked parameter shows its place");
        });
    }

    private sealed class SingleContextFactory(DbContextOptions<MelodyBridgeDbContext> options)
        : IDbContextFactory<MelodyBridgeDbContext>
    {
        public MelodyBridgeDbContext CreateDbContext() => new(options);

        public MelodyBridgeDbContext CreateDbContext(
            DbContextOptionsBuilder<MelodyBridgeDbContext> optionsBuilder)
            => throw new NotSupportedException();
    }
}
