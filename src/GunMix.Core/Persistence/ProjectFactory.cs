using GunMix.Core.Assets;
using GunMix.Core.Audio;
using GunMix.Core.Model;
using GunMix.Core.Synthesis;

namespace GunMix.Core.Persistence;

/// <summary>由导入结果建立工程：每把武器独立素材、配方、类型和导出设置。</summary>
public static class ProjectFactory
{
    /// <param name="loader">可选的解码函数，用于自适应配方的峰值保护；null 时只按响度配平。</param>
    public static GunProject CreateFromImport(ImportReport report, string sourceRoot, string? fallbackWeaponName = null,
        Func<AssetInfo, AssetBuffer?>? loader = null)
    {
        var project = new GunProject
        {
            SourceRoot = sourceRoot,
            ProjectName = string.IsNullOrEmpty(sourceRoot) ? "未命名工程" : Path.GetFileName(sourceRoot.TrimEnd('\\', '/')),
        };
        if (project.ProjectName.Length == 0) project.ProjectName = "未命名工程";

        var assetsByWeapon = report.Assets
            .GroupBy(a => AssetService.WeaponNameOf(a) ?? fallbackWeaponName ?? "未知武器", StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in assetsByWeapon)
        {
            var weapon = new Weapon
            {
                Name = group.Key,
                TypeName = AssetService.TypeFromDirectories(group) ?? WeaponTypes.GuessFromName(group.Key),
            };
            foreach (var asset in group)
            {
                asset.WeaponId = weapon.Id;
                project.Assets.Add(asset);
            }
            SetupNewWeapon(weapon, group.ToList(), loader, project.SampleRate);
            project.Weapons.Add(weapon);
        }

        project.ActiveWeaponId = project.Weapons.FirstOrDefault()?.Id ?? Guid.Empty;
        return project;
    }

    /// <summary>
    /// 新武器的配方与导出设置：教学预设（保持原顺序）＋按枪型自适应配方；默认激活“自适应·普通”。
    /// weaponAssets 须已设置 WeaponId。
    /// </summary>
    public static void SetupNewWeapon(Weapon weapon, List<AssetInfo> weaponAssets,
        Func<AssetInfo, AssetBuffer?>? loader = null, int sampleRate = 48000)
    {
        AddDefaultRecipes(weapon, weaponAssets);
        var adaptive = AdaptiveRecipeBuilder.Build(weapon, weaponAssets, loader, sampleRate);
        weapon.Recipes.AddRange(adaptive);
        weapon.ActiveRecipeId = (adaptive.FirstOrDefault() ?? weapon.Recipes[0]).Id;
        weapon.Export.NamePrefix = SanitizePrefix(weapon.Name);
    }

    /// <summary>
    /// 重新生成自适应配方：只替换带 AdaptiveProfile 标记的配方，用户自建或复制的配方保留。
    /// 返回新生成的配方。
    /// </summary>
    public static List<Recipe> RegenerateAdaptive(Weapon weapon, List<AssetInfo> weaponAssets,
        Func<AssetInfo, AssetBuffer?>? loader = null, int sampleRate = 48000)
    {
        bool activeWasAdaptive = weapon.ActiveRecipe?.AdaptiveProfile != null;
        string? activeName = weapon.ActiveRecipe?.Name;
        weapon.Recipes.RemoveAll(r => r.AdaptiveProfile != null);
        var adaptive = AdaptiveRecipeBuilder.Build(weapon, weaponAssets, loader, sampleRate);
        weapon.Recipes.AddRange(adaptive);
        if (weapon.Recipes.Count == 0) AddDefaultRecipes(weapon, weaponAssets);
        if (activeWasAdaptive || weapon.Recipes.All(r => r.Id != weapon.ActiveRecipeId))
        {
            weapon.ActiveRecipeId = (adaptive.FirstOrDefault(r => r.Name == activeName)
                                     ?? adaptive.FirstOrDefault()
                                     ?? weapon.Recipes[0]).Id;
        }
        return adaptive;
    }

    /// <summary>教学预设：普通玩家基础（SHOT/MECH 开）与手册五层对照（-12/-18/-24/-24/-24）。可编辑。</summary>
    public static void AddDefaultRecipes(Weapon weapon, List<AssetInfo> weaponAssets)
    {
        var playerGroups = weaponAssets
            .Where(a => a.Parsed?.Category == "fire" && a.Parsed?.Perspective == "plr" && !a.Parsed.IsExperimental)
            .Select(a => a.GroupKey)
            .Distinct()
            .ToHashSet();

        var roles = new (string Role, string Keyword, string Sub)[]
        {
            (LayerRoles.Shot, "shot", "主体"),
            (LayerRoles.Mech, "mech", "机械"),
            (LayerRoles.Low, "lfe", "低频"),
            (LayerRoles.Swt, "swt", "修饰候选"),
            (LayerRoles.Atmo, "atmo", "空间候选"),
        };

        // 普通玩家基础：只开启 SHOT 和 MECH；LOW、SWT、ATMO 已分配候选池但默认关闭。
        var basic = new Recipe { Name = "普通玩家基础", IsTeachingPreset = true };
        foreach (var (role, keyword, sub) in roles)
        {
            var poolKey = playerGroups.FirstOrDefault(g => g.EndsWith("_" + keyword) || g == keyword);
            if (poolKey == null) continue;
            bool enabled = role is LayerRoles.Shot or LayerRoles.Mech;
            basic.Layers.Add(new Layer
            {
                Name = role,
                Role = role,
                Enabled = enabled,
                GainDb = role switch { LayerRoles.Shot => -12, LayerRoles.Mech => -18, _ => -24 },
                PoolGroupKey = poolKey,
                VariantMode = VariantMode.Fixed,
                FixedAssetId = weaponAssets
                    .Where(a => a.GroupKey == poolKey)
                    .OrderBy(a => a.Parsed?.Variant ?? int.MaxValue)
                    .FirstOrDefault()?.Id,
            });
        }

        // 手册五层对照：五层及 -12/-18/-24/-24/-24 dB，顺序轮换变体。
        var manual = new Recipe { Name = "手册五层对照", IsTeachingPreset = true };
        foreach (var (role, keyword, _) in roles)
        {
            var poolKey = playerGroups.FirstOrDefault(g => g.EndsWith("_" + keyword) || g == keyword);
            if (poolKey == null) continue;
            manual.Layers.Add(new Layer
            {
                Name = role,
                Role = role,
                Enabled = true,
                GainDb = role switch
                {
                    LayerRoles.Shot => -12,
                    LayerRoles.Mech => -18,
                    _ => -24,
                },
                PoolGroupKey = poolKey,
                VariantMode = VariantMode.Rotation,
            });
        }

        if (basic.Layers.Count > 0) weapon.Recipes.Add(basic);
        if (manual.Layers.Count > 0) weapon.Recipes.Add(manual);

        // 其他武器通用空模板：主体＋可选机械/低频/空间，手动指定素材，不要求五层齐全。
        if (weapon.Recipes.Count == 0)
        {
            var blank = new Recipe { Name = "基础配方", IsTeachingPreset = false };
            blank.Layers.Add(new Layer { Name = "主体", Role = LayerRoles.Shot, Enabled = true, GainDb = 0 });
            blank.Layers.Add(new Layer { Name = "机械", Role = LayerRoles.Mech, Enabled = false, GainDb = -18 });
            blank.Layers.Add(new Layer { Name = "低频", Role = LayerRoles.Low, Enabled = false, GainDb = -24 });
            blank.Layers.Add(new Layer { Name = "空间", Role = LayerRoles.Atmo, Enabled = false, GainDb = -24 });
            weapon.Recipes.Add(blank);
        }
    }

    /// <summary>复制配方到另一武器：只复制结构和参数，素材全部要求重新映射。</summary>
    public static Recipe CloneRecipeToWeapon(Recipe source, Weapon target, string newName)
    {
        var copy = source.Clone();
        copy.Name = newName;
        copy.IsTeachingPreset = false;
        foreach (var layer in copy.Layers)
        {
            layer.PoolGroupKey = "";
            layer.PoolAssetIds = [];
            layer.FixedAssetId = null;
        }
        target.Recipes.Add(copy);
        return copy;
    }

    private static string SanitizePrefix(string name)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in name)
        {
            if (char.IsLetterOrDigit(c) || c is '_' or '-') sb.Append(c);
        }
        return sb.Length > 0 ? sb.ToString() : "weapon";
    }
}
