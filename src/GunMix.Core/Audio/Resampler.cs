using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace GunMix.Core.Audio;

/// <summary>采样率转换（其他采样率在内存副本中转换为工程 48 kHz，原文件保持不变）。</summary>
public static class Resampler
{
    public static float[] Resample(float[] interleaved, int channels, int srcRate, int dstRate)
    {
        if (srcRate == dstRate || interleaved.Length == 0)
            return interleaved;
        var source = new RawFloatSource(interleaved, channels, srcRate);
        var resampler = new WdlResamplingSampleProvider(source, dstRate);
        long outFrames = (long)Math.Ceiling(interleaved.LongLength / channels * (double)dstRate / srcRate);
        var output = new float[outFrames * channels];
        int total = 0;
        while (total < output.Length)
        {
            int n = resampler.Read(output, total, output.Length - total);
            if (n <= 0) break;
            total += n;
        }
        if (total < output.Length)
            Array.Clear(output, total, output.Length - total);
        return output;
    }

    private sealed class RawFloatSource(float[] data, int channels, int rate) : ISampleProvider
    {
        private int _pos;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(rate, channels);

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, data.Length - _pos);
            if (n <= 0) return 0;
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
    }
}
