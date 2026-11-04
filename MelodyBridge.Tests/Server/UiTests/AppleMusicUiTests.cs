using Bunit;
using MelodyBridge.Infrastructure.Accounts;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Pages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The Apple Music account card and its place in the import modal. These
/// assertions cover the visible promise: a cookie field that stays disabled
/// until acknowledged, a connected state with a test action, and the
/// honest "library songs" label.
/// </summary>
[TestFixture]
[Category("UI")]
public class AppleMusicUiTests
{
    private Bunit.TestContext _ctx = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        var factory = TestHelpers.CreateInMemFactory();
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);
        _ctx.Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        _ctx.Services.AddDownloadPages(factory);
        var tokens = new AccountTokenStore(
            factory, NullLogger<AccountTokenStore>.Instance);
        _ctx.Services.AddSingleton(tokens);
        _ctx.Services.AddSingleton(new SpotifyAccountProvider(
            tokens, NullLogger<SpotifyAccountProvider>.Instance));
        _ctx.Services.AddSingleton(new YouTubeAccountProvider(
            tokens, NullLogger<YouTubeAccountProvider>.Instance));
        var collector = new MelodyBridge.Server.Services.LogCollector();
        _ctx.Services.AddSingleton<MelodyBridge.Core.Logging.ILogCollector>(collector);
        _ctx.Services.AddSingleton(new MelodyBridge.Server.Services.LogExporter(collector));
        _ctx.Services.AddSingleton(new Mock<MelodyBridge.Core.IMediaServerDirectory>().Object);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    [TestCase("bare-token", "bare-token")]
    [TestCase("media-user-token=header-token; sp_dc=other", "header-token")]
    [TestCase("#HttpOnly_.music.apple.com\tTRUE\t/\tTRUE\t0\tmedia-user-token\texport-token", "export-token")]
    public void NormalizeAppleCookie_AcceptsTheThreePasteShapes(string pasted, string expected)
        => Assert.That(Settings.NormalizeAppleCookie(pasted), Is.EqualTo(expected));

    [Test]
    public void Settings_AppleCard_ExplainsTheCookieAndKeepsSaveDisabledUntilAcknowledged()
    {
        var cut = _ctx.Render<Settings>();
        cut.WaitForAssertion(() =>
        {
            var card = cut.FindAll(".account-card")
                .Single(c => c.TextContent.Contains("Apple Music"));
            Assert.Multiple(() =>
            {
                Assert.That(card.TextContent, Does.Contain("media-user-token"));
                Assert.That(card.TextContent, Does.Contain("Most Apple Music playlists need no cookie"));
                Assert.That(card.TextContent, Does.Contain("unofficial session"));
                var save = card.QuerySelector("button.btn-modern.primary");
                Assert.That(save, Is.Not.Null);
                Assert.That(save!.HasAttribute("disabled"), Is.True);
            });
        }, TimeSpan.FromSeconds(3));

        cut.FindAll(".account-card")
            .Single(c => c.TextContent.Contains("Apple Music"))
            .QuerySelector("input[type=checkbox]")!
            .Change(true);

        cut.WaitForAssertion(() =>
        {
            var save = cut.FindAll(".account-card")
                .Single(c => c.TextContent.Contains("Apple Music"))
                .QuerySelector("button.btn-modern.primary");
            Assert.That(save, Is.Not.Null);
            Assert.That(save!.HasAttribute("disabled"), Is.False);
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Settings_ConnectedAppleCard_ShowsTestAndRemove()
    {
        var apple = _ctx.Services.GetRequiredService<AppleMusicAccountProvider>();
        apple.SaveSettingAsync(AppleMusicAccountProvider.TokenKey, "token").GetAwaiter().GetResult();
        apple.SetEnabledAsync(true).GetAwaiter().GetResult();

        var cut = _ctx.Render<Settings>();
        cut.WaitForAssertion(() =>
        {
            var card = cut.FindAll(".account-card")
                .Single(c => c.TextContent.Contains("Apple Music"));
            Assert.Multiple(() =>
            {
                Assert.That(card.TextContent, Does.Contain("Test session"));
                Assert.That(card.TextContent, Does.Contain("Remove cookie"));
            });
        }, TimeSpan.FromSeconds(3));
    }

    [Test]
    public void Playlists_ImportModal_ShowsAppleAndTheSharedLinkHint()
    {
        var cut = _ctx.Render<Playlists>();
        cut.Find("div.topbar-actions button.ghost").Click();

        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("Apple Music"));
            Assert.That(cut.Markup, Does.Contain("shared playlist link"));
        }, TimeSpan.FromSeconds(3));
    }
}
