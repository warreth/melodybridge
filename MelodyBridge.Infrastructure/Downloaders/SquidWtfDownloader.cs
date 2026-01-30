namespace MelodyBridge.Infrastructure.Downloaders;

using System;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Resolvers;
using MelodyBridge.Infrastructure.Apis;
using MelodyBridge.Infrastructure.Helpers;

/// <summary>
/// Abstract base class for squid.wtf downloaders
/// </summary>
public abstract class SquidwtfDownloaderBase : IDownloaderPlugin
{
    // Downloads a track from the specific squid.wtf site
    public abstract Track DownloadTrack(SongID songID, TrackQuality quality);

    // Simple error checking for null arguments
    protected void CheckArguments(SongID songID, TrackQuality quality)
    {
        if (songID == null) throw new ArgumentNullException(nameof(songID));
        if (quality == null) throw new ArgumentNullException(nameof(quality));
    }
}

/// <summary>
/// Concrete implementation for qobuz.squid.wtf
/// </summary>
public class QobuzSquidwtfDownloader : SquidwtfDownloaderBase
{
    /// <summary>
    /// Downloads a track from qobuz.squid.wtf using the Qobuz track ID.
    /// </summary>
    public override Track DownloadTrack(SongID songID, TrackQuality quality)
    {
        CheckArguments(songID, quality);

        // Step 1: Resolve Qobuz track ID using QobuzIdResolver
        QobuzIdResolver resolver = new QobuzIdResolver();
        long? qobuzId = resolver.GetQobuzTrackIdByIsrcAsync(songID.ID).GetAwaiter().GetResult();
        if (qobuzId == null)
            throw new Exception($"Qobuz ID not found for ISRC: {songID.ID}");


        // Step 2: Map TrackQuality to squid.wtf quality code
        string qualityCode = quality switch
        {
            { Bitrate: 24, Format: MediaType.FLAC } => "27", //TODO: VERIFY THESE CODES
            { Bitrate: 320, Format: MediaType.MP3 } => "6",
            { Bitrate: 320, Format: MediaType.AAC } => "4",
            { Bitrate: 320, Format: MediaType.OPUS } => "5",
            { Bitrate: 16, Format: MediaType.FLAC } => "6",
            _ => "6" //Default
        };

        // Step 3: Get download URL using QobuzSquidWtfApi
        string downloadUrl = QobuzSquidWtfApi.GetDownloadUrl(qobuzId.Value, qualityCode);
        if (string.IsNullOrEmpty(downloadUrl))
            throw new Exception("Failed to obtain download URL from qobuz.squid.wtf");


        // Step 4: Generate temp file path using TrackFileHelper
        string tempFile = TrackFileHelper.GetTempTrackFilePath(qobuzId.Value, quality.Bitrate.ToString(), quality.Format.ToString().ToLower());

        // Step 5: Download file using TrackFileHelper
        TrackFileHelper.DownloadFile(downloadUrl, tempFile);

        // Step 6: Return Track
        return new Track
        {
            CurrentTrackLocation = new FileLocation(tempFile),
            PlatformSongID = new SongID(Platform.Qobuz, qobuzId.Value.ToString()),
            Quality = quality, //TODO: Verify quality after download
            SourcePlatform = Platform.Qobuz,
            MediaType = quality.Format,
            SyncStatus = SyncStatus.Completed

        };
    }
}

//TODO: Metadata fetcher plugins

//TODO: Metadata tagger