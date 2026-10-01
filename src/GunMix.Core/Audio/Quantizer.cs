namespace GunMix.Core.Audio;

/// <summary>
/// 量化器。整数导出默认 TPDF 抖动（可关闭）；抖动使用保存的种子，保证同版本可重现。
/// 取整方式为半值舍入到偶数（与 np.rint 一致）。
/// </summary>
public static class Quantizer
{
    public const string TpdfVersion = "tpdf-splitmix32-v1";

    public static short[] ToInt16(float[] data, bool tpdf, int seed, double gain = 1.0)
    {
        var result = new short[data.Length];
        SplitMix32 rng = default;
        bool hasRng = tpdf;
        if (tpdf) rng = new SplitMix32((uint)seed);
        for (int i = 0; i < data.Length; i++)
        {
            double x = data[i] * (double)gain * 32767.0;
            if (tpdf)
            {
                double d = (To01(rng.Next()) + To01(rng.Next()) - 1.0); // 三角分布 ±1 LSB
                x += d;
            }
            long v = (long)Math.Round(x, MidpointRounding.ToEven);
            if (v > 32767) v = 32767;
            if (v < -32768) v = -32768;
            result[i] = (short)v;
        }
        return result;
    }

    /// <summary>对照渲染使用的传统量化：无抖动，rint(x*32767)。</summary>
    public static short[] ToInt16Legacy(float[] data, double gain = 1.0)
    {
        var result = new short[data.Length];
        for (int i = 0; i < data.Length; i++)
        {
            long v = (long)Math.Round((double)data[i] * gain * 32767.0, MidpointRounding.ToEven);
            if (v > 32767) v = 32767;
            if (v < -32768) v = -32768;
            result[i] = (short)v;
        }
        return result;
    }

    public static byte[] ToInt24Bytes(float[] data, bool tpdf, int seed, double gain = 1.0)
    {
        SplitMix32 rng = tpdf ? new SplitMix32((uint)seed) : default;
        var bytes = new byte[data.Length * 3];
        for (int i = 0; i < data.Length; i++)
        {
            double x = data[i] * (double)gain * 8388607.0;
            if (tpdf)
                x += To01(rng.Next()) + To01(rng.Next()) - 1.0;
            long v = (long)Math.Round(x, MidpointRounding.ToEven);
            if (v > 8388607) v = 8388607;
            if (v < -8388608) v = -8388608;
            int u = (int)v & 0xFFFFFF;
            bytes[i * 3] = (byte)u;
            bytes[i * 3 + 1] = (byte)(u >> 8);
            bytes[i * 3 + 2] = (byte)(u >> 16);
        }
        return bytes;
    }

    public static byte[] ToInt16Bytes(short[] data)
    {
        var bytes = new byte[data.Length * 2];
        for (int i = 0; i < data.Length; i++)
            BitConverter.GetBytes(data[i]).CopyTo(bytes, i * 2);
        return bytes;
    }

    public static byte[] ToFloat32Bytes(float[] data)
    {
        var bytes = new byte[data.Length * 4];
        Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public static short[] BytesToInt16(byte[] bytes)
    {
        var result = new short[bytes.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = BitConverter.ToInt16(bytes, i * 2);
        return result;
    }

    private static double To01(uint v) => v * (1.0 / 4294967296.0);
}

/// <summary>确定性随机数（SplitMix32），保证同种子同算法版本结果一致。</summary>
public struct SplitMix32
{
    private uint _state;

    public SplitMix32(uint seed)
    {
        _state = seed;
    }

    public uint Next()
    {
        _state += 0x9E3779B9u;
        uint z = _state;
        z ^= z >> 16;
        z *= 0x21F0AAADu;
        z ^= z >> 15;
        z *= 0x735A2D97u;
        z ^= z >> 15;
        return z;
    }
}
