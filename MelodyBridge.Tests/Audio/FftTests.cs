using System.Numerics;
using MelodyBridge.Infrastructure.Audio;

namespace MelodyBridge.Tests.Audio;

/// <summary>
/// The radix-2 FFT on its own, with signals whose answer is known in
/// closed form. The spectrum check leans on it, so a bin off by a factor
/// of two or a missing symmetry would quietly skew every bandwidth.
/// </summary>
[TestFixture]
public class FftTests
{
    [Test]
    public void Forward_SingleTone_PeaksAtItsOwnBin()
    {
        const int size = 1024;
        const int toneBin = 120;
        var buffer = new Complex[size];
        for (var i = 0; i < size; i++)
            buffer[i] = new Complex(Math.Cos(2 * Math.PI * toneBin * i / size), 0);

        Fft.Forward(buffer);

        var peak = 1;
        for (var k = 2; k < size / 2; k++)
            if (buffer[k].Magnitude > buffer[peak].Magnitude)
                peak = k;
        Assert.That(peak, Is.EqualTo(toneBin), "a pure tone must land in its own bin");
    }

    [Test]
    public void Forward_AConstant_IsAllInBinZero()
    {
        const int size = 256;
        var buffer = new Complex[size];
        for (var i = 0; i < size; i++) buffer[i] = new Complex(1, 0);

        Fft.Forward(buffer);

        Assert.That(buffer[0].Real, Is.EqualTo(size).Within(1e-6),
            "a constant transforms to size in bin zero");
        for (var k = 1; k < size; k++)
            Assert.That(buffer[k].Magnitude, Is.LessThan(1e-6), $"bin {k} must stay empty");
    }

    [Test]
    public void Forward_TwoTones_BothHaveEnergy()
    {
        const int size = 2048;
        const int low = 100;
        const int high = 500;
        var buffer = new Complex[size];
        for (var i = 0; i < size; i++)
            buffer[i] = new Complex(
                Math.Cos(2 * Math.PI * low * i / size) + Math.Cos(2 * Math.PI * high * i / size), 0);

        Fft.Forward(buffer);

        // A unit cosine has magnitude size/2 in its bin.
        Assert.That(buffer[low].Magnitude, Is.GreaterThan(size / 4.0), "low tone present");
        Assert.That(buffer[high].Magnitude, Is.GreaterThan(size / 4.0), "high tone present");
    }

    [Test]
    public void Forward_NonPowerOfTwo_Throws()
    {
        Assert.That(() => Fft.Forward(new Complex[1000]), Throws.ArgumentException);
    }

    [Test]
    public void Forward_Empty_Throws()
    {
        Assert.That(() => Fft.Forward(Array.Empty<Complex>()), Throws.ArgumentException);
    }
}
