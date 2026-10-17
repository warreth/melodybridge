using System.Net;
using MelodyBridge.Infrastructure.Services;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The shared transient-failure retry, against a scripted HTTP boundary:
/// exact attempt counts, 4xx never retried, transport errors retried and
/// the last response returned when the attempts run out.
/// </summary>
[TestFixture]
public class HttpRetryTests
{
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _answers;
        public int Attempts { get; private set; }

        public SequenceHandler(params Func<HttpResponseMessage>[] answers)
            => _answers = new Queue<Func<HttpResponseMessage>>(answers);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Attempts++;
            if (_answers.Count == 0)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            return Task.FromResult(_answers.Dequeue()());
        }
    }

    private static Task<HttpResponseMessage> Send(
        HttpClient http, int attempts, CancellationToken ct = default)
        => HttpRetry.SendAsync(
            token => http.GetAsync("https://example.test/x", token),
            attempts: attempts,
            initialDelay: TimeSpan.FromMilliseconds(1),
            ct: ct);

    [Test]
    public async Task SendAsync_500ThenSuccess_RetriesAndReturnsTheGoodAnswer()
    {
        using var handler = new SequenceHandler(
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            () => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler);

        using var response = await Send(http, attempts: 3);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(handler.Attempts, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task SendAsync_404_IsNotRetried()
    {
        using var handler = new SequenceHandler(
            () => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler);

        using var response = await Send(http, attempts: 3);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
            Assert.That(handler.Attempts, Is.EqualTo(1), "a 4xx is a verdict, not a hiccup");
        });
    }

    [Test]
    public async Task SendAsync_TransportFailureThenSuccess_Retries()
    {
        using var handler = new SequenceHandler(
            () => throw new HttpRequestException("connection reset"),
            () => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler);

        using var response = await Send(http, attempts: 3);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
            Assert.That(handler.Attempts, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task SendAsync_AllAttemptsFail_ReturnsTheLastResponse()
    {
        using var handler = new SequenceHandler(
            () => new HttpResponseMessage(HttpStatusCode.BadGateway),
            () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            () => new HttpResponseMessage(HttpStatusCode.GatewayTimeout));
        using var http = new HttpClient(handler);

        using var response = await Send(http, attempts: 3);

        Assert.Multiple(() =>
        {
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.GatewayTimeout));
            Assert.That(handler.Attempts, Is.EqualTo(3), "exactly the requested attempts");
        });
    }

    [Test]
    public async Task SendAsync_TransportFailureExhaustsAttempts_Throws()
    {
        using var handler = new SequenceHandler(
            () => throw new HttpRequestException("a"),
            () => throw new HttpRequestException("b"));
        using var http = new HttpClient(handler);

        var attempts = 0;
        try
        {
            await HttpRetry.SendAsync(
                async token =>
                {
                    attempts++;
                    return await http.GetAsync("https://example.test/x", token);
                },
                attempts: 2,
                initialDelay: TimeSpan.FromMilliseconds(1));
            Assert.Fail("the transport error must surface once the attempts run out");
        }
        catch (HttpRequestException ex)
        {
            Assert.Multiple(() =>
            {
                Assert.That(ex.Message, Is.EqualTo("b"), "the last failure is the one reported");
                Assert.That(attempts, Is.EqualTo(2));
            });
        }
    }

    [Test]
    public async Task SendAsync_CallerCancellation_StopsWithoutRetrying()
    {
        using var handler = new SequenceHandler(
            () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
            () => new HttpResponseMessage(HttpStatusCode.OK));
        using var http = new HttpClient(handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await HttpRetry.SendAsync(
                token => http.GetAsync("https://example.test/x", token),
                attempts: 3,
                initialDelay: TimeSpan.FromMilliseconds(1),
                ct: cts.Token));
    }
}
