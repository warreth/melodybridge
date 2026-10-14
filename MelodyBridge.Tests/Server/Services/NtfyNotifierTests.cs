using System.Net;
using MelodyBridge.Infrastructure.Services;
using MelodyBridge.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Server.Services;

/// <summary>
/// The ntfy notifier against a real SettingsStore and a recording HTTP
/// handler: the request shape (body, headers, bearer token) and every
/// gate (master switch, per-category switch, URL) are asserted on the
/// wire, not through a fake notifier.
/// </summary>
[TestFixture]
public class NtfyNotifierTests
{
    private string _dbPath = null!;
    private TestSqliteFactory _factory = null!;
    private SettingsStore _settings = null!;

    [SetUp]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"mb-ntfy-{Guid.NewGuid():N}.db");
        _factory = new TestSqliteFactory(_dbPath);
        using var db = _factory.CreateDbContext();
        db.Database.EnsureCreated();
        _settings = new SettingsStore(_factory);
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch { /* best effort */ }
        }
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(string Method, string Url, string Body, string? Title, string? Priority, string? Tags, string? Auth)> Sent { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            request.Headers.TryGetValues("Title", out var title);
            request.Headers.TryGetValues("Priority", out var priority);
            request.Headers.TryGetValues("Tags", out var tags);
            Sent.Add((request.Method.Method, request.RequestUri!.ToString(), body,
                title?.FirstOrDefault(), priority?.FirstOrDefault(), tags?.FirstOrDefault(),
                request.Headers.Authorization?.ToString()));
            return new HttpResponseMessage(Status);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private NtfyNotifier NewNotifier(RecordingHandler handler)
        => new(_settings, new StubFactory(handler), NullLogger<NtfyNotifier>.Instance);

    [Test]
    public async Task Disabled_SendsNothing()
    {
        await _settings.SetAsync("ntfy_url", "https://ntfy.example/music");
        var handler = new RecordingHandler();

        await NewNotifier(handler).NotifyAsync(NotificationCategories.Downloads, "hello", "info");

        Assert.That(handler.Sent, Is.Empty, "the master switch gates everything");
    }

    [Test]
    public async Task CategoryOff_SendsNothing()
    {
        await _settings.SetAsync("ntfy_enabled", "true");
        await _settings.SetAsync("ntfy_url", "https://ntfy.example/music");
        await _settings.SetAsync(NtfyNotifier.SettingKeyFor(NotificationCategories.Downloads), "false");
        var handler = new RecordingHandler();

        await NewNotifier(handler).NotifyAsync(NotificationCategories.Downloads, "hello", "info");

        Assert.That(handler.Sent, Is.Empty, "a disabled category is not pushed");
    }

    [Test]
    public async Task NoUrl_SendsNothing()
    {
        await _settings.SetAsync("ntfy_enabled", "true");
        var handler = new RecordingHandler();

        await NewNotifier(handler).NotifyAsync(NotificationCategories.Downloads, "hello", "info");

        Assert.That(handler.Sent, Is.Empty);
    }

    [Test]
    public async Task Enabled_SendsTheMessageWithHeaders()
    {
        await _settings.SetAsync("ntfy_enabled", "true");
        await _settings.SetAsync("ntfy_url", "https://ntfy.example/music");
        var handler = new RecordingHandler();

        await NewNotifier(handler).NotifyAsync(NotificationCategories.Downloads,
            "'Mix' finished: 3 of 3 done (2 failed).", "warn");

        Assert.That(handler.Sent, Has.Count.EqualTo(1));
        var sent = handler.Sent[0];
        Assert.Multiple(() =>
        {
            Assert.That(sent.Method, Is.EqualTo("POST"));
            Assert.That(sent.Url, Is.EqualTo("https://ntfy.example/music"));
            Assert.That(sent.Body, Is.EqualTo("'Mix' finished: 3 of 3 done (2 failed)."));
            Assert.That(sent.Title, Is.EqualTo("MelodyBridge"));
            Assert.That(sent.Priority, Is.EqualTo("high"), "warnings arrive with high priority");
            Assert.That(sent.Tags, Is.EqualTo("warning"));
            Assert.That(sent.Auth, Is.Null, "no token means no Authorization header");
        });
    }

    [Test]
    public async Task Token_IsSentAsBearer()
    {
        await _settings.SetAsync("ntfy_enabled", "true");
        await _settings.SetAsync("ntfy_url", "https://ntfy.example/music");
        await _settings.SetAsync("ntfy_token", "tk_secret");
        var handler = new RecordingHandler();

        await NewNotifier(handler).NotifyAsync(NotificationCategories.Jobs, "Sync job 'x' completed.", "success");

        Assert.Multiple(() =>
        {
            Assert.That(handler.Sent[0].Auth, Is.EqualTo("Bearer tk_secret"));
            Assert.That(handler.Sent[0].Priority, Is.EqualTo("default"));
            Assert.That(handler.Sent[0].Tags, Is.EqualTo("white_check_mark"));
        });
    }

    [Test]
    public async Task NonSuccessAnswer_DoesNotThrow()
    {
        await _settings.SetAsync("ntfy_enabled", "true");
        await _settings.SetAsync("ntfy_url", "https://ntfy.example/music");
        var handler = new RecordingHandler { Status = HttpStatusCode.NotFound };

        Assert.DoesNotThrowAsync(() => NewNotifier(handler)
            .NotifyAsync(NotificationCategories.Failures, "boom", "warn"));
        Assert.That(handler.Sent, Has.Count.EqualTo(1), "the attempt still reached the wire");
    }

    [Test]
    public async Task TestSend_ReportsTheError_AndSuccessIsNull()
    {
        var handler = new RecordingHandler();
        var notifier = NewNotifier(handler);

        var missing = await notifier.TestAsync("", null);
        Assert.That(missing, Is.EqualTo("No ntfy URL configured."));

        var ok = await notifier.TestAsync("https://ntfy.example/music", "tk");
        Assert.That(ok, Is.Null);
        Assert.That(handler.Sent, Has.Count.EqualTo(1), "the empty URL never reached the wire");
    }
}
