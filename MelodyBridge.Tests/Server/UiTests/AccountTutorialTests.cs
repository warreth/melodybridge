using Bunit;
using MelodyBridge.Infrastructure.Accounts;
using MelodyBridge.Infrastructure.Data;
using MelodyBridge.Server.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The noob-friendly connect flow on the accounts tab: a numbered
/// 3-step tutorial per provider, the per-provider redirect URI shown
/// with a copy button, and the Spotify "no localhost" rule — the URI
/// shown and the one passed to BeginLoginAsync/CompleteLoginAsync
/// always uses 127.0.0.1 and a ?provider= marker when the app is
/// browsed via localhost, so the callback page knows which account to
/// finish.
/// </summary>
[TestFixture]
[Category("UI")]
public class AccountTutorialTests
{
    private Bunit.TestContext _ctx = null!;
    private Mock<SpotifyAccountProvider> _spotify = null!;
    private Mock<YouTubeAccountProvider> _youtube = null!;

    [SetUp]
    public void Setup()
    {
        _ctx = new Bunit.TestContext();
        var factory = TestHelpers.CreateInMemFactory();
        _ctx.Services.AddSingleton<IDbContextFactory<MelodyBridgeDbContext>>(factory);
        _ctx.Services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        _ctx.Services.AddDownloadPages(factory);
        var tokenStore = new AccountTokenStore(factory, NullLogger<AccountTokenStore>.Instance);

        _spotify = new Mock<SpotifyAccountProvider>(tokenStore,
            NullLogger<SpotifyAccountProvider>.Instance) { CallBase = true };
        _spotify.Setup(s => s.IsConnectedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _spotify.Setup(s => s.GetSettingAsync("client_id", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, CancellationToken _) => null);
        _ctx.Services.AddSingleton(_spotify.Object);

        _youtube = new Mock<YouTubeAccountProvider>(tokenStore,
            NullLogger<YouTubeAccountProvider>.Instance) { CallBase = true };
        _youtube.Setup(s => s.IsConnectedAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        _youtube.Setup(s => s.GetSettingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, CancellationToken _) => null);
        _ctx.Services.AddSingleton(_youtube.Object);

        var collector = new MelodyBridge.Server.Services.LogCollector();
        _ctx.Services.AddSingleton<MelodyBridge.Core.Logging.ILogCollector>(collector);
        _ctx.Services.AddSingleton(new MelodyBridge.Server.Services.LogExporter(collector));
        _ctx.Services.AddSingleton(new Mock<MelodyBridge.Core.IMediaServerDirectory>().Object);
        _ctx.JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [TearDown]
    public void TearDown() => _ctx.Dispose();

    [Test]
    public void AccountsTab_ShowsNumberedTutorialSteps()
    {
        var cut = _ctx.Render<Settings>();

        var ordered = cut.FindAll("ol");
        Assert.That(ordered.Count, Is.EqualTo(2),
            "Spotify and YouTube each get a numbered step list");

        var spotifySteps = ordered[0].QuerySelectorAll("li");
        Assert.That(spotifySteps.Count, Is.EqualTo(3), "three Spotify steps");
        Assert.That(spotifySteps[0].TextContent, Does.Contain("developer.spotify.com/dashboard"));
        Assert.That(spotifySteps[1].TextContent, Does.Contain("redirect URI"),
            "step 2 names the redirect URI to register");
        Assert.That(spotifySteps[2].TextContent, Does.Contain("Client ID"),
            "step 3 says to paste the Client ID and connect");

        var youtubeSteps = ordered[1].QuerySelectorAll("li");
        Assert.That(youtubeSteps.Count, Is.EqualTo(3), "three YouTube steps");
        Assert.That(youtubeSteps[0].TextContent, Does.Contain("console.cloud.google.com"));
        Assert.That(youtubeSteps[0].TextContent, Does.Contain("YouTube Data API"),
            "the YouTube step names the API to enable");
    }

    [Test]
    public void RedirectUri_ShownWithLoopbackIp_AndCopyButton()
    {
        var cut = _ctx.Render<Settings>();

        // bUnit's navigation base is http://localhost/ — the shown URI must
        // swap localhost for 127.0.0.1 (Spotify's April 2025 rule) and carry
        // each provider's own marker in the query.
        cut.WaitForAssertion(() =>
        {
            Assert.That(cut.Markup, Does.Contain("http://127.0.0.1"),
                "the shown redirect URI uses the loopback IP literal");
            Assert.That(cut.Markup, Does.Contain("http://127.0.0.1/auth/callback?provider=spotify"),
                "the Spotify card shows its own provider-suffixed URI");
            Assert.That(cut.Markup, Does.Contain("http://127.0.0.1/auth/callback?provider=youtube"),
                "the YouTube card shows its own provider-suffixed URI");
        }, TimeSpan.FromSeconds(3));
        Assert.That(cut.Markup, Does.Not.Contain("http://localhost"),
            "localhost never appears as the redirect host");

        var copyButtons = cut.FindAll("button").Where(b => b.TextContent.Trim() == "Copy").ToList();
        Assert.That(copyButtons.Count, Is.EqualTo(2),
            "both provider cards carry a copy button");
    }

    [Test]
    public void CopyButton_CallsClipboardInterop()
    {
        var cut = _ctx.Render<Settings>();

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Copy").Click();

        var invocation = _ctx.JSInterop.VerifyInvoke("navigator.clipboard.writeText");
        Assert.That((string)invocation.Arguments.Single(),
            Does.StartWith("http://127.0.0.1"),
            "the copied URI is the substituted loopback one");
        Assert.That((string)invocation.Arguments.Single(),
            Does.Contain("?provider=spotify"),
            "the copied URI carries the Spotify marker so the callback knows the account");
    }

    [Test]
    public void ConnectSpotify_PassesLoopbackRedirect_ToBeginLogin()
    {
        _spotify.Setup(s => s.GetSettingAsync("client_id", It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, CancellationToken _) => null);
        var cut = _ctx.Render<Settings>();

        var input = cut.Find("input[placeholder='Spotify app Client ID']");
        input.Change("test-client-id");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Connect Spotify").Click();

        _spotify.Verify(s => s.BeginLoginAsync(
            It.Is<string>(u => u.StartsWith("http://127.0.0.1/auth/callback?provider=spotify")),
            It.IsAny<CancellationToken>()), Times.Once,
            "the login starts with the loopback redirect carrying the Spotify marker");
        _spotify.Verify(s => s.BeginLoginAsync(
            It.Is<string>(u => !u.Contains("localhost")),
            It.IsAny<CancellationToken>()), Times.Once,
            "localhost is never sent to Spotify");
    }

    [Test]
    public async Task ConnectYouTube_PassesItsOwnProviderRedirect_ToBeginLogin()
    {
        _youtube.Setup(s => s.GetSettingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, CancellationToken _) => null);
        var cut = _ctx.Render<Settings>();

        cut.Find("input[placeholder='OAuth client ID']").Change("yt-client-id");
        cut.Find("input[placeholder='OAuth client secret']").Change("yt-secret");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Connect YouTube").Click();

        _youtube.Verify(s => s.BeginLoginAsync(
            It.Is<string>(u => u.StartsWith("http://127.0.0.1/auth/callback?provider=youtube")),
            It.IsAny<CancellationToken>()), Times.Once,
            "the YouTube login starts with its own provider-suffixed redirect");
    }

    [Test]
    public void AuthCallback_Exchange_UsesSameLoopbackRedirect()
    {
        // The callback derives its own redirect; the exchange URI must match
        // the one the login started with, provider marker included. Pin the
        // browser to the localhost callback URL a Spotify login lands on.
        var nav = new FixedUriNavigationManager(
            "http://localhost/",
            "http://localhost/auth/callback?provider=spotify&code=x&state=y");
        _ctx.Services.RemoveAll<NavigationManager>();
        _ctx.Services.AddSingleton<NavigationManager>(nav);

        _spotify.Setup(s => s.CompleteLoginAsync(
                It.IsAny<string>(),
                It.Is<string>(u => u.StartsWith("http://127.0.0.1")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("Spotify account connected");

        _ctx.Render<AuthCallback>();

        _spotify.Verify(s => s.CompleteLoginAsync(
            It.IsAny<string>(),
            It.Is<string>(u => u == "http://127.0.0.1/auth/callback?provider=spotify"),
            It.IsAny<CancellationToken>()), Times.Once,
            "the exchange URI is exactly what Settings registered: loopback host plus the provider query");
        _spotify.Verify(s => s.CompleteLoginAsync(
            It.IsAny<string>(),
            It.Is<string>(u => !u.Contains("localhost")),
            It.IsAny<CancellationToken>()), Times.Once,
            "localhost is never sent in the exchange URI");
    }

    [Test]
    public void AuthCallback_ProviderMarker_SelectsYouTubeNotSpotify()
    {
        var nav = new FixedUriNavigationManager(
            "http://localhost/",
            "http://localhost/auth/callback?provider=youtube&code=x&state=y");
        _ctx.Services.RemoveAll<NavigationManager>();
        _ctx.Services.AddSingleton<NavigationManager>(nav);

        _youtube.Setup(s => s.CompleteLoginAsync(
                It.IsAny<string>(),
                It.Is<string>(u => u == "http://127.0.0.1/auth/callback?provider=youtube"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("YouTube account connected");

        var cut = _ctx.Render<AuthCallback>();

        _youtube.Verify(s => s.CompleteLoginAsync(
            It.IsAny<string>(),
            It.Is<string>(u => u == "http://127.0.0.1/auth/callback?provider=youtube"),
            It.IsAny<CancellationToken>()), Times.Once,
            "the provider marker routes the exchange to YouTube");
        _spotify.Verify(s => s.CompleteLoginAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never,
            "Spotify's exchange is never touched by a YouTube callback");
        cut.WaitForAssertion(() =>
            Assert.That(cut.Markup, Does.Contain("YouTube account connected")),
            TimeSpan.FromSeconds(3));
    }
}
