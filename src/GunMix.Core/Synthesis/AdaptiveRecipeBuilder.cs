using GunMix.Core.Assets;
using GunMix.Core.Audio;
using GunMix.Core.Mixing;
using GunMix.Core.Model;
using GunMix.Core.Timeline;

namespace GunMix.Core.Synthesis;

/// <summary>
/// 按枪型自适应生成配方：依据该武器实际存在的文件组选择层，按 WeaponProfile 的相对响度目标
/// 和素材实测 RMS 计算增益，再按真实渲染峰值做整体下调保护。所有结果都是试混建议，可继续编辑。
/// 缺素材的层直接省略；跨状态复用（如消音借用普通机械层）会在层名和说明中写明，且默认关闭。
/// </summary>
public static class AdaptiveRecipeBuilder
{
    public const string NamePrefix = "自适应·";

    /// <summary>候选素材组：按顺序取第一个存在的；Reuse 表示跨状态复用。</summary>
    private sealed record Candidate(string Group, bool Reuse = false, string? Label = null);

    private sealed record LayerSpec(string Role, Candidate[] Candidates, bool Experimental = false);

    private sealed record RecipeSpec(string Name, double OffsetDb, LayerSpec[] Layers, Func<HashSet<string>, bool>? Condition = null);

    private static Candidate[] C(params string[] groups) => groups.Select(g => new Candidate(g)).ToArray();

    private static readonly RecipeSpec[] Specs =
    [
        new("普通", 0,
        [
            new(LayerRoles.Shot, C("fire_plr_shot")),
            new(LayerRoles.Mech, C("fire_plr_mech")),
            new(LayerRoles.Low, C("fire_plr_lfe")),
            new(LayerRoles.Swt, C("fire_plr_swt")),
            new(LayerRoles.Atmo, C("fire_plr_atmo")),
        ]),
        new("ADS", 0,
        [
            new(LayerRoles.Shot, [new("fire_plr_shot_ads"), new("fire_plr_shot", Label: "沿用普通")]),
            new(LayerRoles.Mech, [new("fire_plr_mech_ads"), new("fire_plr_mech", Label: "沿用普通")]),
            new(LayerRoles.Low, [new("fire_plr_lfe_ads"), new("fire_plr_lfe", Label: "沿用普通")]),
            new(LayerRoles.Swt, [new("fire_plr_swt_ads"), new("fire_plr_swt", Label: "沿用普通")]),
            new(LayerRoles.Atmo, [new("fire_plr_atmo_ads"), new("fire_plr_atmo", Label: "沿用普通")]),
        ], g => HasAds(g, "fire_plr_")),
        new("消音", -4,
        [
            new(LayerRoles.Shot, C("sup_plr_shot")),
            new(LayerRoles.Mech, [new("sup_plr_mech"), new("fire_plr_mech", Reuse: true, Label: "项目自定复用普通")]),
            new(LayerRoles.Low, C("sup_plr_lfe")),
            new(LayerRoles.Swt, C("sup_plr_swt")),
            new(LayerRoles.Atmo, C("sup_plr_atmo")),
        ]),
        new("消音 ADS", -4,
        [
            new(LayerRoles.Shot, [new("sup_plr_shot_ads"), new("sup_plr_shot", Label: "沿用消音")]),
            new(LayerRoles.Mech, [new("sup_plr_mech_ads"), new("sup_plr_mech", Label: "沿用消音"),
                new("fire_plr_mech_ads", Reuse: true, Label: "项目自定复用普通")]),
            new(LayerRoles.Low, [new("sup_plr_lfe_ads"), new("sup_plr_lfe", Label: "沿用消音")]),
            new(LayerRoles.Swt, [new("sup_plr_swt_ads"), new("sup_plr_swt", Label: "沿用消音")]),
            new(LayerRoles.Atmo, [new("sup_plr_atmo_ads"), new("sup_plr_atmo", Label: "沿用消音")]),
        ], g => HasAds(g, "sup_plr_")),
        new("NPC 近", 0,
        [
            new(LayerRoles.Shot, C("fire_npc_shot")),
            new(LayerRoles.Mech, C("fire_npc_mech")),
            new(LayerRoles.Low, C("fire_npc_lfe")),
            new(LayerRoles.Swt, C("fire_npc_swt")),
        ]),
        new("NPC 中", -6,
        [
            new(LayerRoles.Shot, C("fire_npc_shot_med")),
            new(LayerRoles.Mech, C("fire_npc_shot_med_mech")),
        ]),
        new("NPC 远", -12, [new(LayerRoles.Shot, C("fire_npc_shot_far"))]),
        new("NPC 极远", -18, [new(LayerRoles.Shot, C("fire_npc_shot_dist"))]),
    ];

    private static bool HasAds(HashSet<string> groups, string prefix) =>
        groups.Any(g => g.StartsWith(prefix) && g.EndsWith("_ads") && !NameParser.Parse(g).IsExperimental
                        && !g.Contains("interrupt") && !g.Contains("last"));

    /// <summary>
    /// 为一把武器生成全部适用的自适应配方。weaponAssets 必须都属于该武器。
    /// loader 可为 null（此时跳过峰值保护，仅按响度配平）。
    /// </summary>
    public static List<Recipe> Build(Weapon weapon, IReadOnlyList<AssetInfo> weaponAssets,
        Func<AssetInfo, AssetBuffer?>? loader = null, int sampleRate = 48000)
    {
        var profile = WeaponProfiles.For(weapon.TypeName);
        var groups = weaponAssets.Where(a => a.GroupKey.Length > 0).Select(a => a.GroupKey).ToHashSet();
        var result = new List<Recipe>();
        foreach (var spec in Specs)
        {
            if (spec.Condition != null && !spec.Condition(groups)) continue;
            var recipe = BuildOne(spec, profile, groups, weaponAssets, weapon.Id);
            if (recipe == null) continue;
            if (loader != null) ApplyPeakGuard(recipe, weaponAssets, weapon.Id, loader, sampleRate);
            result.Add(recipe);
        }
        return result;
    }

    private static Recipe? BuildOne(RecipeSpec spec, WeaponProfile profile, HashSet<string> groups,
        IReadOnlyList<AssetInfo> assets, Guid weaponId)
    {
        var recipe = new Recipe
        {
            Name = NamePrefix + spec.Name,
            AdaptiveProfile = profile.TypeName,
            BurstRpm = profile.Rpm,
            BurstShotCount = profile.BurstShots,
        };
        var notes = new List<string> { $"{profile.TypeName}模板：{profile.Summary}", profile.Notes };
        if (spec.OffsetDb != 0) notes.Add($"本配方主体目标相对模板 {spec.OffsetDb:+0;-0} dB（状态/距离的示例电平差，原版数值未知）。");

        double shotTarget = profile.ShotTargetDbfs + spec.OffsetDb;
        double? shotGain = null;
        var missing = new List<string>();

        foreach (var layerSpec in spec.Layers)
        {
            var chosen = layerSpec.Candidates.FirstOrDefault(c => groups.Contains(c.Group));
            if (chosen == null)
            {
                if (layerSpec.Role == LayerRoles.Shot) return null; // 没有主体则不生成该状态
                if (!layerSpec.Experimental) missing.Add($"{layerSpec.Role}（{layerSpec.Candidates[0].Group}）");
                continue;
            }
            var pool = assets.Where(a => a.GroupKey == chosen.Group)
                .OrderBy(a => a.Parsed?.Variant ?? int.MaxValue).ThenBy(a => a.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var layer = new Layer
            {
                Name = chosen.Label == null ? layerSpec.Role : $"{layerSpec.Role}（{chosen.Label}）",
                Role = layerSpec.Experimental ? LayerRoles.Custom : layerSpec.Role,
                PoolGroupKey = chosen.Group,
                VariantMode = pool.Count > 1 ? VariantMode.Random : VariantMode.Fixed,
                FixedAssetId = pool.Count == 1 ? pool[0].Id : null,
                Enabled = !layerSpec.Experimental && !chosen.Reuse,
                IsExperimental = layerSpec.Experimental,
            };
            if (layerSpec.Role == LayerRoles.Atmo && profile.FireMode == FireMode.Auto)
                layer.BurstVoiceLimit = profile.AtmoVoiceLimit;
            if (chosen.Reuse) notes.Add($"{layer.Name}：本状态无独立素材，复用 {chosen.Group}，默认关闭。");

            recipe.Layers.Add(layer);
        }

        // 增益：主体按目标 RMS，其他层按“主体目标 + 相对目标 − 本层实测 RMS”
        var shotLayer = recipe.Layers.First(l => l.Role == LayerRoles.Shot);
        var shotRms = LoudnessMeter.GroupEarlyRms(assets.Where(a => a.GroupKey == shotLayer.PoolGroupKey));
        shotGain = shotRms is { } sr ? shotTarget - sr : -12 + spec.OffsetDb;
        foreach (var layer in recipe.Layers)
        {
            layer.GainDb = Round(GainFor(layer, profile, shotTarget, shotGain.Value, assets, spec.OffsetDb));
        }
        // 松扳机尾音：玩家配方统一附一个默认关闭的尾音层，连发时可选开启
        if (ReleaseTail.CandidateGroup(recipe, groups) is { } tailGroup)
        {
            recipe.Layers.Add(ReleaseTail.Create(tailGroup, assets.Count(a => a.GroupKey == tailGroup),
                Round(shotGain.Value - 3)));
            notes.Add($"松扳机尾音可选：{tailGroup} 在末发起点 + 一个射击间隔触发一次（只进入连发；原版条件未确认，默认关闭）。");
        }
        notes.Add(shotRms is { } r
            ? $"主体 {shotLayer.PoolGroupKey} 实测前 250 ms RMS 中位数 {r:0.0} dBFS → 增益 {shotLayer.GainDb:0.0} dB。"
            : "素材未测响度（旧工程）：使用固定回退增益，可点“响度配平”重算。");
        if (missing.Count > 0) notes.Add("缺少素材、已省略的层：" + string.Join("、", missing) + "。");
        recipe.Notes = string.Join("\n", notes);
        return recipe;
    }

    private static double GainFor(Layer layer, WeaponProfile profile, double shotTarget, double shotGain,
        IReadOnlyList<AssetInfo> assets, double offsetDb)
    {
        if (layer.Role == LayerRoles.Shot) return shotGain;
        if (layer.IsExperimental) return shotGain - 3; // 尾声类素材起音段常近乎静音，不按前 250 ms RMS 配平
        var rms = LoudnessMeter.GroupEarlyRms(assets.Where(a => a.GroupKey == layer.PoolGroupKey));
        if (rms is { } v) return shotTarget + profile.RelFor(layer.Role) - v;
        return (layer.Role == LayerRoles.Mech ? -18 : -24) + offsetDb;
    }

    /// <summary>
    /// 对任意配方按枪型模板重新配平（保留层开关、延时、素材池与变体设置）。
    /// 自定义角色层与实验层不改。返回说明文字。
    /// </summary>
    public static string Rebalance(Recipe recipe, Weapon weapon, IReadOnlyList<AssetInfo> weaponAssets,
        Func<AssetInfo, AssetBuffer?>? loader = null, int sampleRate = 48000)
    {
        var profile = WeaponProfiles.For(weapon.TypeName);
        var shotLayer = recipe.Layers.FirstOrDefault(l => l.Role == LayerRoles.Shot && l.Enabled)
                        ?? recipe.Layers.FirstOrDefault(l => l.Role == LayerRoles.Shot);
        if (shotLayer == null) return "配方中没有 SHOT 角色的层，无法配平。";

        var shotPool = AssetService.ResolvePool(shotLayer, weaponAssets, weapon.Id);
        var shotRms = LoudnessMeter.GroupEarlyRms(shotPool);
        if (shotRms == null) return "主体素材没有响度测量（请重新导入或打开时自动测量），未修改。";

        // 沿用配方原有的状态偏移：由当前主体实际电平反推，不强行拉回模板电平
        double shotTarget = recipe.AdaptiveProfile != null
            ? shotLayer.GainDb + shotRms.Value
            : profile.ShotTargetDbfs;
        int changed = 0;
        foreach (var layer in recipe.Layers)
        {
            if (layer.IsExperimental || layer.Role is LayerRoles.Custom) continue;
            var pool = AssetService.ResolvePool(layer, weaponAssets, weapon.Id);
            var rms = LoudnessMeter.GroupEarlyRms(pool);
            if (rms == null) continue;
            double target = layer.Role == LayerRoles.Shot ? shotTarget : shotTarget + profile.RelFor(layer.Role);
            layer.GainDb = Round(target - rms.Value);
            changed++;
        }
        string guard = loader != null ? ApplyPeakGuard(recipe, weaponAssets, weapon.Id, loader, sampleRate) : "";
        return $"已按「{profile.TypeName}」模板配平 {changed} 层（主体目标 {shotTarget:0.0} dBFS）。{guard}";
    }

    /// <summary>渲染单发与连发，若峰值超过上限则所有层同量下调。返回说明（无调整时为空）。</summary>
    public static string ApplyPeakGuard(Recipe recipe, IReadOnlyList<AssetInfo> assets, Guid weaponId,
        Func<AssetInfo, AssetBuffer?> loader, int sampleRate)
    {
        double single = RenderPeakDb(recipe, assets, weaponId, loader, sampleRate, ManifestKind.Single);
        double burst = RenderPeakDb(recipe, assets, weaponId, loader, sampleRate, ManifestKind.Burst);
        double shift = Math.Min(0, Math.Min(WeaponProfiles.SinglePeakCeilingDbfs - single, WeaponProfiles.BurstPeakCeilingDbfs - burst));
        if (shift > -0.05) return "";
        shift = Math.Floor(shift * 2) / 2; // 0.5 dB 步进向下取整，保证不超上限
        foreach (var layer in recipe.Layers) layer.GainDb = Math.Clamp(layer.GainDb + shift, -60, 6);
        var text = $"峰值保护：单发 {single:0.0} / 连发 {burst:0.0} dBFS，全部层整体 {shift:0.0} dB。";
        recipe.Notes = recipe.Notes.Length > 0 ? recipe.Notes + "\n" + text : text;
        return text;
    }

    public static double RenderPeakDb(Recipe recipe, IReadOnlyList<AssetInfo> assets, Guid weaponId,
        Func<AssetInfo, AssetBuffer?> loader, int sampleRate, ManifestKind kind)
    {
        var manifest = TimelineCompiler.BuildManifest(recipe, assets, weaponId, kind);
        var timeline = TimelineCompiler.Compile(recipe, manifest, assets, weaponId, sampleRate, kind);
        if (timeline.Events.Count == 0) return -120;
        var mix = MixKernel.Render(timeline, ev => timeline.AssetById.TryGetValue(ev.AssetId, out var a) ? loader(a) : null);
        return mix.PeakDbfs;
    }

    private static double Round(double db) => Math.Clamp(Math.Round(db * 2, MidpointRounding.AwayFromZero) / 2, -60, 6);
}
