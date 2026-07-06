namespace MelodyBridge.Core;

public interface IAsyncDownloader
{
    /// <summary>
    /// Download the track and return the local file path.
    /// </summary>
    Task<string> DownloadAsync(string sourceIdentifier, string outputDirectory, string melodyId, CancellationToken ct = default);
    /// <summary>
    /// Whether this downloader can handle the given source identifier (eg. a YouTube URL)
    /// </summary>
    bool CanHandle(string sourceIdentifier);
    string Name { get; }
}
