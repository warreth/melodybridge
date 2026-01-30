using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using MelodyBridge.Core;

namespace MelodyBridge.Infrastructure.Helpers
{
    /// <summary>
    /// Default HTTP file download strategy.
    /// </summary>
    public class HttpFileDownloadStrategy : IFileDownloadStrategy
    {
        public bool CanHandle(string url)
        {
            return url.StartsWith("http://") || url.StartsWith("https://");
        }

        /// <summary>
        /// Downloads a file using standard HTTP GET.
        /// </summary>
        public async Task DownloadFileAsync(string url, string filePath)
        {
            using var client = new HttpClient();
            HttpResponseMessage resp;
            try
            {
                resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to download file: {ex.Message}");
            }

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"Download failed with HTTP {(int)resp.StatusCode}");

            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            await resp.Content.CopyToAsync(fs);
        }
    }

    /// <summary>
    /// Helper for downloading track files, supporting multiple sites and strategies.
    /// </summary>
    public static class TrackFileHelper
    {
        // List of registered strategies (can be extended for other sites)
        private static readonly List<IFileDownloadStrategy> _strategies = new List<IFileDownloadStrategy>
        {
            new HttpFileDownloadStrategy()
            // Add more strategies here for other sites if needed
        };

        /// <summary>
        /// Downloads a file from the given URL to the specified file path using the appropriate strategy.
        /// </summary>
        public static void DownloadFile(string url, string filePath)
        {
            DownloadFileAsync(url, filePath).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Asynchronously downloads a file from the given URL to the specified file path.
        /// </summary>
        public static async Task DownloadFileAsync(string url, string filePath)
        {
            foreach (var strategy in _strategies)
            {
                if (strategy.CanHandle(url))
                {
                    await strategy.DownloadFileAsync(url, filePath);
                    return;
                }
            }
            throw new NotSupportedException($"No download strategy found for URL: {url}");
        }

        /// <summary>
        /// Generates a temp file path for a track.
        /// </summary>
        public static string GetTempTrackFilePath(long trackId, string qualityCode, string extension)
        {
            string tempDir = Path.GetTempPath();
            string fileName = $"{trackId}_{qualityCode}_{Guid.NewGuid().ToString("N")}.{extension}";
            return Path.Combine(tempDir, fileName);
        }
    }
}
