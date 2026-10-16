using System.Net;

namespace MelodyBridge.Infrastructure.Services;

/// <summary>
/// Retry for transient upstream failures. Public archives and mirrors
/// answer with an occasional 5xx or a dropped connection under load; the
/// download plugins treat one such answer as a hard failure today, which
/// loses the track even though a retry a moment later succeeds.
///
/// Only idempotent GET-style sends belong here, and only 5xx, 408 and
/// transport timeouts are retried: a 4xx is a verdict the caller must
/// report (geo-block, taken down, bad URL), never something to hammer.
/// </summary>
public static class HttpRetry
{
    /// <summary>
    /// Sends the request through <paramref name="send"/>, retrying
    /// transient failures with a doubling delay. The final response, good
    /// or bad, is returned to the caller; the caller owns disposal.
    /// </summary>
    public static async Task<HttpResponseMessage> SendAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        int attempts = 3,
        TimeSpan? initialDelay = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(send);
        if (attempts < 1) attempts = 1;

        var delay = initialDelay ?? TimeSpan.FromMilliseconds(300);

        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                response = await send(ct);
                if (!IsTransient(response.StatusCode) || attempt >= attempts)
                    return response;
            }
            catch (HttpRequestException) when (attempt < attempts)
            {
                // Transport failure: the retry below is the whole point.
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested && attempt < attempts)
            {
                // A client-side timeout, not a caller cancellation.
            }

            response?.Dispose();
            await Task.Delay(delay, ct);
            delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, TimeSpan.TicksPerSecond * 5));
        }
    }

    /// <summary>5xx and 408 mean "try again"; 429 is the server's own pacing and is left to callers that can honour Retry-After.</summary>
    private static bool IsTransient(HttpStatusCode status)
        => (int)status >= 500 || status == HttpStatusCode.RequestTimeout;
}
