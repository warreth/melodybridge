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
    /// <summary>Analyze the whole file (slower, also catches subtle ones). Very long files are capped at 15 minutes.</summary>
    Thorough,
}

/// <summary>
/// Detects files that carry far less audio bandwidth than their declared
/// bitrate needs: a 320 kbps header on top of a source that stopped much
/// lower. It decodes the audio, averages the spectrum with FFT frames,
/// and looks at how the high end falls off.
///
/// A spectrum cannot tell an upscaled rip from a master that never had
/// the high end, so the check stays deliberately narrow. Content that
/// fades out gently is music that was mixed that way, not a mislabelled
/// rip; Opus and Vorbis stay near full band even at low bitrates; and
/// only a steep codec-style corner, at least two bitrate classes below
/// the declared one (the gap is configurable), counts as inflated. A file
/// with no such corner gets no quality verdict at all. The check only
/// flags, it never deletes; the flagged file becomes a review the user
/// settles with Keep or Retry.
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
    // The Settings page can lower or raise it (1..4); this is the default.
    public const int DefaultClassGap = 2;

    // The bitrate classes a bandwidth can imply, low to high.
    private static readonly int[] Classes = { 96, 128, 160, 192, 256, 320 };

    /// <summary>
    /// Result of one spectral verification.
    /// <see cref="BandwidthHz"/> is the highest frequency still within the
    /// content threshold of the midband; it is a lower bound shaped by the
    /// music, so it is only trustworthy when <see cref="CodecCutoff"/> is
    /// true and the value sits at a real corner.
    /// </summary>
    public record Result(
        bool LooksInflated,
        bool CodecCutoff,
        double BandwidthHz,
        int EffectiveKbpsClass,
        int? DeclaredKbps,
        string? Codec,
        string Note);

    /// <summary>
    /// Verifies a downloaded audio file against its declared bitrate.
    /// <paramref name="classGap"/> is how many bitrate classes the measured
    /// bandwidth may sit below the declared one before the file counts as
    /// inflated (1 strict .. 4 forgiving). Returns null when the mode is
    /// Off, ffmpeg or ffprobe is missing, or the file has no readable
    /// audio facts or spectrum.
    /// </summary>
    public static Result? Verify(string filePath, SpectrumMode mode, int classGap = DefaultClassGap)
    {
        if (mode == SpectrumMode.Off) return null;
        var ffmpeg = FindBinary("ffmpeg");
        if (ffmpeg is null) return null;
        // Without ffprobe there is no codec or declared bitrate to judge
        // against, so the file skips the check instead of getting a
        // meaningless hint.
        if (FindFfprobe() is null) return null;

        try
        {
            var facts = AudioFactsProbe.Probe(filePath);
            if (facts.Codec is null && facts.BitrateKbps is null) return null;

            var powerDb = WelchSpectrum(ffmpeg, filePath, mode);
            if (powerDb is null || powerDb.Length == 0) return null;

            var (bandwidthHz, cliffDropDb) = FindBandwidth(powerDb);
            return Judge(bandwidthHz, cliffDropDb, facts, classGap);
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
    /// Decodes the analysed window to mono 16-bit PCM at a fixed rate and
    /// Welch-averages its spectrum in dB, one value per bin. The PCM is
    /// consumed frame by frame as ffmpeg produces it, so a long file never
    /// has to sit in memory whole. Fast reads the first minute, Thorough
    /// the whole file (capped at 15 minutes so it cannot run away).
    /// </summary>
    private static double[]? WelchSpectrum(string ffmpeg, string filePath, SpectrumMode mode)
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

        var window = HannWindow(FrameSize);
        var bins = FrameSize / 2 + 1;
        var accum = new double[bins];
        var frame = new Complex[FrameSize];
        var samples = new short[FrameSize];
        var bytes = new byte[FrameSize * 2];
        var hopBytes = HopSize * 2;
        var frames = 0;

        var ok = ToolProcessRunner.RunToStream(ffmpeg, args, timeoutMs: 180000, stream =>
        {
            // Fill one frame, then slide by half a frame (50% overlap).
            if (ReadAtLeast(stream, bytes, 0, bytes.Length) < bytes.Length) return;
            while (true)
            {
                Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
                if (AccumulateFrame(samples, window, frame, accum)) frames++;
                Buffer.BlockCopy(bytes, hopBytes, bytes, 0, bytes.Length - hopBytes);
                if (ReadAtLeast(stream, bytes, bytes.Length - hopBytes, hopBytes) < hopBytes) break;
            }
        });

        if (!ok || frames == 0) return null;

        var db = new double[bins];
        for (var k = 0; k < bins; k++)
            db[k] = 10 * Math.Log10(accum[k] / frames + 1e-12);
        return db;
    }

    /// <summary>
    /// Adds one Hann-windowed frame to the running spectrum. Returns false
    /// for a near-silent frame (about -72 dBFS): it would only add a flat
    /// noise floor and drag the core reference down.
    /// </summary>
    private static bool AccumulateFrame(short[] samples, double[] window, Complex[] frame, double[] accum)
    {
        double energy = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            double s = samples[i];
            energy += s * s;
        }
        if (Math.Sqrt(energy / samples.Length) < 8) return false;

        for (var i = 0; i < samples.Length; i++)
            frame[i] = new Complex(samples[i] * window[i], 0);

        Fft.Forward(frame);
        for (var k = 0; k < accum.Length; k++)
            accum[k] += frame[k].Magnitude * frame[k].Magnitude;
        return true;
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes, or as many as the stream has before it ends.</summary>
    private static int ReadAtLeast(Stream stream, byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = stream.Read(buffer, offset + total, count - total);
            if (read <= 0) break;
            total += read;
        }
        return total;
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

    private static Result Judge(double bandwidthHz, double cliffDropDb, AudioFacts facts, int classGap)
    {
        var declared = facts.BitrateKbps;
        var codec = facts.Codec?.ToLowerInvariant();
        var nyquist = facts.SampleRateHz is > 0 ? facts.SampleRateHz.Value / 2.0 : AnalysisRate / 2.0;
        var effective = BandwidthToKbps(bandwidthHz, codec, nyquist);

        // The drop is the only evidence a spectrum can offer: without a
        // codec-style corner there is nothing to accuse, and the measured
        // bandwidth is just the music's own fade and must not be reported.
        var cutoff = cliffDropDb >= CliffDropDb;
        var declaredIndex = declared is > 0 ? ClassIndex(declared.Value) : -1;
        var inflated = cutoff
            && declaredIndex >= ClassIndex(InflatedDeclaredFloorKbps)
            && declaredIndex - ClassIndex(effective) >= Math.Clamp(classGap, 1, 4);

        var note = Describe(bandwidthHz, effective, declared, inflated, cutoff);
        return new Result(inflated, cutoff, bandwidthHz, effective, declared, codec, note);
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

    private static string Describe(double bandwidthHz, int effective, int? declared, bool inflated, bool cutoff)
    {
        var khz = (bandwidthHz / 1000).ToString("F1", CultureInfo.InvariantCulture);
        if (inflated)
            return $"bandwidth stops at {khz} kHz, far below what {declared} kbps needs";
        if (cutoff)
            return $"bandwidth drops off at {khz} kHz, about {effective} kbps of real detail";
        return $"high end fades out around {khz} kHz with no codec cutoff to read";
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
