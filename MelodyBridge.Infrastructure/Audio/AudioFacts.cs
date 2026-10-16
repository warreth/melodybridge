using System.Text.Json;

namespace MelodyBridge.Infrastructure.Audio;

/// <summary>
/// Codec, declared bitrate and sample rate of a local audio file, read
/// with ffprobe. The spectrum check needs the codec to interpret a
/// measured bandwidth (Opus stays near-fullband at low bitrates, MP3 does
/// not) and the sample rate to know the ceiling the format can carry.
/// </summary>
public readonly record struct AudioFacts(string? Codec, int? BitrateKbps, int? SampleRateHz);

/// <summary>Reads <see cref="AudioFacts"/> for one file with one ffprobe run.</summary>
public static class AudioFactsProbe
{
    public static AudioFacts Probe(string filePath)
    {
        var ffprobe = SpectrumAnalyzer.FindFfprobe();
        if (ffprobe is null) return default;

        try
        {
            var result = ToolProcessRunner.Run(ffprobe, new[]
            {
                "-v", "error",
                "-select_streams", "a:0",
                "-show_entries", "stream=codec_name,bit_rate,sample_rate:format=bit_rate",
                "-of", "json",
                filePath,
            }, timeoutMs: 15000);
            if (result.TimedOut || result.ExitCode != 0) return default;

            using var doc = JsonDocument.Parse(result.Stdout);
            var root = doc.RootElement;
            string? codec = null;
            int? kbps = null;
            int? rate = null;

            if (root.TryGetProperty("streams", out var streams)
                && streams.ValueKind == JsonValueKind.Array
                && streams.GetArrayLength() > 0)
            {
                var stream = streams[0];
                if (stream.TryGetProperty("codec_name", out var c) && c.ValueKind == JsonValueKind.String)
                    codec = c.GetString();
                if (stream.TryGetProperty("bit_rate", out var b))
                    kbps = ParseKbps(b);
                if (stream.TryGetProperty("sample_rate", out var sr)
                    && sr.ValueKind == JsonValueKind.String
                    && int.TryParse(sr.GetString(), out var parsedRate) && parsedRate > 0)
                    rate = parsedRate;
            }

            // FLAC and Opus carry no stream-level bit_rate: only format has it.
            if (kbps is null
                && root.TryGetProperty("format", out var format)
                && format.TryGetProperty("bit_rate", out var formatBitRate))
                kbps = ParseKbps(formatBitRate);

            return new AudioFacts(codec, kbps, rate);
        }
        catch
        {
            return default;
        }
    }

    private static int? ParseKbps(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String
            && int.TryParse(value.GetString(), out var text) && text > 0)
            return text / 1000;
        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number) && number > 0)
            return (int)(number / 1000);
        return null;
    }
}
