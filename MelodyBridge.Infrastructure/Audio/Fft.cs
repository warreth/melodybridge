using System.Numerics;

namespace MelodyBridge.Infrastructure.Audio;

/// <summary>
/// A small in-place radix-2 FFT, enough for the spectrum check's analysis
/// window. The framework ships no FFT, and one page of well-understood
/// butterflies beats a new dependency for this.
/// </summary>
internal static class Fft
{
    /// <summary>
    /// Forward transform, in place. The buffer length must be a power of
    /// two; the caller sizes the window.
    /// </summary>
    public static void Forward(Complex[] buffer)
    {
        var n = buffer.Length;
        if (n == 0 || (n & (n - 1)) != 0)
            throw new ArgumentException("FFT size must be a power of two and non-zero", nameof(buffer));

        // Bit-reversal permutation.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;
            if (i < j)
                (buffer[i], buffer[j]) = (buffer[j], buffer[i]);
        }

        // Butterflies, doubling the transform size each pass.
        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2 * Math.PI / len;
            var root = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var start = 0; start < n; start += len)
            {
                var w = Complex.One;
                var half = len / 2;
                for (var k = 0; k < half; k++)
                {
                    var even = buffer[start + k];
                    var odd = buffer[start + k + half] * w;
                    buffer[start + k] = even + odd;
                    buffer[start + k + half] = even - odd;
                    w *= root;
                }
            }
        }
    }
}
