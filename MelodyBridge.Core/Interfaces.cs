using System.Net.NetworkInformation;
using MelodyBridge.Core;

namespace MelodyBridge.Core;
/// <summary>
/// Interface for file download strategies for different sites.
/// </summary>
public interface IFileDownloadStrategy
{
    /// <summary>
    /// Downloads a file from the given URL to the specified file path.
    /// </summary>
    Task DownloadFileAsync(string url, string filePath);
    /// <summary>
    /// Returns true if this strategy can handle the given URL.
    /// </summary>
    bool CanHandle(string url);
}