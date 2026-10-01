namespace GunMix.Core.Mixing;

public readonly record struct TailTrimResult(float[] Data, long RemovedFrames, long KeptFrames)
{
    public bool DidTrim => RemovedFrames > 0;
}

/// <summary>
/// 尾部静音截断：从混合结果末尾向前找到最后一个超过阈值的采样，其后保留 tailMs 尾巴再截断。
/// 只处理尾部真正的静音/极低电平，不改动枪声本体的自然衰减；阈值默认 -60 dBFS，保守到只砍长静音。
/// 输入为交错 float32。
/// </summary>
public static class TailTrimmer
{
    public const double DefaultThresholdDb = -60.0;
    public const double DefaultTailMs = 120.0;

    public static TailTrimResult Trim(
        float[] interleaved, int channels, int sampleRate,
        double thresholdDb = DefaultThresholdDb, double tailMs = DefaultTailMs)
    {
        if (channels < 1 || interleaved.Length == 0)
            return new TailTrimResult(interleaved, 0, 0);

        double threshold = Math.Pow(10, thresholdDb / 20.0);
        long frames = interleaved.Length / channels;

        long lastLoud = -1;
        for (long f = frames - 1; f >= 0; f--)
        {
            long baseIdx = f * channels;
            bool found = false;
            for (int c = 0; c < channels; c++)
            {
                if (Math.Abs((double)interleaved[baseIdx + c]) >= threshold)
                {
                    found = true;
                    break;
                }
            }
            if (found)
            {
                lastLoud = f;
                break;
            }
        }

        // 全为低于阈值的尾部：保留 tailMs，其余截断（避免整段静音导出）。
        long tailFrames = Math.Max(0, (long)Math.Round(tailMs / 1000.0 * sampleRate, MidpointRounding.ToEven));
        long keep = lastLoud < 0
            ? Math.Min(frames, tailFrames)
            : Math.Min(frames, lastLoud + 1 + tailFrames);

        if (keep >= frames)
            return new TailTrimResult(interleaved, 0, frames);

        var result = new float[keep * channels];
        Array.Copy(interleaved, result, result.Length);
        return new TailTrimResult(result, frames - keep, keep);
    }
}
