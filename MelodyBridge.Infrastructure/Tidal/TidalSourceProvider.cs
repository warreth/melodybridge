using System.Text.RegularExpressions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Tidal;

/// <summary>
/// Public Tidal playlist source. The same no-account public token the Tidal
/// search uses reads the playlist and pages its tracks 50 at a time, so a
/// pasted Tidal link works like a Spotify or YouTube link.
/// </summary>
public sealed partial class TidalSourceProvider : ISourceProvider
{
    private const string DefaultCountry = "US";
    private const string DownloaderId = "tidal";

    private readonly TidalPublicCatalog _catalog;
    private readonly IPluginConfig _config;
    private readonly ILogger<TidalSourceProvider> _logger;

    public string Name => "Tidal";
    public Platform Platform => Platform.Tidal;

    public TidalSourceProvider(
        TidalPublicCatalog catalog,
        IPluginConfig config,
        ILogger<TidalSourceProvider> logger)
    {
        _catalog = catalog;
        _config = config;
        _logger = logger;
    }

    public bool CanHandle(string sourceIdentifier)
        => PlaylistRegex().IsMatch(sourceIdentifier ?? string.Empty);

    public async Task<Playlist> GetPlaylistAsync(string sourceIdentifier)
    {
        var match = PlaylistRegex().Match(sourceIdentifier ?? string.Empty);
        if (!match.Success)
            throw new ArgumentException(
                "Enter a Tidal playlist link, for example https://tidal.com/browse/playlist/…",
                nameof(sourceIdentifier));

        var id = match.Groups["id"].Value;
        var country = await CountryAsync();
        var result = await _catalog.GetPlaylistAsync(id, country);

        if (result.Status != TidalPublicCatalog.SearchStatus.Ok || result.Playlist is null)
        {
            var message = result.Status switch
            {
                TidalPublicCatalog.SearchStatus.NotFound =>
                    "Tidal did not find that playlist. It must be public.",
                TidalPublicCatalog.SearchStatus.Rejected =>
                    "Tidal refused the public playlist token. Try again in a moment.",
                TidalPublicCatalog.SearchStatus.RateLimited =>
                    "Tidal rate limited the playlist request. Try again in a minute.",
                _ => result.Message ?? "The Tidal playlist could not be read.",
            };
            throw new InvalidOperationException(message);
        }

        var tracks = result.Tracks.Select(MapTrack).ToList();
        var playlist = new Playlist
        {
            Id = result.Playlist.Id,
            Name = result.Playlist.Title,
            Owner = result.Playlist.Owner,
            Description = result.Playlist.Description,
            SourceUrl = $"https://tidal.com/browse/playlist/{result.Playlist.Id}",
            CoverImageUrl = result.Playlist.CoverUrl,
            Tracks = tracks,
            TrackCount = result.Playlist.TrackCount > 0 ? result.Playlist.TrackCount : tracks.Count,
            Duration = result.Playlist.Duration,
        };

        if (result.Playlist.TrackCount > tracks.Count)
            playlist.Warning = $"partial fetch: read {tracks.Count} of {result.Playlist.TrackCount} tracks";

        _logger.LogInformation(
            "Fetched Tidal playlist '{Name}' with {Count} tracks", playlist.Name, playlist.TrackCount);
        return playlist;
    }

    private static Track MapTrack(TidalPublicCatalog.TidalPublicTrack track) => new()
    {
        Title = track.Title,
        Artist = track.Artist,
        Album = track.Album,
        AlbumArtist = track.AlbumArtist,
        CoverUrl = track.CoverUrl,
        Isrc = track.Isrc,
        Duration = track.Duration,
        SourcePlatform = Platform.Tidal,
        SongID = new SongID(Platform.Tidal, track.Id.ToString()),
        PlatformSongID = new SongID(Platform.Tidal, track.Id.ToString()),
    };

    private async Task<string> CountryAsync()
    {
        try
        {
            var value = await _config.GetAsync(DownloaderId, "country_code");
            return string.IsNullOrWhiteSpace(value) ? DefaultCountry : value.Trim().ToUpperInvariant();
        }
        catch
        {
            return DefaultCountry;
        }
    }

    [GeneratedRegex(
        @"^(?:https?://(?:www\.|listen\.)?tidal\.com/(?:browse/)?playlist/|tidal:playlist:)(?<id>[0-9a-fA-F-]{36})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaylistRegex();
}
