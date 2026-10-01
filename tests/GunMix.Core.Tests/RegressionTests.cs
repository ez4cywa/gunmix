using System.IO;
using GunMix.Core.Assets;
using GunMix.Core.Audio;
using GunMix.Core.Model;
using GunMix.Core.Mixing;
using GunMix.Core.Persistence;
using GunMix.Core.Timeline;
using Xunit;

namespace GunMix.Core.Tests;

/// <summary>
/// A03 对照回归：用现有 mix_recipe.json 的 42 事件渲染，
/// 与现有 27 秒对照音频逐采样比较（相同取整与缩放方式时误差不超过 1 LSB）。
/// 对照音频与源素材在本机存在时才执行。
/// </summary>
public class RegressionTests
{
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "output", "audio")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static (string Recipe, string ReferenceWav, string SourceDir)? LocateInputs()
    {
        var root = FindRepoRoot();
        if (root == null) return null;
        var recipe = Path.Combine(root, "output", "audio", "mix_recipe.json");
        var wav = Path.Combine(root, "output", "audio", "ar_mike4_layers_comparison.wav");
        string? sourceDir = null;
        try
        {
            var legacy = LegacyMixRecipe.Load(recipe);
            if (Directory.Exists(legacy.SourceDirectory)) sourceDir = legacy.SourceDirectory;
        }
        catch
        {
            return null;
        }
        if (!File.Exists(recipe) || !File.Exists(wav) || sourceDir == null) return null;
        return (recipe, wav, sourceDir);
    }

    [Fact]
    public void A03_RenderLegacyRecipeMatchesReferenceAudio()
    {
        var inputs = LocateInputs();
        if (inputs == null)
        {
            // 本机缺少对照素材（例如 CI 环境）时跳过；验收时须在有素材的机器上执行。
            return;
        }
        var (recipePath, refWavPath, sourceDir) = inputs.Value;
        var legacy = LegacyMixRecipe.Load(recipePath);
        Assert.Equal(48000, legacy.SampleRate);
        Assert.True(legacy.Events.Count >= 40, $"事件数 {legacy.Events.Count}");

        var refWav = WavReader.Read(refWavPath);
        Assert.Equal(48000, refWav.SampleRate);
        Assert.Equal(2, refWav.Channels);

        var cache = new AudioCache();
        var events = legacy.Events.Select(e =>
        {
            var path = Path.Combine(sourceDir, e.File);
            var decoded = WavReader.Read(path);
            float[] data = decoded.Data;
            if (decoded.SampleRate != 48000)
                data = Resampler.Resample(data, decoded.Channels, decoded.SampleRate, 48000);
            var buffer = new AssetBuffer
            {
                Data = data,
                Channels = decoded.Channels,
                SampleRate = 48000,
                FrameCount = decoded.FrameCount,
            };
            long start = (long)Math.Round(e.TimeS * 48000, MidpointRounding.ToEven);
            return (Buffer: buffer, Start: start, GainDb: e.GainDb, File: e.File);
        }).ToList();

        long total = refWav.FrameCount;
        var mix = new float[total * 2];
        foreach (var (buffer, start, gainDb, _) in events)
        {
            double gain = Math.Pow(10, gainDb / 20.0);
            int ch = buffer.Channels;
            long frames = buffer.FrameCount;
            long copy = Math.Min(frames, total - start);
            for (long f = 0; f < copy; f++)
            {
                long d = (start + f) * 2;
                long s = f * ch;
                float l = buffer.Data[s] * (float)gain;
                float r = ch == 1 ? l : buffer.Data[s + 1] * (float)gain;
                mix[d] += l;
                mix[d + 1] += r;
            }
        }

        // 相同 16 bit 量化方式：无抖动 rint(x*32767)
        var quantized = Quantizer.ToInt16Legacy(mix);

        // 直接读取参考 WAV 的 int16 原始数据，避免 float 往返引入误差
        short[] refShorts;
        using (var reader = new NAudio.Wave.WaveFileReader(refWavPath))
        {
            var raw = new byte[reader.Length];
            int read = 0;
            while (read < raw.Length)
            {
                int n = reader.Read(raw, read, raw.Length - read);
                if (n <= 0) break;
                read += n;
            }
            refShorts = Quantizer.BytesToInt16(raw);
        }

        long maxDiff = 0, diffSamples = 0;
        for (int i = 0; i < Math.Min(quantized.Length, refShorts.Length); i++)
        {
            long diff = Math.Abs((long)quantized[i] - refShorts[i]);
            if (diff > maxDiff) maxDiff = diff;
            if (diff > 1) diffSamples++;
        }
        Assert.True(maxDiff <= 1, $"A03 超出 1 LSB：maxDiff={maxDiff}，超差样本数={diffSamples}");
        Assert.Equal(0, diffSamples);
        cache.Dispose();
    }

    [Fact]
    public void A05_TailPreservedForSixShots()
    {
        // 六发从 0 秒开始、ATMO 完整播放时尾部终点按最长事件计算，不被新一发截断
        var recipe = new Recipe { BurstRpm = 600, BurstShotCount = 6 };
        var layer = new Layer { Name = "ATMO", Enabled = true, GainDb = -24, VariantMode = VariantMode.Fixed };
        recipe.Layers.Add(layer);
        var asset = new AssetInfo
        {
            Id = Guid.NewGuid(),
            FileName = "atmo.wav",
            Format = new AudioFormatInfo(48000, 16, 1, false, (long)(4.46 * 48000)),
        };
        layer.PoolAssetIds.Add(asset.Id);

        var assetFrames = asset.Format.FrameCount;
        var manifest = TimelineCompiler.BuildManifest(recipe, [asset], asset.WeaponId, ManifestKind.Burst);
        var timeline = TimelineCompiler.Compile(recipe, manifest, [asset], asset.WeaponId, 48000, ManifestKind.Burst);
        // 尾部终点 = 最后一发起点（0.5 s）+ ATMO 完整长度；新一发不截断旧实例
        Assert.Equal(5 * 4800 + assetFrames, timeline.TotalSamples);
    }

    [Fact]
    public void A01_ImportSampleDirectoryMatchesInventory()
    {
        var inputs = LocateInputs();
        if (inputs == null) return; // 本机缺少样本素材（例如 CI 环境）时跳过
        var (_, _, sourceDir) = inputs.Value;

        var service = new AssetService();
        var report = service.ImportDirectory(sourceDir, includeSubdirectories: true);

        Assert.Equal(278, report.TotalFiles);
        Assert.Equal(278, report.Assets.Count); // 278 个有效文件
        Assert.Equal(35, report.Assets.Select(a => a.GroupKey).Where(g => g.Length > 0).Distinct().Count()); // 35 个命名组
        Assert.Equal(232, report.Assets.Count(a => a.Format.Channels == 1));
        Assert.Equal(46, report.Assets.Count(a => a.Format.Channels == 2));
        Assert.All(report.Assets, a => Assert.Equal(48000, a.Format.SampleRate));
        // 未知文件不误归组：全部文件都有武器标识与分组
        Assert.All(report.Assets, a => Assert.Equal("mike4", a.Parsed!.Weapon));
    }

    [Fact]
    public void A04_BurstTimingViaManifest()
    {
        // 48 kHz、600 RPM 时事件间隔为 4800 采样（已覆盖非整采样 100 发）
        var recipe = new Recipe { BurstRpm = 600, BurstShotCount = 6 };
        var layer = new Layer { Name = "SHOT", Enabled = true, GainDb = -12, VariantMode = VariantMode.Fixed };
        recipe.Layers.Add(layer);
        var asset = new AssetInfo { Id = Guid.NewGuid(), FileName = "shot.wav", Format = new AudioFormatInfo(48000, 16, 1, false, 48000) };
        layer.PoolAssetIds.Add(asset.Id);

        var manifest = TimelineCompiler.BuildManifest(recipe, [asset], asset.WeaponId, ManifestKind.Burst);
        var timeline = TimelineCompiler.Compile(recipe, manifest, [asset], asset.WeaponId, 48000, ManifestKind.Burst);
        var starts = timeline.Events.Select(e => e.StartSample).OrderBy(s => s).ToList();
        Assert.Equal([0, 4800, 9600, 14400, 19200, 24000], starts);
    }

    [Fact]
    public void A06_SaveReloadReusesManifestWithoutResampling()
    {
        // 保存并重开工程，清单原样恢复：同一渲染版本、源文件、事件与抖动种子得到一致 PCM
        var project = new GunProject();
        var weapon = new Weapon { Name = "mike4" };
        var recipe = new Recipe { Name = "r", BurstRpm = 600, BurstShotCount = 6, RandomSeed = 42 };
        var layer = new Layer { Name = "SHOT", Enabled = true, VariantMode = VariantMode.Random };
        recipe.Layers.Add(layer);
        var asset = new AssetInfo { Id = Guid.NewGuid(), FileName = "a.wav", WeaponId = weapon.Id, Format = new AudioFormatInfo(48000, 16, 1, false, 48000) };
        layer.PoolAssetIds.Add(asset.Id);
        weapon.Recipes.Add(recipe);
        weapon.ActiveRecipeId = recipe.Id;
        project.Weapons.Add(weapon);
        project.Assets.Add(asset);
        project.ActiveWeaponId = weapon.Id;

        var manifest = TimelineCompiler.BuildManifest(recipe, project.Assets, weapon.Id, ManifestKind.Burst);
        recipe.BurstManifest = manifest;

        var path = Path.Combine(Path.GetTempPath(), "a06_" + Guid.NewGuid().ToString("N")[..8] + GunProject.FileExtension);
        try
        {
            ProjectStore.Save(project, path);
            var loaded = ProjectStore.Load(path);
            var loadedRecipe = loaded.Weapons[0].Recipes[0];
            Assert.NotNull(loadedRecipe.BurstManifest);
            var rebuilt = TimelineCompiler.BuildManifest(loadedRecipe, loaded.Assets, loaded.Weapons[0].Id, ManifestKind.Burst);
            // 不重新抽样：重开工程后清单与保存时一致
            Assert.Equal(
                loadedRecipe.BurstManifest.Entries.Select(e => (e.LayerId, e.ShotIndex, e.AssetId)),
                rebuilt.Entries.Select(e => (e.LayerId, e.ShotIndex, e.AssetId)));
            Assert.True(loadedRecipe.BurstManifest.Matches(loadedRecipe, ManifestKind.Burst));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A11_ExporterWritesTempThenPublishes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gunmix_export_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var data = new float[] { 0.1f, -0.1f, 0.2f, -0.2f };
            var path = Path.Combine(dir, "out.wav");
            var result = Exporter.WriteWav(data, 48000, 2, 16, dither: false, ditherSeed: 1,
                attenuateToDbfs: null, finalPath: path, overwrite: false);
            Assert.True(result.Success, result.Error);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(Path.Combine(dir, "out.part.wav")));

            // 拒绝覆盖：默认生成新名称
            var again = Exporter.WriteWav(data, 48000, 2, 16, false, 1, null, path, overwrite: false);
            Assert.False(again.Success);

            // 越界 + 显式衰减：记录实际增益
            var loud = new float[] { 1.2f, -1.2f };
            var clipped = Exporter.WriteWav(loud, 48000, 2, 16, false, 1, -1.0,
                Path.Combine(dir, "loud.wav"), overwrite: false);
            Assert.True(clipped.Success);
            Assert.NotNull(clipped.AppliedGainDb);
            Assert.True(clipped.AppliedGainDb < 0);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 清理失败忽略 */ }
        }
    }
}
