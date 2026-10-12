using Bunit;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Accounts;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The OAuth landing page exchanges the login exactly once, on the first
/// interactive render. Under ServerPrerendered the initialization pass ran
/// on a throwaway instance and consumed the one-shot pending login, so the
/// circuit's pass showed a false error after a successful connect.
///
/// bUnit does not prerender, so the fixture pins the contract it can
/// observe: one exchange, no error, and a re-render never exchanges again.
/// </summary>
[TestFixture]
[Category("UI")]
public class AuthCallbackTests
{
    private Bunit.TestContext _ctx = null!;
    private Mock<SpotifyAccountProvider> _spotify = null!;
    private Mock<YouTubeAccountProvider> _youtube = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        var factory = TestHelpers.CreateInMemFactory();
        var tokenStore = new AccountTokenStore(factory, NullLogger<AccountTokenStore>.Instance);
        _spotify = new Mock<SpotifyAccountProvider>(tokenStore, NullLogger<SpotifyAccountProvider>.Instance)
        {
            CallBase = true,
        };
        _spotify
            .Setup(s => s.CompleteLoginAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("Connected as Test Listener");
        _youtube = new Mock<YouTubeAccountProvider>(tokenStore, NullLogger<YouTubeAccountProvider>.Instance)
        {
            CallBase = true,
        };

        _ctx.Services.AddSingleton(_spotify.Object);
        _ctx.Services.AddSingleton(_youtube.Object);
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    private void NavigateTo(string url)
        => _ctx.Services.GetRequiredService<NavigationManager>().NavigateTo(url);

    [Test]
    public void Callback_ExchangesOnce_AndARerenderDoesNotExchangeAgain()
    {
        NavigateTo("http://localhost/auth/callback?provider=spotify&code=abc&state=xyz");

        var cut = _ctx.Render<AuthCallback>();

        cut.WaitForAssertion(
            () => Assert.That(cut.Markup, Does.Contain("Connected as Test Listener")),
            TimeSpan.FromSeconds(3));

        cut.Render(); // a second interactive render must not retry the one-shot exchange

        _spotify.Verify(
            s => s.CompleteLoginAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.That(cut.Markup, Does.Not.Contain("login was in progress"),
            "a re-render must never show the one-shot error");
    }

    [Test]
    public void Callback_UnknownProvider_ReportsItWithoutExchanging()
    {
        NavigateTo("http://localhost/auth/callback?provider=myspace&code=abc");

        var cut = _ctx.Render<AuthCallback>();

        cut.WaitForAssertion(
            () => Assert.That(cut.Markup, Does.Contain("Unknown provider")),
            TimeSpan.FromSeconds(3));
        _spotify.Verify(
            s => s.CompleteLoginAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public void Callback_WithoutQuery_ReportsTheHonestMessage()
    {
        var cut = _ctx.Render<AuthCallback>();

        cut.WaitForAssertion(
            () => Assert.That(cut.Markup, Does.Contain("only reached from a login redirect")),
            TimeSpan.FromSeconds(3));
    }
}
