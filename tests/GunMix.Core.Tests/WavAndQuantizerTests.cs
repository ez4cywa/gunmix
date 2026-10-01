using System.IO;
using GunMix.Core.Audio;
using GunMix.Core.Timeline;
using Xunit;

namespace GunMix.Core.Tests;

public class WavAndQuantizerTests : IDisposable
{
    private readonly string _dir;

    public WavAndQuantizerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gunmix_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败忽略 */ }
    }

    private static float[] Sine(int frames, int channels, double amp = 0.5)
    {
        var data = new float[frames * channels];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < channels; c++)
                data[f * channels + c] = (float)(amp * Math.Sin(2 * Math.PI * 440 * f / 48000.0));
        return data;
    }

    [Theory]
    [InlineData(16, false)]
    [InlineData(24, false)]
    [InlineData(32, true)]
    public void RoundtripPreservesData(int bits, bool isFloat)
    {
        var path = Path.Combine(_dir, $"t{bits}.wav");
        var data = Sine(4800, 2);
        byte[] raw = bits switch
        {
            16 => Quantizer.ToInt16Bytes(Quantizer.ToInt16Legacy(data)),
            24 => Quantizer.ToInt24Bytes(data, false, 0),
            _ => Quantizer.ToFloat32Bytes(data),
        };
        WavWriter.Write(path, 48000, bits, isFloat, 2, raw);

        var decoded = WavReader.Read(path);
        Assert.Equal(48000, decoded.SampleRate);
        Assert.Equal(2, decoded.Channels);
        Assert.Equal(4800, decoded.FrameCount);
        Assert.Equal(data.Length, decoded.Data.Length);
        double maxErr = 0;
        for (int i = 0; i < data.Length; i++)
            maxErr = Math.Max(maxErr, Math.Abs(data[i] - decoded.Data[i]));
        double tolerance = bits switch { 16 => 1.0 / 32767, 24 => 1.0 / 8388607, _ => 1e-6 };
        Assert.True(maxErr <= tolerance, $"maxErr={maxErr}");
    }

    [Fact]
    public void LegacyQuantizationMatchesRintTimes32767()
    {
        var data = new float[] { 0.5f, -0.5f, 1.0f / 32767f, 0.00002f };
        var q = Quantizer.ToInt16Legacy(data);
        // np.rint 采用半值舍入到偶数
        Assert.Equal((short)(Math.Round(0.5 * 32767)), q[0]);
        Assert.Equal((short)(Math.Round(-0.5 * 32767)), q[1]);
        Assert.Equal(1, q[2]);
        Assert.Equal(1, q[3]); // 0.00002 × 32767 ≈ 0.655 → 舍入到 1
    }

    [Fact]
    public void TpdfDitherIsDeterministicWithSeed()
    {
        var data = Sine(1000, 2);
        var a = Quantizer.ToInt16(data, true, seed: 7);
        var b = Quantizer.ToInt16(data, true, seed: 7);
        var c = Quantizer.ToInt16(data, true, seed: 8);
        Assert.Equal(a, b);      // 同种子同结果
        Assert.NotEqual(a, c);   // 不同种子不同抖动
        // 抖动不改变整体电平：均值偏差远小于 1 LSB
        double meanA = a.Average(v => (double)v);
        double meanC = c.Average(v => (double)v);
        Assert.True(Math.Abs(meanA - meanC) < 1.0);
    }

    [Fact]
    public void ClampingAtFullScale()
    {
        var data = new float[] { 2.0f, -2.0f };
        var q = Quantizer.ToInt16Legacy(data);
        Assert.Equal(32767, q[0]);
        Assert.Equal(-32768, q[1]);
    }

    [Fact]
    public void UnsupportedEncodingRejected()
    {
        // 非法文件：写垃圾字节
        var path = Path.Combine(_dir, "bad.wav");
        File.WriteAllBytes(path, [0x52, 0x49, 0x46, 0x46, 0x00, 0x00]);
        Assert.Throws<WavDecodeException>(() => { WavReader.Read(path); });
    }

    [Fact]
    public void MissingFileRejected()
    {
        Assert.Throws<WavDecodeException>(() => { WavReader.Read(Path.Combine(_dir, "nope.wav")); });
    }

    [Fact]
    public void ResampleChangesRate()
    {
        var data = Sine(4800, 2);
        var resampled = Resampler.Resample(data, 2, 44100, 48000);
        long expectedFrames = (long)Math.Ceiling(4800 * 48000.0 / 44100);
        Assert.Equal(expectedFrames * 2, resampled.LongLength);
    }
}
