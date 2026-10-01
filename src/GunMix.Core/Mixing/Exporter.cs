using GunMix.Core.Audio;
using GunMix.Core.Model;

namespace GunMix.Core.Mixing;

public sealed record ExportResult
{
    public bool Success { get; init; }

    public string? FinalPath { get; init; }

    public double DurationSeconds { get; init; }

    /// <summary>量化前 float 混音峰值 dBFS（32 bit float 导出也显示高于 0 dBFS 的峰值）。</summary>
    public double PeakDbfs { get; init; }

    /// <summary>量化后文件的实际峰值 dBFS。</summary>
    public double QuantizedPeakDbfs { get; init; }

    /// <summary>应用的显式衰减 dB（负数）；未应用为 null。该值必须记录进导出报告。</summary>
    public double? AppliedGainDb { get; init; }

    /// <summary>自动截尾移除的帧数（0 = 未截或无可截）；记录进导出报告。</summary>
    public long TrimmedFrames { get; init; }

    public double? TrimThresholdDb { get; init; }

    public double? TrimTailMs { get; init; }

    public string? Error { get; init; }
}

/// <summary>
/// 导出：先写临时文件，成功后发布最终文件名；取消或失败不留下被误认成完整结果的 WAV。
/// 不默认加限制器；越界时按用户显式选择的“降低总输出至目标 dBFS”衰减并记录。
/// </summary>
public static class Exporter
{
    public static ExportResult WriteWav(
        float[] mixedData,
        int sampleRate,
        int channels,
        int bitDepth,
        bool dither,
        int ditherSeed,
        double? attenuateToDbfs,
        string finalPath,
        bool overwrite)
    {
        try
        {
            if (File.Exists(finalPath) && !overwrite)
                return new ExportResult { Success = false, Error = $"目标文件已存在：{Path.GetFileName(finalPath)}（默认生成新名称）" };

            double peak = 0;
            foreach (var v in mixedData)
            {
                double a = Math.Abs((double)v);
                if (a > peak) peak = a;
            }

            double? appliedGain = null;
            double gain = 1.0;
            if (peak > 1.0 && attenuateToDbfs is { } target)
            {
                gain = Math.Pow(10, target / 20.0) / peak;
                appliedGain = 20 * Math.Log10(gain);
            }

            string? temp = Path.Combine(Path.GetDirectoryName(finalPath) ?? ".",
                Path.GetFileNameWithoutExtension(finalPath) + ".part" + Path.GetExtension(finalPath));

            int quantizedPeak;
            switch (bitDepth)
            {
                case 16:
                {
                    var shorts = dither
                        ? Quantizer.ToInt16(mixedData, true, ditherSeed, gain)
                        : Quantizer.ToInt16Legacy(mixedData, gain);
                    quantizedPeak = shorts.Max(Math.Abs) is { } m ? m : 0;
                    WavWriter.Write(temp, sampleRate, 16, false, channels, Quantizer.ToInt16Bytes(shorts));
                    break;
                }
                case 24:
                {
                    var bytes = Quantizer.ToInt24Bytes(mixedData, dither, ditherSeed, gain);
                    quantizedPeak = Peak24(bytes);
                    WavWriter.Write(temp, sampleRate, 24, false, channels, bytes);
                    break;
                }
                case 32:
                {
                    var bytes = Quantizer.ToFloat32Bytes(mixedData);
                    quantizedPeak = (int)Math.Round(peak * 8388608);
                    WavWriter.Write(temp, sampleRate, 32, true, channels, bytes);
                    break;
                }
                default:
                    return new ExportResult { Success = false, Error = $"不支持的导出位深 {bitDepth}（可选 16 bit PCM、24 bit PCM、32 bit float）。" };
            }

            File.Move(temp, finalPath, overwrite);
            double fullScale = bitDepth switch { 16 => 32767.0, 24 => 8388607.0, _ => 1.0 };
            double quantDbfs = quantizedPeak > 0 ? 20 * Math.Log10(quantizedPeak / fullScale) : -120;
            double duration = mixedData.LongLength / (double)(channels * sampleRate);
            return new ExportResult
            {
                Success = true,
                FinalPath = finalPath,
                DurationSeconds = duration,
                PeakDbfs = peak > 0 ? 20 * Math.Log10(peak) : -120,
                QuantizedPeakDbfs = quantDbfs,
                AppliedGainDb = appliedGain,
            };
        }
        catch (Exception ex)
        {
            return new ExportResult { Success = false, Error = $"导出失败：{ex.Message}" };
        }
    }

    private static int Peak24(byte[] bytes)
    {
        int peak = 0;
        for (int i = 0; i + 2 < bytes.Length; i += 3)
        {
            int v = bytes[i] | (bytes[i + 1] << 8) | (bytes[i + 2] << 16);
            if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
            int a = Math.Abs(v);
            if (a > peak) peak = a;
        }
        return peak;
    }
}
