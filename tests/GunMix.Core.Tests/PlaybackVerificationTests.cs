using System.Threading;
using GunMix.App.Services;
using Xunit;
using Xunit.Abstractions;

namespace GunMix.Core.Tests;

/// <summary>
/// 真机播放验证：确认数据确实流到了音频引擎（位置推进、时长走完），
/// 而不是静默失败。无输出设备的机器上跳过。
/// </summary>
public class PlaybackVerificationTests
{
    private readonly ITestOutputHelper _out;
    public PlaybackVerificationTests(ITestOutputHelper output) => _out = output;

    private static float[] Sine(int sampleRate, int seconds, double amplitude = 0.3)
    {
        var data = new float[sampleRate * seconds * 2];
        for (int i = 0; i < data.Length / 2; i++)
        {
            float v = (float)(amplitude * System.Math.Sin(2 * System.Math.PI * 440 * i / sampleRate));
            data[i * 2] = v;
            data[i * 2 + 1] = v;
        }
        return data;
    }

    [Fact]
    public void Play_StartsAndPositionAdvances()
    {
        using var svc = new AudioPlaybackService();
        var devices = svc.EnumerateDevices();
        _out.WriteLine($"输出设备数: {devices.Count}");
        foreach (var d in devices) _out.WriteLine($"  - {d.Name}");
        if (devices.Count == 0) return;   // 无设备环境跳过

        double lastPosition = -1;
        int ticks = 0;
        double lastL = 0, lastR = 0;
        svc.PositionChanged += p => { lastPosition = p; Interlocked.Increment(ref ticks); };
        svc.LevelsChanged += (l, r) => { lastL = System.Math.Max(lastL, l); lastR = System.Math.Max(lastR, r); };

        var started = new ManualResetEventSlim(false);
        string? error = "not-called";
        svc.Play(Sine(48000, 2), 48000, 2, null, 0, err => { error = err; started.Set(); });

        Assert.True(started.Wait(10000), "Play 回调超时");
        _out.WriteLine($"onStarted error = {error ?? "(null)"}");
        Assert.True(error == null, $"播放启动失败：{error}");
        Assert.True(svc.IsPlaying, "Play 后应处于播放状态");

        Thread.Sleep(1200);
        _out.WriteLine($"ticks={ticks} lastPosition={lastPosition:0.###} peakL={lastL:0.###} peakR={lastR:0.###}");

        Assert.True(ticks > 0, "没有任何播放块回调：数据未流向设备");
        Assert.True(lastPosition > 0.3, $"播放位置未推进：{lastPosition}");
        Assert.True(lastL > 0.1 && lastR > 0.1, $"电平为静音：L={lastL} R={lastR}");

        svc.Stop(immediate: true);
        Assert.False(svc.IsPlaying);
    }

    [Fact]
    public void Play_NaturalEndRaisesStopped()
    {
        using var svc = new AudioPlaybackService();
        if (svc.EnumerateDevices().Count == 0) return;

        int stopped = 0;
        svc.PlaybackStopped += () => Interlocked.Increment(ref stopped);

        var started = new ManualResetEventSlim(false);
        svc.Play(Sine(48000, 1), 48000, 2, null, 0, _ => started.Set());
        Assert.True(started.Wait(10000));

        // 1 秒素材，等待自然结束
        var deadline = System.DateTime.UtcNow.AddSeconds(12);
        while (System.DateTime.UtcNow < deadline && Interlocked.CompareExchange(ref stopped, 0, 0) == 0)
            Thread.Sleep(100);

        Assert.True(stopped > 0, "播放结束后未收到停止通知");
        Assert.False(svc.IsPlaying);
    }
}
