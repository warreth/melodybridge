using System.Text.RegularExpressions;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.SearchProviders;
using Microsoft.Extensions.Logging;

namespace MelodyBridge.Infrastructure.Qobuz;

/// <summary>
/// Public Qobuz playlist source. Qobuz has no anonymous playlist API, so
/// this uses the same Qobuz token the plugin already stores for search and
/// downloads. A missing token becomes a clear setup message, not an empty
/// playlist.
/// </summary>
public sealed partial class QobuzSourceProvider : ISourceProvider
{
    private readonly QobuzClient _client;
    private readonly QobuzSession _session;
    private readonly ILogger<QobuzSourceProvider> _logger;

    public string Name => "Qobuz";
    public Platform Platform => Platform.Qobuz;

    public QobuzSourceProvider(
        QobuzClient client,
        QobuzSession session,
        ILogger<QobuzSourceProvider> logger)
    {
        _client = client;
        _session = session;
        _logger = logger;
    }

    public bool CanHandle(string sourceIdentifier)
        => PlaylistRegex().IsMatch(sourceIdentifier ?? string.Empty);

    public async Task<Playlist> GetPlaylistAsync(string sourceIdentifier)
    {
        var match = PlaylistRegex().Match(sourceIdentifier ?? string.Empty);
        if (!match.Success || !long.TryParse(match.Groups["id"].Value, out var id))
            throw new ArgumentException(
                "Enter a Qobuz playlist link, for example https://play.qobuz.com/playlist/…",
                nameof(sourceIdentifier));

        var token = await _session.GetTokenAsync();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException(
                "Qobuz playlist import needs the Qobuz token. Add it on the Plugins page, "
                + "in the Qobuz card.");

        var result = await _client.GetPlaylistAsync(id, token);
        if (result.Status != QobuzClient.QobuzSearchStatus.Ok || result.Playlist is null)
        {
            var message = result.Status switch
            {
                QobuzClient.QobuzSearchStatus.NotFound =>
                    "Qobuz did not find that playlist. It must be public.",
                QobuzClient.QobuzSearchStatus.Rejected =>
                    "Qobuz rejected the token. Paste a fresh Qobuz token on the Plugins page.",
                QobuzClient.QobuzSearchStatus.Blocked =>
                    "Qobuz blocked this account from reading playlists.",
                _ => result.Message ?? "The Qobuz playlist could not be read.",
            };
            throw new InvalidOperationException(message);
        }

        var tracks = result.Playlist.Tracks.Select(MapTrack).ToList();
        var playlist = new Playlist
        {
            Id = result.Playlist.Id.ToString(),
            Name = result.Playlist.Title,
            Owner = result.Playlist.Owner,
            Description = result.Playlist.Description,
            SourceUrl = $"https://play.qobuz.com/playlist/{result.Playlist.Id}",
            CoverImageUrl = result.Playlist.CoverUrl,
            Tracks = tracks,
            TrackCount = result.Playlist.TrackCount > 0
                ? result.Playlist.TrackCount
                : tracks.Count,
            Duration = result.Playlist.Duration,
        };

        if (result.Playlist.TrackCount > tracks.Count)
            playlist.Warning =
                $"partial fetch: read {tracks.Count} of {result.Playlist.TrackCount} tracks";

        _logger.LogInformation(
            "Fetched Qobuz playlist '{Name}' with {Count} tracks", playlist.Name, playlist.TrackCount);
        return playlist;
    }

    private static Track MapTrack(QobuzClient.QobuzTrack track) => new()
    {
        Title = track.Title,
        Artist = track.Artist,
        Album = track.Album,
        AlbumArtist = track.AlbumArtist,
        CoverUrl = track.CoverUrl,
        Isrc = track.Isrc,
        Duration = track.Duration,
        SourcePlatform = Platform.Qobuz,
        SongID = new SongID(Platform.Qobuz, track.Id.ToString()),
        PlatformSongID = new SongID(Platform.Qobuz, track.Id.ToString()),
    };

    [GeneratedRegex(
        @"^(?:https?://(?:play|www)\.qobuz\.com/(?:[a-z]{2}-[a-z]{2}/)?playlist/(?:[^/?#]+/)?|qobuz:playlist:)(?<id>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaylistRegex();
}
