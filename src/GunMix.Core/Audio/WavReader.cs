using GunMix.Core.Model;
using NAudio.Wave;

namespace GunMix.Core.Audio;

public sealed class WavDecodeException : Exception
{
    public WavDecodeException(string message) : base(message) { }
}

public sealed record DecodedAudio(float[] Data, int Channels, int SampleRate, long FrameCount)
{
    public long SampleCount => Data.LongLength;
}

/// <summary>
/// WAV 读取：支持 PCM 16/24/32 bit 与 IEEE float 32 bit，单/双声道。
/// 其他编码、声道或文件抛出具体原因，不静默跳过。
/// </summary>
public static class WavReader
{
    public static DecodedAudio Read(string path)
    {
        if (!File.Exists(path))
            throw new WavDecodeException("文件不存在。");
        try
        {
            using var reader = new WaveFileReader(path);
            var fmt = reader.WaveFormat;
            bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat;
            bool isPcm = fmt.Encoding == WaveFormatEncoding.Pcm;
            if (!isFloat && !isPcm)
                throw new WavDecodeException($"不支持的编码格式 0x{(int)fmt.Encoding:X4}（仅支持 PCM 与 IEEE float）。");
            if (fmt.Channels is < 1 or > 2)
                throw new WavDecodeException($"不支持的声道数 {fmt.Channels}（仅支持单/双声道）。");
            int bits = fmt.BitsPerSample;
            if (isFloat && bits != 32)
                throw new WavDecodeException($"不支持的 float 位深 {bits}（仅支持 32 bit float）。");
            if (isPcm && bits is not (16 or 24 or 32))
                throw new WavDecodeException($"不支持的 PCM 位深 {bits}（支持 16/24/32 bit）。");

            long total = reader.Length;
            int bytesPerSample = bits / 8;
            long sampleCount = total / bytesPerSample;
            long frameCount = sampleCount / fmt.Channels;
            var raw = new byte[total];
            int read = 0;
            while (read < total)
            {
                int n = reader.Read(raw, read, (int)Math.Min(int.MaxValue, total - read));
                if (n <= 0) break;
                read += n;
            }
            total = read;
            sampleCount = total / bytesPerSample;
            frameCount = sampleCount / fmt.Channels;

            var data = new float[sampleCount];
            switch (bits)
            {
                case 16:
                    for (long i = 0; i < sampleCount; i++)
                        data[i] = BitConverter.ToInt16(raw, (int)(i * 2)) / 32768f;
                    break;
                case 24:
                    for (long i = 0; i < sampleCount; i++)
                    {
                        int o = (int)(i * 3);
                        int v = raw[o] | (raw[o + 1] << 8) | (raw[o + 2] << 16);
                        if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                        data[i] = v / 8388608f;
                    }
                    break;
                case 32 when isFloat:
                    for (long i = 0; i < sampleCount; i++)
                        data[i] = BitConverter.ToSingle(raw, (int)(i * 4));
                    break;
                case 32:
                    for (long i = 0; i < sampleCount; i++)
                        data[i] = BitConverter.ToInt32(raw, (int)(i * 4)) / 2147483648f;
                    break;
            }
            return new DecodedAudio(data, fmt.Channels, fmt.SampleRate, frameCount);
        }
        catch (WavDecodeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new WavDecodeException($"无法解码 WAV：{ex.Message}");
        }
    }

    /// <summary>只读文件头，快速获取格式（用于扫描列表；失败返回错误信息）。</summary>
    public static (AudioFormatInfo? Info, string? Error) ReadFormat(string path)
    {
        try
        {
            using var reader = new WaveFileReader(path);
            var fmt = reader.WaveFormat;
            long frameCount = reader.Length / (fmt.BitsPerSample / 8 * fmt.Channels);
            bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat;
            return (new AudioFormatInfo(fmt.SampleRate, fmt.BitsPerSample, fmt.Channels, isFloat, frameCount), null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }
}
