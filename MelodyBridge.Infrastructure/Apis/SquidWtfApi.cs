using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MelodyBridge.Infrastructure.Apis
{
    /// <summary>
    /// Provides API helpers for qobuz.squid.wtf.
    /// </summary>
    public static class QobuzSquidWtfApi
    {
        /// <summary>
        /// Gets the direct FLAC download URL for a Qobuz track.
        /// </summary>
        /// <param name="trackId">Qobuz track ID</param>
        /// <param name="qualityCode">Quality code string</param>
        /// <returns>Direct FLAC URL as string</returns>
        public static string GetDownloadUrl(long trackId, string qualityCode)
        {
            // Compose API URL
            string apiUrl = $"https://qobuz.squid.wtf/api/download-music?track_id={trackId}&quality={qualityCode}";

            // Call async helper synchronously for compatibility
            return GetDownloadUrlAsync(apiUrl).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Calls the qobuz.squid.wtf API and parses the download URL.
        /// </summary>
        private static async Task<string> GetDownloadUrlAsync(string apiUrl)
        {
            using var client = new HttpClient();
            HttpResponseMessage resp;
            try
            {
                resp = await client.GetAsync(apiUrl);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to contact qobuz.squid.wtf: {ex.Message}");
            }

            if (!resp.IsSuccessStatusCode)
                throw new Exception($"qobuz.squid.wtf returned HTTP {(int)resp.StatusCode}");

            string body = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body))
                throw new Exception("qobuz.squid.wtf returned empty response");

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("url", out var urlProp))
                {
                    string? url = urlProp.GetString();
                    if (!string.IsNullOrEmpty(url))
                        return url;
                }
            }
            catch (JsonException ex)
            {
                throw new Exception($"Failed to parse qobuz.squid.wtf response: {ex.Message}");
            }

            throw new Exception("Download URL not found in qobuz.squid.wtf response");
        }
    }
}
