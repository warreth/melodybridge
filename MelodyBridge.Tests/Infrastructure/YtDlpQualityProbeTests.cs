using MelodyBridge.Infrastructure.Downloaders;

namespace MelodyBridge.Tests.Infrastructure;

/// <summary>
/// The picker's exact-quality probe parser: yt-dlp's simulate JSON turned
/// into the facts the dialog shows. Real shapes pinned from live yt-dlp
/// output (YouTube opus, SoundCloud HLS AAC), plus the degenerate inputs
/// that must yield "the plugin cannot tell" instead of a guess.
/// </summary>
[TestFixture]
public class YtDlpQualityProbeTests
{
    [Test]
    public void Parse_SoundCloudAac_TakesTheSelectedFormat()
    {
        // The exact shape yt-dlp returns for a SoundCloud track.
        const string json = """
        {
          "id": "582793881", "title": "Acid Engage",
          "format": "hls_aac_160k - audio only", "format_id": "hls_aac_160k",
          "acodec": "mp4a.40.2", "abr": 160, "ext": "m4a", "duration": 508.281
        }
        """;

        var quality = YtDlpProcess.ParseAudioQualityJson(json);

        Assert.That(quality, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(quality!.Codec, Is.EqualTo("mp4a.40.2"));
            Assert.That(quality.BitrateKbps, Is.EqualTo(160));
            Assert.That(quality.Lossless, Is.False);
        });
    }

    [Test]
    public void Parse_YouTubeOpus_TakesAbrAndSampleRate()
    {
        const string json = """
        {
          "id": "K0HSD_i2DvA", "title": "Song",
          "format_id": "251", "format_note": "medium",
          "acodec": "opus", "abr": 162.828, "asr": 48000, "ext": "webm"
        }
        """;

        var quality = YtDlpProcess.ParseAudioQualityJson(json);

        Assert.That(quality, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(quality!.Codec, Is.EqualTo("opus"));
            Assert.That(quality.BitrateKbps, Is.EqualTo(163));
            Assert.That(quality.SampleRateHz, Is.EqualTo(48000));
            Assert.That(quality.Note, Is.EqualTo("medium"));
        });
    }

    [TestCase("flac")]
    [TestCase("alac")]
    [TestCase("pcm_s24le")]
    public void Parse_LosslessCodecs_AreMarkedLossless(string codec)
    {
        var json = $$"""{ "acodec": "{{codec}}", "abr": 1411 }""";

        var quality = YtDlpProcess.ParseAudioQualityJson(json);

        Assert.That(quality!.Lossless, Is.True);
    }

    [Test]
    public void Parse_PlaylistRoot_ReadsTheFirstEntry()
    {
        const string json = """
        {
          "id": "search", "_type": "playlist",
          "entries": [
            { "id": "a", "acodec": "mp4a.40.2", "abr": 128, "asr": 44100 }
          ]
        }
        """;

        var quality = YtDlpProcess.ParseAudioQualityJson(json);

        Assert.That(quality, Is.Not.Null);
        Assert.That(quality!.BitrateKbps, Is.EqualTo(128));
    }

    [Test]
    public void Parse_NoAudioCodec_ReturnsNull()
    {
        Assert.Multiple(() =>
        {
            Assert.That(YtDlpProcess.ParseAudioQualityJson("""{ "acodec": "none" }"""), Is.Null,
                "an entry with no audio stream has no audio facts");
            Assert.That(YtDlpProcess.ParseAudioQualityJson("""{ "title": "no codec here" }"""), Is.Null);
            Assert.That(YtDlpProcess.ParseAudioQualityJson("not json at all"), Is.Null);
            Assert.That(YtDlpProcess.ParseAudioQualityJson("[]"), Is.Null);
        });
    }
}
