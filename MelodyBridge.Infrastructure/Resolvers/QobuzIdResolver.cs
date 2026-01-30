//Should resolve Qobuz IDs for tracks where possible
namespace MelodyBridge.Infrastructure.Resolvers;

using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

/// <summary>
/// Resolves Qobuz track IDs using ISRC codes.
/// </summary>
public class QobuzIdResolver
{
    private readonly HttpClient _client;
    private readonly string _appId;

    /// <summary>
    /// Initializes a new instance of the QobuzIdResolver class.
    /// </summary>
    public QobuzIdResolver()
    {
        _client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        _appId = "798273057"; //! From SpotiFLAC, can be changed later
    }

    /// <summary>
    /// Searches Qobuz for a track by ISRC and returns the Qobuz track ID.
    /// </summary>
    /// <param name="isrc">The ISRC code to search for.</param>
    /// <returns>The Qobuz track ID if found, otherwise null.</returns>
    public async Task<long?> GetQobuzTrackIdByIsrcAsync(string isrc)
    {
        // Simple error checking for input
        if (string.IsNullOrWhiteSpace(isrc))
            throw new ArgumentException("ISRC must not be empty.", nameof(isrc));

        // Build Qobuz API URL
        var apiBase = "https://www.qobuz.com/api.json/0.2/track/search?query=";
        var url = $"{apiBase}{isrc}&limit=1&app_id={_appId}";

        HttpResponseMessage response;
        try
        {
            response = await _client.GetAsync(url);
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to search track: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
            throw new Exception($"API returned status {(int)response.StatusCode}");

        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
            throw new Exception("API returned empty response");

        // Only deserialize the minimum needed for the ID
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var items = root.GetProperty("tracks").GetProperty("items");
            if (items.GetArrayLength() == 0)
                throw new Exception($"Track not found for ISRC: {isrc}");
            var id = items[0].GetProperty("id").GetInt64();
            return id;
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to extract Qobuz ID: {ex.Message}", ex);
        }
    }
}

