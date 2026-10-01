using System.IO;
using GunMix.Core.Assets;
using GunMix.Core.Audio;
using GunMix.Core.Model;
using GunMix.Core.Mixing;
using GunMix.Core.Timeline;
using Xunit;

namespace GunMix.Core.Tests;

public class MixKernelTests
{
    private static AssetBuffer Buffer(float[] data, int channels, int rate = 48000) =>
        new() { Data = data, Channels = channels, SampleRate = rate, FrameCount = data.Length / channels };

    [Fact]
    public void MonoIsDuplicatedToBothChannels()
    {
        var mono = new float[] { 0.25f, -0.5f };
        var buffer = Buffer(mono, 1);
        var layer = new Layer { Name = "L", Enabled = true, GainDb = 0 };
        var asset = new AssetInfo { Format = new AudioFormatInfo(48000, 16, 1, false, 2) };
        var timeline = new CompiledTimeline
        {
            Events = [new ShotEvent { LayerId = layer.Id, AssetId = asset.Id, StartSample = 0, GainDb = 0 }],
            TotalSamples = 2,
            AssetById = new Dictionary<Guid, AssetInfo> { [asset.Id] = asset },
        };
        var result = MixKernel.Render(timeline, _ => buffer);
        Assert.Equal(0.25f, result.Data[0]);
        Assert.Equal(0.25f, result.Data[1]);
        Assert.Equal(-0.5f, result.Data[2]);
        Assert.Equal(-0.5f, result.Data[3]);
    }

    [Fact]
    public void StereoKeepsChannelsAsIs()
    {
        var stereo = new float[] { 0.25f, -0.5f };
        var buffer = Buffer(stereo, 2);
        var asset = new AssetInfo { Format = new AudioFormatInfo(48000, 16, 2, false, 1) };
        var timeline = new CompiledTimeline
        {
            Events = [new ShotEvent { AssetId = asset.Id, StartSample = 0, GainDb = 0 }],
            TotalSamples = 1,
            AssetById = new Dictionary<Guid, AssetInfo> { [asset.Id] = asset },
        };
        var result = MixKernel.Render(timeline, _ => buffer);
        Assert.Equal(0.25f, result.Data[0]);
        Assert.Equal(-0.5f, result.Data[1]);
    }

    [Fact]
    public void GainAppliedLinearly()
    {
        var mono = new float[] { 1.0f };
        var buffer = Buffer(mono, 1);
        var asset = new AssetInfo { Format = new AudioFormatInfo(48000, 16, 1, false, 1) };
        var timeline = new CompiledTimeline
        {
            Events = [new ShotEvent { AssetId = asset.Id, StartSample = 0, GainDb = -6.0206 }],
            TotalSamples = 1,
            AssetById = new Dictionary<Guid, AssetInfo> { [asset.Id] = asset },
        };
        var result = MixKernel.Render(timeline, _ => buffer);
        Assert.Equal(0.5, result.Data[0], 4); // -6 dB ≈ ×0.5
    }

    [Fact]
    public void NewShotDoesNotTruncateOldInstance()
    {
        // 两发同素材重叠：第二发起点处旧实例仍在继续，不允许截断
        var src = new float[10];
        for (int i = 0; i < 10; i++) src[i] = 0.1f * (i + 1);
        var buffer = Buffer(src, 1);
        var asset = new AssetInfo { Format = new AudioFormatInfo(48000, 16, 1, false, 10) };
        var timeline = new CompiledTimeline
        {
            Events =
            [
                new ShotEvent { AssetId = asset.Id, StartSample = 0, GainDb = 0 },
                new ShotEvent { AssetId = asset.Id, StartSample = 5, GainDb = 0 },
            ],
            TotalSamples = 15,
            AssetById = new Dictionary<Guid, AssetInfo> { [asset.Id] = asset },
        };
        var result = MixKernel.Render(timeline, _ => buffer);
        // 采样 5：旧实例 0.6 + 新实例 0.1
        Assert.Equal(0.1f * 6 + 0.1f, result.Data[5 * 2], 4);
        // 采样 10–14：只有第二发的尾部 0.6..1.0
        Assert.Equal(0.1f * 6, result.Data[10 * 2], 4);
        Assert.Equal(0.1f * 10, result.Data[14 * 2], 4);
        Assert.Equal(15, result.Frames); // 尾部保留
    }

    [Fact]
    public void PeakComputedFromActualMix()
    {
        var src = new float[] { 0.6f };
        var buffer = Buffer(src, 1);
        var asset = new AssetInfo { Format = new AudioFormatInfo(48000, 16, 1, false, 1) };
        var timeline = new CompiledTimeline
        {
            Events =
            [
                new ShotEvent { AssetId = asset.Id, StartSample = 0, GainDb = 0 },
                new ShotEvent { AssetId = asset.Id, StartSample = 0, GainDb = 0 },
            ],
            TotalSamples = 1,
            AssetById = new Dictionary<Guid, AssetInfo> { [asset.Id] = asset },
        };
        var result = MixKernel.Render(timeline, _ => buffer);
        Assert.Equal(1.2, result.Peak, 4);
        Assert.True(result.ExceedsFullScale);
    }

    [Fact]
    public void MissingAssetsReported()
    {
        var asset = new AssetInfo { FileName = "gone.wav", Format = new AudioFormatInfo(48000, 16, 1, false, 1) };
        var timeline = new CompiledTimeline
        {
            Events = [new ShotEvent { AssetId = asset.Id, StartSample = 0, GainDb = 0 }],
            TotalSamples = 1,
            AssetById = new Dictionary<Guid, AssetInfo> { [asset.Id] = asset },
        };
        var result = MixKernel.Render(timeline, _ => null);
        Assert.Contains("gone.wav", result.MissingAssets);
    }
}

public class TailTrimmerTests
{
    [Fact]
    public void Trim_RemovesTrailingSilence_KeepsTail()
    {
        // 前 0.1 秒有信号，其后 2 秒静音：截断到 0.1s + 120ms 尾
        int sr = 48000;
        var data = new float[sr * 3 * 2];
        for (int i = 0; i < sr / 10 * 2; i++) data[i] = 0.5f;
        var result = TailTrimmer.Trim(data, 2, sr, -60, 120);
        Assert.True(result.DidTrim);
        Assert.Equal(sr / 10 + sr * 120 / 1000, result.KeptFrames);
        Assert.Equal(data.Length - result.RemovedFrames * 2, result.Data.Length);
    }

    [Fact]
    public void Trim_NoTrailingSilence_Unchanged()
    {
        int sr = 48000;
        var data = new float[sr * 2];
        for (int i = 0; i < data.Length; i++) data[i] = 0.5f; // 满幅到最后
        var result = TailTrimmer.Trim(data, 2, sr, -60, 120);
        Assert.False(result.DidTrim);
        Assert.Same(data, result.Data);
    }

    [Fact]
    public void Trim_AllSilent_KeepsOnlyTail()
    {
        int sr = 48000;
        var data = new float[sr * 2 * 2]; // 2 秒全静音
        var result = TailTrimmer.Trim(data, 2, sr, -60, 120);
        Assert.True(result.DidTrim);
        Assert.Equal(sr * 120 / 1000, result.KeptFrames);
    }

    [Fact]
    public void Trim_ThresholdRespected()
    {
        // -50 dBFS 的慢衰减尾：阈值 -60 时该尾高于阈值，不应被截掉
        int sr = 48000;
        var data = new float[sr * 2];
        float v = (float)Math.Pow(10, -50 / 20.0); // ≈0.00316 > 阈值 0.001
        for (int i = 0; i < data.Length; i++) data[i] = v;
        var result = TailTrimmer.Trim(data, 2, sr, -60, 120);
        Assert.False(result.DidTrim);
    }
}

public class AssetServiceTests : IDisposable
{
    private readonly string _dir;

    public AssetServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gunmix_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败忽略 */ }
    }

    private static void WriteTestWav(string path, int frames = 100, int channels = 1)
    {
        var data = new float[frames * channels];
        for (int i = 0; i < data.Length; i++) data[i] = (float)(0.1 * Math.Sin(i));
        WavWriter.Write(path, 48000, 16, false, channels, Quantizer.ToInt16Bytes(Quantizer.ToInt16Legacy(data)));
    }

    [Fact]
    public void ImportScansGroupsAndHashes()
    {
        WriteTestWav(Path.Combine(_dir, "weap_rex_mike4_fire_plr_shot_01.qnn.85.48000.all.wav"));
        WriteTestWav(Path.Combine(_dir, "weap_rex_mike4_fire_plr_shot_02.qnn.85.48000.all.wav"));
        WriteTestWav(Path.Combine(_dir, "weap_rex_mike4_fire_plr_mech_01.qnn.85.48000.all.wav"));
        File.WriteAllText(Path.Combine(_dir, "not_wav.wav"), "garbage");

        var service = new AssetService();
        var report = service.ImportDirectory(_dir, includeSubdirectories: false);

        Assert.Equal(4, report.TotalFiles);
        Assert.Equal(3, report.Assets.Count);
        Assert.Single(report.Failures); // 损坏文件进入失败清单，不使全部导入失败
        Assert.All(report.Assets, a => Assert.Equal(64, a.Sha256.Length));
        Assert.Equal(2, report.Assets.Count(a => a.GroupKey == "fire_plr_shot"));
        Assert.Contains("mike4", report.WeaponsFound);
    }

    [Fact]
    public void ManualGroupingOverrides()
    {
        WriteTestWav(Path.Combine(_dir, "weap_rex_mike4_fire_plr_shot_01.qnn.85.48000.all.wav"));
        var service = new AssetService();
        var overrides = new Dictionary<string, string> { ["weap_rex_mike4_fire_plr_shot_01.qnn.85.48000.all.wav"] = "my_custom_group" };
        var report = service.ImportDirectory(_dir, includeSubdirectories: false, manualGroupOverrides: overrides);
        Assert.Equal("my_custom_group", report.Assets[0].GroupKey);
        Assert.Equal(GroupSource.Manual, report.Assets[0].GroupSource);
    }

    [Fact]
    public void RelocateFindsByHash()
    {
        var sub = Path.Combine(_dir, "moved", "deeper");
        Directory.CreateDirectory(sub);
        var wavPath = Path.Combine(sub, "weap_rex_mike4_fire_plr_shot_01.qnn.85.48000.all.wav");
        WriteTestWav(wavPath);

        var asset = new AssetInfo
        {
            SourceDirectory = @"X:\original",
            FileName = "weap_rex_mike4_fire_plr_shot_01.qnn.85.48000.all.wav",
            Sha256 = AssetService.ComputeHash(wavPath),
        };
        var found = AssetService.Relocate(asset, _dir, "", out var conflict);
        Assert.NotNull(found);
        Assert.False(conflict); // 哈希一致

        // 同名但内容不同不得自动替代
        File.WriteAllText(Path.Combine(_dir, "同名不同内容.wav"), "different");
        var asset2 = new AssetInfo
        {
            SourceDirectory = @"X:\original",
            FileName = "同名不同内容.wav",
            Sha256 = "deadbeef",
        };
        var found2 = AssetService.Relocate(asset2, _dir, "", out var conflict2);
        Assert.True(conflict2);
    }

    [Fact]
    public void PoolResolutionByGroupKeySortsVariants()
    {
        foreach (var n in new[] { "02", "01", "10" })
            WriteTestWav(Path.Combine(_dir, $"weap_rex_mike4_fire_plr_shot_{n}.qnn.85.48000.all.wav"));
        var service = new AssetService();
        var report = service.ImportDirectory(_dir, includeSubdirectories: false);
        var weaponId = report.Assets[0].WeaponId = report.Assets[0].WeaponId == Guid.Empty
            ? Guid.NewGuid() : report.Assets[0].WeaponId;
        foreach (var a in report.Assets) a.WeaponId = weaponId;

        var layer = new Layer { PoolGroupKey = "fire_plr_shot" };
        var pool = AssetService.ResolvePool(layer, report.Assets, weaponId);
        Assert.Equal(3, pool.Count);
        Assert.Equal("01", pool[0].Parsed!.Variant!.Value.ToString("00"));
        Assert.Equal("02", pool[1].Parsed!.Variant!.Value.ToString("00"));
        Assert.Equal("10", pool[2].Parsed!.Variant!.Value.ToString("00"));
    }
}
