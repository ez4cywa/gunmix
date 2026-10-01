using GunMix.Core.Audio;
using GunMix.Core.Model;

namespace GunMix.Core.Synthesis;

/// <summary>素材响度测量：峰值、前 250 ms RMS（声道平均能量）与 95% 累计能量时刻。</summary>
public static class LoudnessMeter
{
    public static AssetLoudness Measure(float[] data, int channels, int sampleRate)
    {
        long frames = data.LongLength / channels;
        long earlyFrames = Math.Min(frames, (long)Math.Round(sampleRate * AssetLoudness.EarlyWindowMs / 1000.0));
        double peak = 0, earlySum = 0, total = 0;
        var cumulative = new double[frames];
        for (long f = 0; f < frames; f++)
        {
            double e = 0;
            for (int c = 0; c < channels; c++)
            {
                double v = data[f * channels + c];
                e += v * v;
                double a = Math.Abs(v);
                if (a > peak) peak = a;
            }
            e /= channels;
            total += e;
            cumulative[f] = total;
            if (f < earlyFrames) earlySum += e;
        }
        double e95Ms = 0;
        if (total > 0)
        {
            int idx = Array.BinarySearch(cumulative, total * 0.95);
            if (idx < 0) idx = ~idx;
            e95Ms = idx * 1000.0 / sampleRate;
        }
        double earlyRms = earlyFrames > 0 ? Math.Sqrt(earlySum / earlyFrames) : 0;
        return new AssetLoudness(ToDb(peak), ToDb(earlyRms), Math.Round(e95Ms, 1));
    }

    public static AssetLoudness Measure(DecodedAudio audio) => Measure(audio.Data, audio.Channels, audio.SampleRate);

    public static double ToDb(double linear) => linear > 1e-12 ? Math.Round(20 * Math.Log10(linear), 2) : -240;

    /// <summary>一组素材的代表响度：前 250 ms RMS 的中位数（与研究脚本的组统计口径一致）。未测量返回 null。</summary>
    public static double? GroupEarlyRms(IEnumerable<AssetInfo> assets)
    {
        var values = assets.Select(a => a.Loudness?.EarlyRmsDbfs).Where(v => v is > -200).Select(v => v!.Value)
            .OrderBy(v => v).ToList();
        if (values.Count == 0) return null;
        int mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }
}
