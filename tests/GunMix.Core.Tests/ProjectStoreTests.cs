using System.IO;
using GunMix.Core.Assets;
using GunMix.Core.Model;
using GunMix.Core.Persistence;
using Xunit;

namespace GunMix.Core.Tests;

public class ProjectStoreTests : IDisposable
{
    private readonly string _dir;

    public ProjectStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "gunmix_tests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 清理失败忽略 */ }
    }

    private static GunProject MakeProject()
    {
        var weapon = new Weapon { Name = "mike4", TypeName = WeaponTypes.AssaultRifle };
        var recipe = new Recipe
        {
            Name = "手册五层对照",
            IsTeachingPreset = true,
            BurstRpm = 600,
            BurstShotCount = 6,
            RandomSeed = 42,
        };
        var layer = new Layer
        {
            Name = "SHOT",
            Role = LayerRoles.Shot,
            Enabled = true,
            Solo = false,
            GainDb = -12,
            DelayMs = 3.5,
            PoolGroupKey = "fire_plr_shot",
            VariantMode = VariantMode.Random,
            Seed = 7,
        };
        recipe.Layers.Add(layer);
        recipe.SingleManifest = new EventManifest
        {
            Kind = ManifestKind.Single,
            Entries = [new ManifestEntry { LayerId = layer.Id, ShotIndex = 0, AssetId = Guid.NewGuid() }],
        };
        weapon.Recipes.Add(recipe);
        weapon.ActiveRecipeId = recipe.Id;
        return new GunProject
        {
            ProjectName = "测试工程",
            SourceRoot = @"D:\src",
            ActiveWeaponId = weapon.Id,
            Weapons = [weapon],
            Assets =
            [
                new AssetInfo
                {
                    WeaponId = weapon.Id,
                    FileName = "weap_rex_mike4_fire_plr_shot_01.qnn.85.48000.all.wav",
                    SourceDirectory = @"D:\src\ar_mike4",
                    Sha256 = "abc123",
                    GroupKey = "fire_plr_shot",
                    Format = new AudioFormatInfo(48000, 16, 1, false, 151680),
                },
            ],
            SnapshotA = new AbSnapshot { Label = "A", Recipe = recipe.Clone(), MonitorCompensationDb = -1.5 },
        };
    }

    [Fact]
    public void SaveLoadRoundtrip()
    {
        var path = Path.Combine(_dir, "p.gunmix.json");
        var project = MakeProject();
        ProjectStore.Save(project, path);
        var loaded = ProjectStore.Load(path);

        Assert.Equal("测试工程", loaded.ProjectName);
        Assert.Equal(@"D:\src", loaded.SourceRoot);
        var weapon = loaded.Weapons.Single();
        Assert.Equal("mike4", weapon.Name);
        Assert.Equal(WeaponTypes.AssaultRifle, weapon.TypeName);
        var recipe = weapon.Recipes.Single();
        Assert.Equal("手册五层对照", recipe.Name);
        Assert.True(recipe.IsTeachingPreset);
        Assert.Equal(600, recipe.BurstRpm);
        Assert.Equal(6, recipe.BurstShotCount);
        var layer = recipe.Layers.Single();
        Assert.Equal(-12, layer.GainDb);
        Assert.Equal(3.5, layer.DelayMs);
        Assert.Equal("fire_plr_shot", layer.PoolGroupKey);
        Assert.Equal(VariantMode.Random, layer.VariantMode);
        Assert.Equal(7, layer.Seed);
        Assert.NotNull(recipe.SingleManifest);
        Assert.Single(recipe.SingleManifest.Entries);
        Assert.Equal(-1.5, loaded.SnapshotA!.MonitorCompensationDb);
        Assert.Single(loaded.Assets);
        Assert.Equal("abc123", loaded.Assets[0].Sha256);
    }

    [Fact]
    public void SaveIsAtomic_NoTempLeftBehind()
    {
        var path = Path.Combine(_dir, "p.gunmix.json");
        ProjectStore.Save(MakeProject(), path);
        ProjectStore.Save(MakeProject(), path); // 二次保存走 File.Replace
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void UndoRedoRestoresState()
    {
        var options = ProjectStore.JsonOptions;
        var stack = new UndoStack(options);
        var project = MakeProject();
        var layer = project.Weapons[0].Recipes[0].Layers[0];

        stack.Push(project);
        layer.GainDb = -6; // 编辑
        var undone = stack.Undo(project);
        Assert.NotNull(undone);
        Assert.Equal(-12, undone!.Weapons[0].Recipes[0].Layers[0].GainDb);

        var redone = stack.Redo(undone);
        Assert.NotNull(redone);
        Assert.Equal(-6, redone!.Weapons[0].Recipes[0].Layers[0].GainDb);
    }

    [Fact]
    public void LegacyRecipeImport()
    {
        var path = Path.Combine(_dir, "mix_recipe.json");
        File.WriteAllText(path, """
        {
          "description": "自定义教学试混",
          "source_directory": "D:\\src",
          "sample_rate": 48000,
          "channels": 2,
          "duration_s": 27,
          "gains_db": { "fire_plr_shot": -12 },
          "events": [
            { "time_s": 1, "file": "a.wav", "gain_db": -12, "source_offset_s": 0 },
            { "time_s": 21.1, "file": "b.wav", "gain_db": -18, "source_offset_s": 0 }
          ]
        }
        """);
        var legacy = LegacyMixRecipe.Load(path);
        Assert.Equal(2, legacy.Events.Count);
        Assert.Equal(21.1, legacy.Events[1].TimeS, 6);
        Assert.Equal(-18, legacy.Events[1].GainDb);
    }

    [Fact]
    public void CloneRecipeToWeaponRequiresRemap()
    {
        var weaponA = new Weapon { Name = "A" };
        var recipe = new Recipe { Name = "r" };
        recipe.Layers.Add(new Layer { Name = "SHOT", PoolGroupKey = "fire_plr_shot", GainDb = -12 });
        weaponA.Recipes.Add(recipe);

        var weaponB = new Weapon { Name = "B" };
        var copy = ProjectFactory.CloneRecipeToWeapon(recipe, weaponB, "副本");
        Assert.Equal("副本", copy.Name);
        Assert.Equal(-12, copy.Layers[0].GainDb);       // 参数保留
        Assert.Equal("", copy.Layers[0].PoolGroupKey);  // 素材要求重新映射
        Assert.Empty(copy.Layers[0].PoolAssetIds);
    }

    [Fact]
    public void ProjectFactoryCreatesWeaponsAndPresets()
    {
        var assets = new List<AssetInfo>();
        foreach (var group in new[] { "shot", "mech", "lfe", "swt", "atmo" })
        {
            assets.Add(new AssetInfo
            {
                FileName = $"weap_rex_mike4_fire_plr_{group}_01.qnn.85.48000.all.wav",
                Parsed = NameParser.Parse($"weap_rex_mike4_fire_plr_{group}_01.qnn.85.48000.all.wav"),
                GroupKey = $"fire_plr_{group}",
            });
        }
        assets.Add(new AssetInfo
        {
            FileName = "weap_rex_kar98_fire_plr_shot_01.qnn.85.48000.all.wav",
            Parsed = NameParser.Parse("weap_rex_kar98_fire_plr_shot_01.qnn.85.48000.all.wav"),
            GroupKey = "fire_plr_shot",
        });
        assets.Add(new AssetInfo
        {
            FileName = "mystery_sound_a.wav",
            Parsed = NameParser.Parse("mystery_sound_a.wav"),
            GroupKey = "",
        });
        var report = new ImportReport { TotalFiles = assets.Count };
        report.Assets.AddRange(assets);

        var project = ProjectFactory.CreateFromImport(report, @"D:\src");
        // mike4 / kar98 / 未知武器（命名无法识别的文件不丢弃）
        Assert.Equal(3, project.Weapons.Count);
        var mike4 = project.Weapons.First(w => w.Name == "mike4");
        Assert.All(project.Assets.Where(a => a.Parsed!.Weapon == "mike4"), a => Assert.Equal(mike4.Id, a.WeaponId));

        // 普通玩家基础：SHOT 和 MECH 开，其余关但已分配池
        var basic = mike4.Recipes.First(r => r.Name == "普通玩家基础");
        Assert.Equal(5, basic.Layers.Count);
        Assert.True(basic.Layers.First(l => l.Role == LayerRoles.Shot).Enabled);
        Assert.True(basic.Layers.First(l => l.Role == LayerRoles.Mech).Enabled);
        Assert.False(basic.Layers.First(l => l.Role == LayerRoles.Low).Enabled);
        Assert.Equal("fire_plr_lfe", basic.Layers.First(l => l.Role == LayerRoles.Low).PoolGroupKey);

        // 手册五层对照：五层全开，增益 -12/-18/-24/-24/-24
        var manual = mike4.Recipes.First(r => r.Name == "手册五层对照");
        Assert.All(manual.Layers, l => Assert.True(l.Enabled));
        Assert.Equal(-12, manual.Layers.First(l => l.Role == LayerRoles.Shot).GainDb);
        Assert.Equal(-18, manual.Layers.First(l => l.Role == LayerRoles.Mech).GainDb);
        Assert.Equal(-24, manual.Layers.First(l => l.Role == LayerRoles.Low).GainDb);
        Assert.Equal(-24, manual.Layers.First(l => l.Role == LayerRoles.Swt).GainDb);
        Assert.Equal(-24, manual.Layers.First(l => l.Role == LayerRoles.Atmo).GainDb);

        // kar98 同样有 fire_plr_shot 组，也得到教学预设（SHOT 一层）
        var kar = project.Weapons.First(w => w.Name == "kar98");
        Assert.Equal("普通玩家基础", kar.Recipes[0].Name);
        Assert.Single(kar.Recipes[0].Layers);
        Assert.Equal("fire_plr_shot", kar.Recipes[0].Layers[0].PoolGroupKey);

        // 完全无法识别的素材进入“未知武器”，使用通用空模板：素材待手动指定
        var unknown = project.Weapons.First(w => w.Name == "未知武器");
        Assert.Equal("基础配方", unknown.Recipes[0].Name);
        Assert.Equal(4, unknown.Recipes[0].Layers.Count);
        Assert.All(unknown.Recipes[0].Layers, l =>
        {
            Assert.Null(l.FixedAssetId);
            Assert.Equal("", l.PoolGroupKey);
        });
    }
}
