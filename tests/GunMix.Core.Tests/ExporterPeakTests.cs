using System.IO;
using GunMix.Core.Audio;
using GunMix.Core.Mixing;
using Xunit;

namespace GunMix.Core.Tests;

public class ExporterPeakTests
{
    [Theory]
    [InlineData(0.5f, null)]
    [InlineData(2f, -1.0)]
    public void FloatReportMatchesWrittenSamples(float amplitude, double? target)
    {
        string path = Path.Combine(Path.GetTempPath(), $"gunmix-peak-{Guid.NewGuid():N}.wav");
        float[] input = [amplitude, -amplitude];
        try
        {
            var result = Exporter.WriteWav(input, 48000, 1, 32, false, 0, target, path, false);
            Assert.True(result.Success, result.Error);
            double actual = WavReader.Read(path).Data.Max(v => Math.Abs((double)v));
            Assert.Equal(20 * Math.Log10(actual), result.QuantizedPeakDbfs, 5);
            if (target is { } expected) Assert.Equal(expected, result.QuantizedPeakDbfs, 5);
            Assert.Equal(amplitude, input[0]);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NegativeFullScalePcmExportsWithoutOverflow(bool sourceFormat)
    {
        string path = Path.Combine(Path.GetTempPath(), $"gunmix-pcm-{Guid.NewGuid():N}.wav");
        try
        {
            var result = Exporter.WriteWav([-2f, -2f], 44100, 1, 16, false, 0, null, path, false, sourceFormat);
            Assert.True(result.Success, result.Error);
            Assert.True(double.IsFinite(result.QuantizedPeakDbfs));
            Assert.Equal(-1f, WavReader.Read(path).Data[0]);
        }
        finally { File.Delete(path); }
    }
}
