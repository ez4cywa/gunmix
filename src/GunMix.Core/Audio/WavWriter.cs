using NAudio.Wave;

namespace GunMix.Core.Audio;

/// <summary>
/// WAV 写入：写临时文件成功后发布最终文件名；取消或失败不留下完整结果。
/// </summary>
public static class WavWriter
{
    /// <summary>将 float32 交错采样写入 WAV。bytes 为已量化的原始数据。</summary>
    public static void Write(string path, int sampleRate, int bitsPerSample, bool isFloat, int channels, byte[] rawData)
    {
        WaveFormat fmt = isFloat
            ? WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels)
            : new WaveFormat(sampleRate, bitsPerSample, channels);
        using var writer = new WaveFileWriter(path, fmt);
        writer.Write(rawData, 0, rawData.Length);
        writer.Flush();
    }
}
