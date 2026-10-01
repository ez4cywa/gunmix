using GunMix.Core.Model;

namespace GunMix.Core.Synthesis;

/// <summary>
/// 连发的松扳机尾音：在序列释放时刻（末发起点 + 一个射击间隔）触发一次 *_plr_interrupt 素材。
/// 该用法依据命名与波形推断（interrupt 组起音段弱、尾部长），原版触发条件未确认，故仍标为实验层。
/// </summary>
public static class ReleaseTail
{
    public const string LayerName = "TAIL 松扳机尾声";

    /// <summary>导出文件名标记：连发含尾音时加在射速/发数之后。</summary>
    public const string FileTag = "_tail";

    public static Layer? Find(Recipe recipe) => recipe.Layers.FirstOrDefault(l => l.IsReleaseTail);

    public static bool IsEnabled(Recipe recipe) => recipe.Layers.Any(l => l.IsReleaseTail && l.Enabled);

    /// <summary>
    /// 与配方状态对应的尾音素材组：主体为 sup_plr_* 用 sup_plr_interrupt，否则 fire_plr_interrupt；
    /// 配方含 *_ads 层时优先 *_interrupt_ads。NPC 配方没有 interrupt 素材，返回 null。
    /// </summary>
    public static string? CandidateGroup(Recipe recipe, IReadOnlyCollection<string> availableGroups)
    {
        var shot = recipe.Layers.FirstOrDefault(l => l.Role == LayerRoles.Shot)?.PoolGroupKey ?? "";
        string? prefix = shot.StartsWith("sup_plr_") ? "sup_plr_" : shot.StartsWith("fire_plr_") ? "fire_plr_" : null;
        if (prefix == null) return null;
        bool ads = recipe.Layers.Any(l => l.PoolGroupKey.EndsWith("_ads"));
        string[] candidates = ads ? [prefix + "interrupt_ads", prefix + "interrupt"] : [prefix + "interrupt"];
        return candidates.FirstOrDefault(availableGroups.Contains);
    }

    /// <summary>取得尾音层；配方中没有时按 CandidateGroup 新建（默认关闭，增益 = 主体 −3 dB）。无素材返回 null。</summary>
    public static Layer? Ensure(Recipe recipe, IReadOnlyList<AssetInfo> weaponAssets)
    {
        if (Find(recipe) is { } existing) return existing;
        var groups = weaponAssets.Select(a => a.GroupKey).Where(g => g.Length > 0).ToHashSet();
        var group = CandidateGroup(recipe, groups);
        if (group == null) return null;
        int poolCount = weaponAssets.Count(a => a.GroupKey == group);
        var shotGain = recipe.Layers.FirstOrDefault(l => l.Role == LayerRoles.Shot)?.GainDb ?? -12;
        var layer = Create(group, poolCount, shotGain - 3);
        layer.FixedAssetId = poolCount == 1 ? weaponAssets.First(a => a.GroupKey == group).Id : null;
        recipe.Layers.Add(layer);
        return layer;
    }

    public static Layer Create(string group, int poolCount, double gainDb) => new()
    {
        Name = LayerName,
        Role = LayerRoles.Custom,
        PoolGroupKey = group,
        VariantMode = poolCount > 1 ? VariantMode.Random : VariantMode.Fixed,
        Enabled = false,
        IsExperimental = true,
        Trigger = ExperimentalTrigger.ReleaseMoment,
        GainDb = Math.Clamp(gainDb, -60, 6),
    };
}
