using System.IO;
using GunMix.Core.Assets;
using GunMix.Core.Audio;
using GunMix.Core.Mixing;
using GunMix.Core.Model;
using GunMix.Core.Persistence;
using GunMix.Core.Synthesis;
using GunMix.Core.Timeline;
using Xunit;

namespace GunMix.Core.Tests;

/// <summary>多武器适配：命名解析 v2、枪型判定、自适应配方配平、连发实例上限。</summary>
public class SynthesisTests
{
    // 真实素材路径由环境变量 GUNMIX_ASSETS_ROOT 提供；未设置时相关测试自动跳过。
    private static string? WpnRoot => TestPaths.WpnRoot;

    // ───────────── 命名与枪型 ─────────────

    [Theory]
    [InlineData("weap_findia_fcg_prefire_plr_ads_03.vnn.85.48000.all.wav", "findia", "fcg", true)]
    [InlineData("weap_psierra41_fire_plr_shot_01.qnn.85.48000.all.wav", "psierra41", "fire_plr_shot", false)]
    [InlineData("weap_rex_psierra41_fire_plr_atmo_02.qnn.85.48000.all.wav", "psierra41", "fire_plr_atmo", false)]
    [InlineData("weap_rex_hotel45_fire_plr_fcg_ads_02.qnn.85.48000.all.wav", "hotel45", "fire_plr_fcg_ads", true)]
    [InlineData("wpn_rex_findia_apex_knives_lift_cloth.lnn.85.48000.all.wav", "findia", "apex_knives_lift_cloth", false)]
    public void ParsesPrefixVariantsAcrossWeapons(string file, string weapon, string groupKeyStart, bool experimental)
    {
        var r = NameParser.Parse(file);
        Assert.Equal(weapon, r.Weapon);
        Assert.StartsWith(groupKeyStart, r.GroupKey);
        Assert.Equal(experimental, r.IsExperimental);
    }

    [Fact]
    public void FcgInsideFireGroupIsExperimentalBucket()
    {
        Assert.Equal("实验分支（未启用）", NameParser.BucketOf("fire_plr_fcg"));
        Assert.Equal("实验分支（未启用）", NameParser.BucketOf("fire_plr_fcg_ads"));
        Assert.Equal("ADS 候选", NameParser.BucketOf("fire_plr_mech_ads"));
    }

    [Theory]
    [InlineData("ar_mike4", "mike4", WeaponTypes.AssaultRifle)]
    [InlineData("dm_findia", "findia", WeaponTypes.Marksman)]
    [InlineData("lm_tango73", "tango73", WeaponTypes.MachineGun)]
    [InlineData("pi_hotel45", "hotel45", WeaponTypes.Pistol)]
    [InlineData("sm_psierra41", "psierra41", WeaponTypes.Smg)]
    [InlineData("sn_svictor98", "svictor98", WeaponTypes.Sniper)]
    [InlineData("rex_findia", "findia", null)]
    public void WeaponFolderGivesNameAndType(string folder, string weapon, string? type)
    {
        var r = NameParser.ParseWeaponFolder(folder);
        Assert.NotNull(r);
        Assert.Equal(weapon, r!.Value.Weapon);
        Assert.Equal(type, WeaponTypes.FromClassPrefix(r.Value.ClassPrefix));
    }

    [Fact]
    public void UnrelatedFolderIsNotAWeapon()
    {
        Assert.Null(NameParser.ParseWeaponFolder("reloads"));
        Assert.Null(NameParser.ParseWeaponFolder("wpn"));
    }

    // ───────────── 响度测量 ─────────────

    [Fact]
    public void LoudnessMeterFullScaleSine()
    {
        int n = 48000;
        var data = new float[n];
        for (int i = 0; i < n; i++) data[i] = (float)Math.Sin(2 * Math.PI * 1000 * i / 48000.0);
        var l = LoudnessMeter.Measure(data, 1, 48000);
        Assert.InRange(l.PeakDbfs, -0.01, 0.0);
        Assert.InRange(l.EarlyRmsDbfs, -3.02, -3.00); // 正弦 RMS = -3.01 dBFS
        Assert.InRange(l.Energy95Ms, 940, 960);
    }

    // ───────────── 自适应配方 ─────────────

    private static AssetInfo A(Guid weaponId, string group, double rms, int? variant = 1, int channels = 1) => new()
    {
        WeaponId = weaponId,
        GroupKey = group,
        FileName = $"weap_rex_test_{group}_{variant:00}.qnn.85.48000.all.wav",
        Parsed = NameParser.Parse($"weap_rex_test_{group}_{variant:00}.qnn.85.48000.all.wav"),
        Format = new AudioFormatInfo(48000, 16, channels, false, 48000),
        Loudness = new AssetLoudness(0, rms, 80),
    };

    [Fact]
    public void AdaptiveGainsFollowMeasuredLoudnessAndProfile()
    {
        var weapon = new Weapon { Name = "test", TypeName = WeaponTypes.AssaultRifle };
        var assets = new List<AssetInfo>
        {
            A(weapon.Id, "fire_plr_shot", -10, 1), A(weapon.Id, "fire_plr_shot", -10, 2),
            A(weapon.Id, "fire_plr_mech", -15, 1, 2),
            A(weapon.Id, "fire_plr_lfe", -7, null),
            A(weapon.Id, "sup_plr_shot", -13, 1),
        };
        var recipes = AdaptiveRecipeBuilder.Build(weapon, assets);
        Assert.Equal(["自适应·普通", "自适应·消音"], recipes.Select(r => r.Name));

        var normal = recipes[0];
        Assert.Equal(WeaponTypes.AssaultRifle, normal.AdaptiveProfile);
        Assert.Equal(750, normal.BurstRpm);
        Assert.Equal(-12.5, normal.Layers.Single(l => l.Role == LayerRoles.Shot).GainDb);   // -22.5 − (−10)
        Assert.Equal(-18.5, normal.Layers.Single(l => l.Role == LayerRoles.Mech).GainDb);   // -22.5 − 11 − (−15)
        Assert.Equal(-24.5, normal.Layers.Single(l => l.Role == LayerRoles.Low).GainDb);    // -22.5 − 9 − (−7)
        Assert.Equal(VariantMode.Fixed, normal.Layers.Single(l => l.Role == LayerRoles.Low).VariantMode); // 单文件池
        Assert.Contains("SWT", normal.Notes); // 缺失层写入说明

        var sup = recipes[1];
        Assert.Equal(-13.5, sup.Layers.Single(l => l.Role == LayerRoles.Shot).GainDb);      // -26.5 − (−13)
        var reused = sup.Layers.Single(l => l.Role == LayerRoles.Mech);
        Assert.Equal("fire_plr_mech", reused.PoolGroupKey);
        Assert.False(reused.Enabled);           // 跨状态复用默认关闭
        Assert.Contains("复用", reused.Name);
    }

    [Fact]
    public void SniperProfileUsesBoltCadenceAndNoVoiceLimit()
    {
        var weapon = new Weapon { Name = "svictor98", TypeName = WeaponTypes.Sniper };
        var assets = new List<AssetInfo>
        {
            A(weapon.Id, "fire_plr_shot", -5), A(weapon.Id, "fire_plr_mech", -5.5, 1, 2),
            A(weapon.Id, "fire_plr_atmo", -6.4, 1, 2),
        };
        var normal = AdaptiveRecipeBuilder.Build(weapon, assets).Single();
        Assert.Equal(45, normal.BurstRpm);
        Assert.Equal(3, normal.BurstShotCount);
        Assert.All(normal.Layers, l => Assert.Equal(0, l.BurstVoiceLimit));
        // 栓动 MECH 目标 -8 dB（高于步枪 -11），体现拉栓过程
        Assert.Equal(-20.5 - 8 + 5.5, normal.Layers.Single(l => l.Role == LayerRoles.Mech).GainDb);
    }

    [Fact]
    public void AutoProfileLimitsAtmoVoices()
    {
        var weapon = new Weapon { Name = "psierra41", TypeName = WeaponTypes.Smg };
        var assets = new List<AssetInfo> { A(weapon.Id, "fire_plr_shot", -14), A(weapon.Id, "fire_plr_atmo", -15, 1, 2) };
        var normal = AdaptiveRecipeBuilder.Build(weapon, assets).Single();
        Assert.Equal(3, normal.Layers.Single(l => l.Role == LayerRoles.Atmo).BurstVoiceLimit);
        Assert.Equal(0, normal.Layers.Single(l => l.Role == LayerRoles.Shot).BurstVoiceLimit);
    }

    [Fact]
    public void RegenerateReplacesOnlyAdaptiveRecipes()
    {
        var weapon = new Weapon { Name = "test", TypeName = WeaponTypes.AssaultRifle };
        var assets = new List<AssetInfo> { A(weapon.Id, "fire_plr_shot", -10) };
        ProjectFactory.SetupNewWeapon(weapon, assets);
        var user = new Recipe { Name = "我的配方" };
        weapon.Recipes.Add(user);
        int before = weapon.Recipes.Count;

        weapon.TypeName = WeaponTypes.Sniper;
        ProjectFactory.RegenerateAdaptive(weapon, assets);
        Assert.Equal(before, weapon.Recipes.Count);
        Assert.Contains(weapon.Recipes, r => r.Name == "我的配方");
        Assert.Equal(45, weapon.Recipes.Single(r => r.Name == "自适应·普通").BurstRpm);
    }

    // ───────────── 松扳机尾音 ─────────────

    [Fact]
    public void ReleaseTailOnlyInBurstAtReleaseMoment()
    {
        var weaponId = Guid.NewGuid();
        var shot = A(weaponId, "fire_plr_shot", -10);
        var tail = A(weaponId, "fire_plr_interrupt", -11);
        var recipe = new Recipe
        {
            BurstRpm = 600, BurstShotCount = 6,
            Layers = [new Layer { Name = "SHOT", Role = LayerRoles.Shot, PoolGroupKey = "fire_plr_shot" }],
        };
        var layer = ReleaseTail.Ensure(recipe, [shot, tail])!;
        Assert.Equal("fire_plr_interrupt", layer.PoolGroupKey);
        Assert.False(layer.Enabled); // 默认关闭，可选开启
        layer.Enabled = true;

        var burst = TimelineCompiler.BuildManifest(recipe, [shot, tail], weaponId, ManifestKind.Burst);
        var timeline = TimelineCompiler.Compile(recipe, burst, [shot, tail], weaponId, 48000, ManifestKind.Burst);
        var tailEvent = timeline.Events.Single(e => e.LayerId == layer.Id);
        Assert.Equal(6 * 4800, tailEvent.StartSample); // 末发 5×4800 + 一个间隔

        var single = TimelineCompiler.BuildManifest(recipe, [shot, tail], weaponId, ManifestKind.Single);
        Assert.DoesNotContain(single.Entries, e => e.LayerId == layer.Id);
        Assert.True(single.Matches(recipe, ManifestKind.Single)); // 不会因尾音层反复判定为过期
        Assert.True(burst.Matches(recipe, ManifestKind.Burst));
    }

    [Theory]
    [InlineData("sup_plr_shot", false, "sup_plr_interrupt")]
    [InlineData("fire_plr_shot", true, "fire_plr_interrupt_ads")]
    [InlineData("fire_npc_shot", false, null)]
    public void ReleaseTailGroupMatchesRecipeState(string shotGroup, bool ads, string? expected)
    {
        var recipe = new Recipe { Layers = [new Layer { Role = LayerRoles.Shot, PoolGroupKey = shotGroup }] };
        if (ads) recipe.Layers.Add(new Layer { Role = LayerRoles.Mech, PoolGroupKey = "fire_plr_mech_ads" });
        var groups = new[] { "fire_plr_interrupt", "fire_plr_interrupt_ads", "sup_plr_interrupt" };
        Assert.Equal(expected, ReleaseTail.CandidateGroup(recipe, groups));
    }

    [Fact]
    public void AdaptiveRecipesCarryDisabledReleaseTail()
    {
        var weapon = new Weapon { Name = "test", TypeName = WeaponTypes.AssaultRifle };
        var assets = new List<AssetInfo> { A(weapon.Id, "fire_plr_shot", -10), A(weapon.Id, "fire_plr_interrupt", -11) };
        var normal = AdaptiveRecipeBuilder.Build(weapon, assets).Single();
        var tail = ReleaseTail.Find(normal)!;
        Assert.False(tail.Enabled);
        Assert.Equal(-15.5, tail.GainDb); // 主体 -12.5 − 3
    }

    // ───────────── 连发实例上限 ─────────────

    [Fact]
    public void VoiceLimitFadesOldestInstance()
    {
        var weaponId = Guid.NewGuid();
        var asset = new AssetInfo
        {
            WeaponId = weaponId, GroupKey = "fire_plr_atmo",
            Format = new AudioFormatInfo(48000, 16, 1, false, 48000), // 1 秒
        };
        var layer = new Layer { Name = "ATMO", Role = LayerRoles.Atmo, PoolAssetIds = [asset.Id], BurstVoiceLimit = 3 };
        var recipe = new Recipe { BurstRpm = 600, BurstShotCount = 10, Layers = [layer] };
        var manifest = TimelineCompiler.BuildManifest(recipe, [asset], weaponId, ManifestKind.Burst);
        var timeline = TimelineCompiler.Compile(recipe, manifest, [asset], weaponId, 48000, ManifestKind.Burst);

        var events = timeline.Events.OrderBy(e => e.StartSample).ToList();
        for (int i = 0; i < 7; i++)
        {
            Assert.Equal(events[i + 3].StartSample, events[i].FadeOutStartSample); // 第 i+4 发起点开始淡出
            Assert.Equal(1440, events[i].FadeOutSamples);                          // 30 ms
        }
        Assert.All(events.Skip(7), e => Assert.Null(e.FadeOutStartSample));
        Assert.Equal(9 * 4800 + 48000, timeline.TotalSamples); // 最后一发完整播放

        // 单发不受影响
        var single = TimelineCompiler.Compile(recipe, TimelineCompiler.BuildManifest(recipe, [asset], weaponId, ManifestKind.Single),
            [asset], weaponId, 48000, ManifestKind.Single);
        Assert.Null(single.Events.Single().FadeOutStartSample);
    }

    [Fact]
    public void MixKernelAppliesLinearFadeThenStops()
    {
        var asset = new AssetInfo { Format = new AudioFormatInfo(48000, 32, 1, true, 100) };
        var buffer = new AssetBuffer { Data = Enumerable.Repeat(1f, 100).ToArray(), Channels = 1, SampleRate = 48000, FrameCount = 100 };
        var timeline = new CompiledTimeline
        {
            Events = [new ShotEvent { AssetId = asset.Id, StartSample = 0, GainDb = 0, FadeOutStartSample = 10, FadeOutSamples = 10 }],
            TotalSamples = 100,
            AssetById = new() { [asset.Id] = asset },
        };
        var mix = MixKernel.Render(timeline, _ => buffer);
        Assert.Equal(1f, mix.Data[9 * 2]);
        Assert.Equal(0.5f, mix.Data[15 * 2], 5);
        Assert.Equal(0f, mix.Data[20 * 2]);
        Assert.Equal(0f, mix.Data[50 * 2 + 1]);
    }

    // ───────────── 真实素材：全部武器 ─────────────

    [Fact]
    public void RealWpnRoot_AllWeaponsGetTypedAdaptiveRecipesWithinPeakCeiling()
    {
        if (!Directory.Exists(WpnRoot)) return; // 素材不在本机时跳过
        var wpn = WpnRoot!;
        var report = new AssetService().ImportDirectory(wpn, includeSubdirectories: true);
        Assert.Empty(report.Failures);
        var buffers = new Dictionary<Guid, AssetBuffer>();
        AssetBuffer? Load(AssetInfo a)
        {
            if (buffers.TryGetValue(a.Id, out var b)) return b;
            var d = WavReader.Read(Path.Combine(a.SourceDirectory, a.FileName));
            return buffers[a.Id] = new AssetBuffer { Data = d.Data, Channels = d.Channels, SampleRate = d.SampleRate, FrameCount = d.FrameCount };
        }
        var project = ProjectFactory.CreateFromImport(report, wpn, loader: Load);

        var expected = new Dictionary<string, string>
        {
            ["findia"] = WeaponTypes.Marksman, ["hotel45"] = WeaponTypes.Pistol, ["mike4"] = WeaponTypes.AssaultRifle,
            ["psierra41"] = WeaponTypes.Smg, ["spier9"] = WeaponTypes.Smg, ["svictor98"] = WeaponTypes.Sniper,
            ["tango73"] = WeaponTypes.MachineGun,
        };
        Assert.Equal(expected.Keys.Order(), project.Weapons.Select(w => w.Name).Order());
        foreach (var weapon in project.Weapons)
        {
            Assert.Equal(expected[weapon.Name], weapon.TypeName);
            var normal = weapon.Recipes.Single(r => r.Name == "自适应·普通");
            Assert.Equal(normal.Id, weapon.ActiveRecipeId);
            Assert.Contains(normal.Layers, l => l.Role == LayerRoles.Shot && l.Enabled);
            var assets = project.Assets.Where(a => a.WeaponId == weapon.Id).ToList();
            foreach (var recipe in weapon.Recipes.Where(r => r.AdaptiveProfile != null))
            {
                double single = AdaptiveRecipeBuilder.RenderPeakDb(recipe, assets, weapon.Id, Load, 48000, ManifestKind.Single);
                double burst = AdaptiveRecipeBuilder.RenderPeakDb(recipe, assets, weapon.Id, Load, 48000, ManifestKind.Burst);
                Assert.True(single <= WeaponProfiles.SinglePeakCeilingDbfs + 1e-6, $"{weapon.Name}/{recipe.Name} 单发峰值 {single:0.00}");
                Assert.True(burst <= WeaponProfiles.BurstPeakCeilingDbfs + 1e-6, $"{weapon.Name}/{recipe.Name} 连发峰值 {burst:0.00}");
            }
            buffers.Clear();
        }
        // psierra41 的两种前缀（weap_ / weap_rex_）合并为同一把武器
        var ps = project.Weapons.Single(w => w.Name == "psierra41");
        Assert.Contains(project.Assets, a => a.WeaponId == ps.Id && a.FileName.StartsWith("weap_psierra41_"));
        Assert.Contains(project.Assets, a => a.WeaponId == ps.Id && a.FileName.StartsWith("weap_rex_psierra41_"));
    }
}
