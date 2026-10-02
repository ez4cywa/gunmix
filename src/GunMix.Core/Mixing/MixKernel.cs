using GunMix.Core.Audio;
using GunMix.Core.Model;
using GunMix.Core.Timeline;

namespace GunMix.Core.Mixing;

public sealed class MixResult
{
    public required float[] Data { get; init; }

    /// <summary>输出帧数（交错数据的 Data.Length / 2）。</summary>
    public required long Frames { get; init; }

    /// <summary>输出峰值（线性），基于实际混合结果计算。</summary>
    public required double Peak { get; init; }

    public double PeakDbfs => Peak > 0 ? 20 * Math.Log10(Peak) : -120;

    public bool ExceedsFullScale => Peak > 1.0;

    public List<string> MissingAssets { get; init; } = [];
}

/// <summary>
/// 混音内核：按事件生成 float32 音频块，处理声道、增益、延时和尾部。
/// 单声道默认等幅复制至左右声道，双声道原样保留；增益按线性系数相乘后累加；
/// 每次触发创建独立声音实例，允许自然重叠，新一发不截断旧实例。
/// </summary>
public static class MixKernel
{
    /// <summary>resolve 缺失素材时返回 null，并在 MissingAssets 中记录。</summary>
    public static MixResult Render(
        CompiledTimeline timeline,
        Func<ShotEvent, AssetBuffer?> resolve,
        int channels = 2,
        long? totalSamples = null)
    {
        if(channels is not (1 or 2))throw new ArgumentException("仅支持单/双声道输出。");
        long frames = totalSamples ?? timeline.TotalSamples;
        if (frames <= 0) frames = 1;
        var data = new float[frames * channels];
        double peak = 0;
        var missing = new List<string>();

        foreach (var ev in timeline.Events)
        {
            var buffer = resolve(ev);
            if (buffer == null)
            {
                if (timeline.AssetById.TryGetValue(ev.AssetId, out var info))
                    missing.Add(info.FileName);
                continue;
            }
            double gain = Math.Pow(10, ev.GainDb / 20.0);
            var src = buffer.Data;
            int srcCh = buffer.Channels;
            long srcFrames = buffer.FrameCount;
            long start = ev.StartSample;

            if(!double.IsFinite(ev.PitchRatio)||ev.PitchRatio<=0||start<0||ev.SourceOffsetSamples<0||!double.IsFinite(ev.GainDb))throw new ArgumentException("无效的播放实例。");
            long copyFrames = Math.Min((long)Math.Ceiling((srcFrames-ev.SourceOffsetSamples)/ev.PitchRatio), frames - start);
            // 实例抢占：从淡出起点线性淡出，结束后停止（未抢占的实例增益路径与之前完全相同）
            long fadeFrom = long.MaxValue, fadeLen = 1;
            if (ev.FadeOutStartSample is { } fadeStart)
            {
                fadeFrom = Math.Max(0, fadeStart - start);
                fadeLen = Math.Max(1, ev.FadeOutSamples);
                copyFrames = Math.Min(copyFrames, fadeFrom + Math.Max(0,ev.FadeOutSamples));
            }
            for (long f = 0; f < copyFrames; f++)
            {
                long dst = (start + f) * channels;
                double sourceFrame=ev.SourceOffsetSamples+f*ev.PitchRatio;
                long sourceIndex=(long)sourceFrame;
                long s = sourceIndex * srcCh;
                long next=Math.Min(sourceIndex+1,srcFrames-1)*srcCh;
                float fraction=(float)(sourceFrame-sourceIndex);
                float Sample(int ch)=>src[s+ch]+(src[next+ch]-src[s+ch])*fraction;
                float g = f < fadeFrom ? (float)gain : (float)(gain * (1.0 - (f - fadeFrom) / (double)fadeLen));
                if (srcCh == 1)
                {
                    float v = Sample(0) * g;
                    data[dst] += v;
                    if (channels > 1) data[dst + 1] += v;
                    if (channels == 1)
                    {
                        double a = Math.Abs(data[dst]);
                        if (a > peak) peak = a;
                    }
                }
                else
                {
                    float l = Sample(0) * g;
                    float r = srcCh > 1 ? Sample(1) * g : l;
                    data[dst] += l;
                    if (channels > 1) data[dst + 1] += r;
                }
                if (channels > 1)
                {
                    double a = Math.Max(Math.Abs(data[dst]), Math.Abs(data[dst + 1]));
                    if (a > peak) peak = a;
                }
            }
        }

        peak=0;
        foreach(float value in data)peak=Math.Max(peak,Math.Abs(value));
        return new MixResult { Data = data, Frames = frames, Peak = peak, MissingAssets = missing };
    }
}
