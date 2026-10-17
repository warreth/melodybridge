using System.Text.Json;
using MelodyBridge.Core;
using MelodyBridge.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MelodyBridge.Tests.Services;

/// <summary>
/// The SoundCloud set source: the identifier rules, the mapping of a real
/// yt-dlp payload (captured from a live set) and the Go+ preview guard,
/// which must keep 30 second clips out of the library.
/// </summary>
[TestFixture]
public class SoundCloudSourceProviderTests
{
    private static SoundCloudSourceProvider Create()
        => new(NullLogger<SoundCloudSourceProvider>.Instance);

    /// <summary>A real yt-dlp entry for a full-length track.</summary>
    private const string FullEntry = """
    {
      "id": "199428706",
      "title": "One More Time",
      "uploader": "Daft Punk",
      "duration": 322.606,
      "webpage_url": "https://soundcloud.com/someone/one-more-time",
      "url": "https://cf-media.sndcdn.com/abc.128.mp3",
      "formats": [
        { "format_id": "http_mp3_1_0", "url": "https://cf-media.sndcdn.com/abc.128.mp3" }
      ]
    }
    """;

    /// <summary>A real yt-dlp entry for a Go+ preview: every stream is a clip.</summary>
    private const string PreviewEntry = """
    {
      "id": "254112129",
      "title": "One More Time",
      "uploader": "Daft Punk",
      "duration": 30.0,
      "webpage_url": "https://soundcloud.com/daftpunkofficialmusic/one-more-time",
      "formats": [
        { "format_id": "hls_mp3_1_0_preview",
          "url": "https://cf-hls-media.sndcdn.com/playlist/0/30/x.128.mp3/playlist.m3u8" },
        { "format_id": "http_mp3_1_0_preview",
          "url": "https://cf-preview-media.sndcdn.com/preview/0/30/x.128.mp3?Policy=abc" }
      ]
    }
    """;

    private static string SetJson(params string[] entries) => $$"""
    {
      "title": "Discovery",
      "uploader": "Daft Punk",
      "webpage_url": "https://soundcloud.com/daftpunkofficialmusic/sets/discovery-8",
      "entries": [ {{string.Join(",", entries)}} ]
    }
    """;

    [TestCase("https://soundcloud.com/someone/sets/my-mix", true)]
    [TestCase("soundcloud.com/someone/sets/my-mix", true)]
    [TestCase("https://soundcloud.com/someone/my-track", false)]
    [TestCase("https://open.spotify.com/playlist/abc", false)]
    [TestCase("", false)]
    public void CanHandle_OnlySets(string input, bool expected)
        => Assert.That(Create().CanHandle(input), Is.EqualTo(expected));

    [Test]
    public void IsPreviewOnly_RealFullTrack_IsFalse()
    {
        using var doc = JsonDocument.Parse(FullEntry);
        Assert.That(SoundCloudSourceProvider.IsPreviewOnly(doc.RootElement), Is.False);
    }

    [Test]
    public void IsPreviewOnly_GoPlusPreview_IsTrue()
    {
        using var doc = JsonDocument.Parse(PreviewEntry);
        Assert.That(SoundCloudSourceProvider.IsPreviewOnly(doc.RootElement), Is.True,
            "a 30 second Go+ clip must never be treated as the track");
    }

    [Test]
    public void IsPreviewOnly_NoFormats_IsFalse()
    {
        using var doc = JsonDocument.Parse("""{"id":"1","title":"x"}""");
        Assert.That(SoundCloudSourceProvider.IsPreviewOnly(doc.RootElement), Is.False,
            "an unknown shape must not drop the entry");
    }

    [Test]
    public void ParseFlatPlaylist_MapsRealEntries_AndFiltersPreviews()
    {
        var filtered = 0;
        var playlist = YouTubeSourceProvider.ParseFlatPlaylist(
            SetJson(FullEntry, PreviewEntry),
            "https://soundcloud.com/daftpunkofficialmusic/sets/discovery-8",
            Platform.Soundcloud,
            id => $"https://soundcloud.com/{id}",
            entry =>
            {
                if (!SoundCloudSourceProvider.IsPreviewOnly(entry)) return true;
                filtered++;
                return false;
            });

        Assert.Multiple(() =>
        {
            Assert.That(filtered, Is.EqualTo(1));
            Assert.That(playlist.Name, Is.EqualTo("Discovery"));
            Assert.That(playlist.Owner, Is.EqualTo("Daft Punk"));
            Assert.That(playlist.TrackCount, Is.EqualTo(1));
        });

        var track = playlist.Tracks![0];
        Assert.Multiple(() =>
        {
            Assert.That(track.Title, Is.EqualTo("One More Time"));
            Assert.That(track.Artist, Is.EqualTo("Daft Punk"));
            Assert.That(track.Duration, Is.EqualTo(TimeSpan.FromSeconds(322.606)));
            Assert.That(track.SongID, Is.EqualTo(new SongID(Platform.Soundcloud, "199428706")));
            Assert.That(track.CurrentTrackLocation!.Path,
                Is.EqualTo("https://soundcloud.com/someone/one-more-time"),
                "the entry's own page wins over the id pattern");
        });
    }

    [Test]
    public void ParseFlatPlaylist_FallsBackToTheIdPattern()
    {
        // A bare SoundCloud id (no webpage_url) still gets a usable location.
        var playlist = YouTubeSourceProvider.ParseFlatPlaylist(
            SetJson("""{"id":"123","title":"Track","uploader":"Artist","duration":100}"""),
            "https://soundcloud.com/x/sets/y",
            Platform.Soundcloud,
            id => $"https://soundcloud.com/{id}");

        Assert.That(playlist.Tracks![0].CurrentTrackLocation!.Path,
            Is.EqualTo("https://soundcloud.com/123"));
    }

    [Test]
    public void HasPlaylistJson_AcceptsCompleteJson_AndRejectsErrors()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SoundCloudSourceProvider.HasPlaylistJson(SetJson()), Is.True);
            Assert.That(SoundCloudSourceProvider.HasPlaylistJson(""), Is.False);
            Assert.That(SoundCloudSourceProvider.HasPlaylistJson("ERROR: not found"), Is.False);
            Assert.That(SoundCloudSourceProvider.HasPlaylistJson("{\"title\":\"x\"}"), Is.False,
                "a document without an entries array is not a playlist");
        });
    }

    [Test]
    public void CountEntries_CountsDeclaredEntriesIncludingUnusableOnes()
    {
        // yt-dlp emits null for an entry it could not extract (DRM), and
        // those must count towards the honest skipped total.
        var json = """
        {"title":"Set","entries":[
          {"id":"1","title":"A"},
          null,
          {"id":"3","title":"C"}
        ]}
        """;
        Assert.That(SoundCloudSourceProvider.CountEntries(json), Is.EqualTo(3));
    }
}
