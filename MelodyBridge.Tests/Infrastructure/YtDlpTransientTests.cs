using MelodyBridge.Infrastructure.Downloaders;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// Transient-failure classification for the yt-dlp retry: media-URL 403s
/// and timeouts deserve a second extraction, real errors (deleted video,
/// private video) do not.
/// </summary>
[TestFixture]
public class YtDlpTransientTests
{
    [Test]
    public void MediaUrl403_IsTransient()
        => Assert.That(YtDlpProcess.IsTransientFailure(
            "ERROR: unable to download video data: HTTP Error 403: Forbidden"), Is.True);

    [Test]
    public void ServerError_IsTransient()
        => Assert.That(YtDlpProcess.IsTransientFailure("ERROR: HTTP Error 503: Service Unavailable"), Is.True);

    [Test]
    public void Timeout_IsTransient()
        => Assert.That(YtDlpProcess.IsTransientFailure("ERROR: The read operation timed out"), Is.True);

    [Test]
    public void DeletedVideo_IsNotTransient()
        => Assert.That(YtDlpProcess.IsTransientFailure(
            "ERROR: [youtube] xyz: Video unavailable"), Is.False);

    [Test]
    public void PrivateVideo_IsNotTransient()
        => Assert.That(YtDlpProcess.IsTransientFailure(
            "ERROR: [youtube] xyz: Private video. Sign in if you've been granted access"), Is.False);
}
