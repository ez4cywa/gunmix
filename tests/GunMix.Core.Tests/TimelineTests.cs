using GunMix.Core.Model;
using GunMix.Core.Timeline;
using Xunit;

namespace GunMix.Core.Tests;

public class TimelineTests
{
    private static Recipe MakeRecipe()
    {
        var layer = new Layer
        {
            Name = "SHOT",
            Role = LayerRoles.Shot,
            Enabled = true,
            GainDb = -12,
            PoolGroupKey = "fire_plr_shot",
        };
        var recipe = new Recipe { Name = "测试", BurstRpm = 600, BurstShotCount = 6 };
        recipe.Layers.Add(layer);
        return recipe;
    }

    private static List<AssetInfo> MakePool(Layer layer, Recipe recipe, int count = 10)
    {
        var weaponId = Guid.NewGuid();
        var pool = new List<AssetInfo>();
        for (int i = 1; i <= count; i++)
        {
            var a = new AssetInfo
            {
                WeaponId = weaponId,
                GroupKey = "fire_plr_shot",
                Format = new AudioFormatInfo(48000, 16, 1, false, 48000),
                FileName = $"weap_rex_mike4_fire_plr_shot_{i:00}.qnn.85.48000.all.wav",
            };
            pool.Add(a);
            layer.PoolAssetIds.Add(a.Id);
        }
        return pool;
    }

    [Fact]
    public void Burst600Rpm48kEventsSpaced4800Samples()
    {
        var recipe = MakeRecipe();
        var pool = MakePool(recipe.Layers[0], recipe);
        var manifest = TimelineCompiler.BuildManifest(recipe, pool, pool[0].WeaponId, ManifestKind.Burst);
        var timeline = TimelineCompiler.Compile(recipe, manifest, pool, pool[0].WeaponId, 48000, ManifestKind.Burst);

        var starts = timeline.Events.OrderBy(e => e.StartSample).Select(e => e.StartSample).Distinct().ToList();
        Assert.Equal(6, starts.Count);
        for (int i = 0; i < 6; i++)
            Assert.Equal(4800L * i, starts[i]); // 600 RPM、48 kHz 下每发间隔 4800 采样
    }

    [Fact]
    public void Burst100ShotsAbsoluteTimeDeviationUnderOneSample()
    {
        var recipe = MakeRecipe();
        recipe.BurstRpm = 977; // 非整采样间隔：60/977 s
        recipe.BurstShotCount = 100;
        var pool = MakePool(recipe.Layers[0], recipe);
        var manifest = TimelineCompiler.BuildManifest(recipe, pool, pool[0].WeaponId, ManifestKind.Burst);
        var timeline = TimelineCompiler.Compile(recipe, manifest, pool, pool[0].WeaponId, 48000, ManifestKind.Burst);

        double interval = 60.0 / 977;
        foreach (var e in timeline.Events)
        {
            double ideal = e.ShotIndex * interval * 48000;
            Assert.InRange(e.StartSample, ideal - 0.5, ideal + 0.5); // 绝对时间计算，无累计偏差
        }
    }

    [Fact]
    public void ReleaseMomentIsLastStartPlusInterval()
    {
        // 六发 600 RPM 从 0 秒开始，起点 0–0.5 秒，释放时刻 0.6 秒
        var recipe = MakeRecipe();
        long release = TimelineCompiler.ReleaseMomentSample(recipe, ManifestKind.Burst, 48000);
        Assert.Equal(28800, release); // 0.6 s
    }

    [Fact]
    public void LayerDelayShiftsEvents()
    {
        var recipe = MakeRecipe();
        recipe.Layers[0].DelayMs = 12.5; // 600 采样
        var pool = MakePool(recipe.Layers[0], recipe);
        var manifest = TimelineCompiler.BuildManifest(recipe, pool, pool[0].WeaponId, ManifestKind.Burst);
        var timeline = TimelineCompiler.Compile(recipe, manifest, pool, pool[0].WeaponId, 48000, ManifestKind.Burst);
        Assert.All(timeline.Events, e => Assert.Equal(600 + 4800L * e.ShotIndex, e.StartSample));
    }

    [Fact]
    public void SingleShotManifestUsesFirstPoolEntryForRotation()
    {
        var recipe = MakeRecipe();
        recipe.Layers[0].VariantMode = VariantMode.Rotation;
        var pool = MakePool(recipe.Layers[0], recipe);
        var manifest = TimelineCompiler.BuildManifest(recipe, pool, pool[0].WeaponId, ManifestKind.Single);
        var entry = Assert.Single(manifest.Entries);
        Assert.Equal(pool[0].Id, entry.AssetId);
    }

    [Fact]
    public void RotationCyclesInOrder()
    {
        var recipe = MakeRecipe();
        recipe.BurstShotCount = 12;
        recipe.Layers[0].VariantMode = VariantMode.Rotation;
        var pool = MakePool(recipe.Layers[0], recipe, count: 10);
        var manifest = TimelineCompiler.BuildManifest(recipe, pool, pool[0].WeaponId, ManifestKind.Burst);
        var picks = manifest.Entries.OrderBy(e => e.ShotIndex).Select(e => e.AssetId).ToList();
        for (int i = 0; i < 12; i++)
            Assert.Equal(pool[i % 10].Id, picks[i]);
    }

    [Fact]
    public void RandomIsReproducibleAndNeverConsecutiveRepeat()
    {
        var recipe = MakeRecipe();
        recipe.BurstShotCount = 100;
        recipe.RandomSeed = 42;
        recipe.Layers[0].VariantMode = VariantMode.Random;
        var pool = MakePool(recipe.Layers[0], recipe, count: 5);

        var manifest1 = TimelineCompiler.BuildManifest(recipe, pool, pool[0].WeaponId, ManifestKind.Burst);
        var manifest2 = TimelineCompiler.BuildManifest(recipe, pool, pool[0].WeaponId, ManifestKind.Burst);
        var picks1 = manifest1.Entries.OrderBy(e => e.ShotIndex).Select(e => e.AssetId).ToList();
        var picks2 = manifest2.Entries.OrderBy(e => e.ShotIndex).Select(e => e.AssetId).ToList();
        Assert.Equal(picks1, picks2); // 同种子可重现

        for (int i = 1; i < picks1.Count; i++)
            Assert.NotEqual(picks1[i - 1], picks1[i]); // 池内不连续重复
    }

    [Fact]
    public void ExperimentalShotNOnlyFiresOnThatShot()
    {
        var recipe = MakeRecipe();
        recipe.Layers[0].IsExperimental = true;
        recipe.Layers[0].Trigger = ExperimentalTrigger.ShotN;
        recipe.Layers[0].TriggerShotNumber = 3;
        var pool = MakePool(recipe.Layers[0], recipe);
        var manifest = TimelineCompiler.BuildManifest(recipe, pool, pool[0].WeaponId, ManifestKind.Burst);
        var entry = Assert.Single(manifest.Entries);
        Assert.Equal(2, entry.ShotIndex);
    }

    [Fact]
    public void DisabledLayersHaveNoEvents()
    {
        var recipe = MakeRecipe();
        recipe.Layers[0].Enabled = false;
        var pool = MakePool(recipe.Layers[0], recipe);
        var manifest = TimelineCompiler.BuildManifest(recipe, pool, pool[0].WeaponId, ManifestKind.Burst);
        Assert.Empty(manifest.Entries);
    }

    [Fact]
    public void ManifestInvalidatedWhenShotCountChanges()
    {
        var recipe = MakeRecipe();
        var pool = MakePool(recipe.Layers[0], recipe);
        var manifest = TimelineCompiler.BuildManifest(recipe, pool, pool[0].WeaponId, ManifestKind.Burst);
        Assert.True(manifest.Matches(recipe, ManifestKind.Burst));
        recipe.BurstShotCount = 10; // 改发数使清单失效
        Assert.False(manifest.Matches(recipe, ManifestKind.Burst));
    }

    [Fact]
    public void InvalidRpmAndShotCountReported()
    {
        var recipe = MakeRecipe();
        recipe.BurstRpm = 5000;
        recipe.BurstShotCount = 200;
        var pool = MakePool(recipe.Layers[0], recipe);
        var manifest = TimelineCompiler.BuildManifest(recipe, pool, pool[0].WeaponId, ManifestKind.Burst);
        var timeline = TimelineCompiler.Compile(recipe, manifest, pool, pool[0].WeaponId, 48000, ManifestKind.Burst);
        Assert.Contains(timeline.Issues, i => i.Message.Contains("射速"));
        Assert.Contains(timeline.Issues, i => i.Message.Contains("发数"));
    }
}
