using System.Globalization;
using System.Numerics;

namespace MelodyBridge.Infrastructure.Audio;

/// <summary>
/// How strictly downloaded files are verified after download.
/// </summary>
public enum SpectrumMode
{
    /// <summary>Trust the declared bitrate.</summary>
    Off,
    /// <summary>Analyze the first 60 seconds (fast, catches obvious blow-ups).</summary>
    Fast,
    /// <summary>Analyze the whole file (slower, also catches subtle ones).</summary>
    Thorough,
}

/// <summary>
/// Detects files that carry far less audio bandwidth than their declared
/// bitrate needs: a 320 kbps header on top of a source that stopped much
/// lower. The real bandwidth is measured from a decoded spectrum (an
/// average of FFT frames), so it reads the codec's own lowpass instead of
/// the music's spectral tilt. The old rolloff percentile sat at 6-10 kHz
/// for ordinary bass-heavy music even when the file was genuine.
///
/// Two safeguards keep it honest: a file whose high end fades gently is
/// content that is band-limited, not a mislabelled rip, and is reported
/// inconclusive; and Opus/Vorbis stay near-fullband at low bitrates, so
/// their bandwidth never implies a low class. The check only warns, it
/// never deletes.
/// </summary>
public static class SpectrumAnalyzer
{
    // One fixed decode rate keeps the FFT bin-to-frequency mapping stable.
    private const int AnalysisRate = 44100;
    private const int FrameSize = 4096;        // ~10.8 Hz per bin at 44.1 kHz
    private const int HopSize = FrameSize / 2; // 50% overlap, Welch averaging
    private const double ReferenceLowHz = 300;
    private const double ReferenceHighHz = 3000;

    // A band counts as real content while it sits within this many dB of
    // the core level; below that it is codec noise or dither.
    private const double RealContentDropDb = 20;

    // A codec lowpass falls off a cliff. Content that merely fades out is
    // not accused; only a drop at least this steep is treated as a cutoff.
    private const double CliffDropDb = 15;

    private const int MinRunHz = 300;

    // Below this declared bitrate a low bandwidth is plausible and never
    // called inflated; the check hunts mislabelled high-bitrate files.
    private const int InflatedDeclaredFloorKbps = 192;

    // The measured bandwidth must imply a class at least this many steps
    // below the declared one before it is called inflated. One step is
    // within the noise of real rips; two is a corner that should not exist.
    private const int InflatedClassGap = 2;

    // The bitrate classes a bandwidth can imply, low to high.
    private static readonly int[] Classes = { 96, 128, 160, 192, 256, 320 };

    /// <summary>Result of one spectral verification.</summary>
    public record Result(
        bool LooksInflated,
        double BandwidthHz,
        int EffectiveKbpsClass,
        int? DeclaredKbps,
        string? Codec,
        string Note);

    /// <summary>
    /// Verifies a downloaded audio file's real bandwidth against its
    /// declared bitrate. Returns null when the mode is Off, ffmpeg or
    /// ffprobe is missing, or the file cannot be read.
    /// </summary>
    public static Result? Verify(string filePath, SpectrumMode mode)
    {
        if (mode == SpectrumMode.Off) return null;
        var ffmpeg = FindBinary("ffmpeg");
        if (ffmpeg is null) return null;

        try
        {
            var facts = AudioFactsProbe.Probe(filePath);
            var samples = Decode(ffmpeg, filePath, mode);
            if (samples is null || samples.Length < FrameSize * 2) return null;

            var powerDb = WelchPowerDb(samples);
            if (powerDb.Length == 0) return null;

            var (bandwidthHz, cliffDropDb) = FindBandwidth(powerDb);
            return Judge(bandwidthHz, cliffDropDb, facts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Proposes what to do with an inflated file (re-download path).</summary>
    public static string? SuggestFix(Result result) => result.LooksInflated
        ? "Re-download this track: pick a lower cap or the 'auto' preset so it keeps the source codec instead of an inflated re-encode."
        : null;

    // ---- measurement ---------------------------------------------------

    /// <summary>
    /// Decodes the analysed window to mono 16-bit PCM at a fixed rate.
    /// Fast reads the first minute, Thorough the whole file (capped, so a
    /// very long file cannot exhaust memory).
    /// </summary>
    private static short[]? Decode(string ffmpeg, string filePath, SpectrumMode mode)
    {
        var args = new List<string> { "-v", "error", "-hide_banner" };
        args.Add("-t");
        args.Add(mode == SpectrumMode.Fast ? "60" : "900");
        args.AddRange(new[]
        {
            "-i", filePath,
            "-vn",
            "-ac", "1",
            "-ar", AnalysisRate.ToString(CultureInfo.InvariantCulture),
            "-f", "s16le",
            "-",
        });

        var result = ToolProcessRunner.RunBinary(ffmpeg, args, timeoutMs: 180000);
        if (result.TimedOut || result.ExitCode != 0) return null;
        if (result.Stdout.Length < 2) return null;

        var samples = new short[result.Stdout.Length / 2];
        Buffer.BlockCopy(result.Stdout, 0, samples, 0, samples.Length * 2);
        return samples;
    }

    /// <summary>Welch average power spectrum in dB, one value per bin.</summary>
    private static double[] WelchPowerDb(short[] samples)
    {
        var window = HannWindow(FrameSize);
        var bins = FrameSize / 2 + 1;
        var accum = new double[bins];
        var frame = new Complex[FrameSize];
        var frames = 0;

        for (var start = 0; start + FrameSize <= samples.Length; start += HopSize)
        {
            double energy = 0;
            for (var i = 0; i < FrameSize; i++)
            {
                double s = samples[start + i];
                energy += s * s;
            }
            // Skip near-silent frames (about -72 dBFS): they would only add
            // a flat noise floor and drag the core reference down.
            if (Math.Sqrt(energy / FrameSize) < 8) continue;

            for (var i = 0; i < FrameSize; i++)
                frame[i] = new Complex(samples[start + i] * window[i], 0);

            Fft.Forward(frame);
            for (var k = 0; k < bins; k++)
                accum[k] += frame[k].Magnitude * frame[k].Magnitude;
            frames++;
        }

        if (frames == 0) return Array.Empty<double>();

        var db = new double[bins];
        for (var k = 0; k < bins; k++)
            db[k] = 10 * Math.Log10(accum[k] / frames + 1e-12);
        return db;
    }

    private static double[] HannWindow(int size)
    {
        var window = new double[size];
        for (var i = 0; i < size; i++)
            window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (size - 1));
        return window;
    }

    /// <summary>
    /// Finds the highest frequency that still holds real content, and how
    /// steeply the spectrum falls just above it.
    /// </summary>
    private static (double BandwidthHz, double CliffDropDb) FindBandwidth(double[] powerDb)
    {
        var binHz = (double)AnalysisRate / FrameSize;
        var smooth = Smooth(powerDb, Math.Max(1, (int)Math.Round(150 / binHz)));

        var lowBin = Math.Max(1, (int)(ReferenceLowHz / binHz));
        var highBin = Math.Min(smooth.Length - 1, (int)(ReferenceHighHz / binHz));
        var reference = Median(smooth, lowBin, highBin);
        var threshold = reference - RealContentDropDb;

        var runBins = Math.Max(1, (int)Math.Round(MinRunHz / binHz));
        var bandwidthBin = lowBin;
        for (var k = lowBin; k + runBins < smooth.Length; k++)
        {
            if (BandAverage(smooth, k, k + runBins) >= threshold)
                bandwidthBin = k;
        }
        var bandwidthHz = bandwidthBin * binHz;

        // Only measure the drop when there is room above the edge; a file
        // that runs to Nyquist is full-band by definition.
        var belowLow = (int)((bandwidthHz - 1200) / binHz);
        var belowHigh = (int)((bandwidthHz - 200) / binHz);
        var aboveLow = (int)((bandwidthHz + 200) / binHz);
        var aboveHigh = (int)((bandwidthHz + 1200) / binHz);
        double drop = 0;
        if (belowLow >= 0 && aboveHigh < smooth.Length)
            drop = BandAverage(smooth, belowLow, belowHigh) - BandAverage(smooth, aboveLow, aboveHigh);

        return (bandwidthHz, drop);
    }

    private static double[] Smooth(double[] values, int halfWidth)
    {
        var result = new double[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            var from = Math.Max(0, i - halfWidth);
            var to = Math.Min(values.Length - 1, i + halfWidth);
            result[i] = BandAverage(values, from, to + 1);
        }
        return result;
    }

    private static double BandAverage(double[] values, int from, int to)
    {
        from = Math.Max(0, from);
        to = Math.Min(values.Length, to);
        if (to <= from) return double.NegativeInfinity;
        double sum = 0;
        for (var i = from; i < to; i++) sum += values[i];
        return sum / (to - from);
    }

    private static double Median(double[] values, int from, int to)
    {
        if (to <= from) return 0;
        var slice = new double[to - from];
        Array.Copy(values, from, slice, 0, slice.Length);
        Array.Sort(slice);
        return slice[slice.Length / 2];
    }

    // ---- verdict -------------------------------------------------------

    private static Result Judge(double bandwidthHz, double cliffDropDb, AudioFacts facts)
    {
        var declared = facts.BitrateKbps;
        var codec = facts.Codec?.ToLowerInvariant();
        var nyquist = facts.SampleRateHz is > 0 ? facts.SampleRateHz.Value / 2.0 : AnalysisRate / 2.0;
        var effective = BandwidthToKbps(bandwidthHz, codec, nyquist);

        var declaredIndex = declared is > 0 ? ClassIndex(declared.Value) : -1;
        var inflated = declaredIndex >= ClassIndex(InflatedDeclaredFloorKbps)
            && declaredIndex - ClassIndex(effective) >= InflatedClassGap
            && cliffDropDb >= CliffDropDb;

        var note = Describe(bandwidthHz, effective, declared, inflated);
        return new Result(inflated, bandwidthHz, effective, declared, codec, note);
    }

    /// <summary>Index of the highest class at or below <paramref name="kbps"/>.</summary>
    private static int ClassIndex(int kbps)
    {
        var index = 0;
        for (var i = 0; i < Classes.Length; i++)
            if (kbps >= Classes[i]) index = i;
        return index;
    }

    /// <summary>
    /// The bitrate the measured bandwidth can plausibly carry. Opus and
    /// Vorbis reach near-fullband at low bitrates, so their bandwidth never
    /// implies a low class. A low sample rate caps what the format can
    /// carry, so the measured bandwidth is scaled to a 44.1 kHz reference.
    /// </summary>
    private static int BandwidthToKbps(double bandwidthHz, string? codec, double nyquist)
    {
        if (codec is "opus" or "vorbis") return 320;
        if (codec is "flac" or "alac" or "wav" or "pcm_s16le" or "pcm_s24le") return 320;

        if (nyquist is > 0 and < AnalysisRate / 2.0)
            bandwidthHz *= (AnalysisRate / 2.0) / nyquist;

        return bandwidthHz switch
        {
            >= 19500 => 320,
            >= 17500 => 256,
            >= 16000 => 192,
            >= 14500 => 160,
            >= 12500 => 128,
            _ => 96,
        };
    }

    private static string Describe(double bandwidthHz, int effective, int? declared, bool inflated)
    {
        var khz = (bandwidthHz / 1000).ToString("F1", CultureInfo.InvariantCulture);
        if (inflated)
            return $"bandwidth stops at {khz} kHz, far below what {declared} kbps needs";
        return $"bandwidth reaches {khz} kHz, about {effective} kbps of real detail";
    }

    // ---- tool discovery ------------------------------------------------

    internal static string? FindBinary(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(OperatingSystem.IsWindows() ? ';' : ':',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = System.IO.Path.Combine(dir.Trim(), name);
                if (OperatingSystem.IsWindows())
                {
                    // Windows never resolves executables without their
                    // explicit extension: probe both "ffmpeg" and "ffmpeg.exe".
                    if (System.IO.File.Exists(candidate)) return candidate;
                    if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        && System.IO.File.Exists(candidate + ".exe"))
                        return candidate + ".exe";
                }
                else if (System.IO.File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch { /* unreadable PATH entry */ }
        }
        return null;
    }

    /// <summary>ffprobe path, shared with <see cref="BitrateProbe"/>.</summary>
    public static string? FindFfprobe() => FindBinary("ffprobe");
}
