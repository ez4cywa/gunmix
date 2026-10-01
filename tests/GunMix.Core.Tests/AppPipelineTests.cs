using System.IO;
using GunMix.App.ViewModels;
using GunMix.Core.Assets;
using GunMix.Core.Model;
using GunMix.Core.Timeline;
using Xunit;

namespace GunMix.Core.Tests;

/// <summary>
/// 应用层闭环测试：真实素材目录 → 导入（ApplyImport）→ 渲染预览（峰值非零，排除“无声”）→
/// 单发与连发分别导出（各自 WAV + recipe JSON）。素材在本机存在时执行，CI 上自动跳过。
/// </summary>
public class AppPipelineTests
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

    private static string? LocateSourceDir()
    {
        var root = FindRepoRoot();
        if (root == null) return null;
        var recipe = Path.Combine(root, "output", "audio", "mix_recipe.json");
        if (!File.Exists(recipe)) return null;
        var sourceDir = LegacyRecipeSource(recipe);
        return Directory.Exists(sourceDir) ? sourceDir : null;
    }

    private static string LegacyRecipeSource(string recipePath)
    {
        var json = File.ReadAllText(recipePath);
        var m = System.Text.RegularExpressions.Regex.Match(json, "\"source_directory\"\\s*:\\s*\"([^\"]+)\"");
        return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\") : "";
    }

    private static MainViewModel BuildLoadedViewModel(string sourceDir)
    {
        var vm = new MainViewModel();
        var svc = new AssetService();
        var report = svc.ImportDirectory(sourceDir, includeSubdirectories: true);
        Assert.Equal(278, report.Assets.Count);
        vm.ApplyImport(report, sourceDir);
        Assert.True(vm.ProjectLoaded);
        return vm;
    }

    [Fact]
    public void Import_RealSampleFolder_LoadsWeaponsAndRecipes()
    {
        var sourceDir = LocateSourceDir();
        if (sourceDir == null) return;

        var vm = BuildLoadedViewModel(sourceDir);
        try
        {
            Assert.Single(vm.Project.Weapons);
            Assert.Equal("mike4", vm.SelectedWeapon?.Name);
            Assert.Equal(WeaponTypes.AssaultRifle, vm.SelectedWeapon?.Weapon.TypeName); // 由目录 ar_ 前缀判定
            // 教学预设（普通玩家基础 + 手册五层对照）在前，随后是按枪型生成的自适应配方
            var names = vm.SelectedWeapon!.Weapon.Recipes.Select(r => r.Name).ToList();
            Assert.Equal(
                ["普通玩家基础", "手册五层对照", "自适应·普通", "自适应·ADS", "自适应·消音",
                 "自适应·NPC 近", "自适应·NPC 中", "自适应·NPC 远", "自适应·NPC 极远"], names);
            Assert.NotNull(vm.CurrentRecipe);
            Assert.Equal("自适应·普通", vm.CurrentRecipe!.Name);
            // 清单已生成且有效（无需手动重新生成即可播放/导出）
            Assert.NotNull(vm.CurrentRecipe!.BurstManifest);
            Assert.True(vm.CurrentRecipe.BurstManifest!.Matches(vm.CurrentRecipe, ManifestKind.Burst));
        }
        finally
        {
            vm.Shutdown();
        }
    }

    [Fact]
    public void PreviewRender_NonSilentForBurst()
    {
        // 排除“无声”：按当前清单渲染连发，混合结果峰值必须远高于静音
        var sourceDir = LocateSourceDir();
        if (sourceDir == null) return;

        var vm = BuildLoadedViewModel(sourceDir);
        try
        {
            vm.RegenerateManifests();
            var weapon = vm.Project.ActiveWeapon!;
            var recipe = vm.CurrentRecipe!;
            var timeline = TimelineCompiler.Compile(recipe, recipe.BurstManifest!, vm.Project.Assets, weapon.Id,
                vm.Project.SampleRate, ManifestKind.Burst);
            var mix = GunMix.Core.Mixing.MixKernel.Render(timeline, ev =>
            {
                var asset = vm.Project.Assets.FirstOrDefault(a => a.Id == ev.AssetId);
                if (asset == null) return null;
                var path = vm.AssetPathOf(asset);
                return vm.Cache.Get(asset.Id, path, vm.Project.SampleRate);
            }, 2);
            Assert.Empty(mix.MissingAssets);
            Assert.True(mix.Peak > 0.1, $"混音峰值异常偏低：{mix.Peak}");
            Assert.Equal(recipe.BurstShotCount, timeline.Events.Select(e => e.ShotIndex).Distinct().Count());
            // 自适应配方带峰值保护：连发不越界
            Assert.True(mix.PeakDbfs <= 0, $"自适应配方连发峰值越界：{mix.PeakDbfs:0.00} dBFS");
        }
        finally
        {
            vm.Shutdown();
        }
    }

    [Fact]
    public void AppendImport_NewWeaponGetsTypedRecipes_AndSwitchKeepsActiveRecipe()
    {
        var sourceDir = LocateSourceDir();
        var spier9 = Path.Combine(Path.GetDirectoryName(sourceDir ?? "") ?? "", "sm_spier9");
        if (sourceDir == null || !Directory.Exists(spier9)) return;

        var vm = BuildLoadedViewModel(sourceDir);
        try
        {
            var mike4 = vm.SelectedWeapon!;
            vm.SelectedRecipe = vm.Recipes.First(r => r.Name == "自适应·ADS");

            vm.ApplyImport(new AssetService().ImportDirectory(spier9), spier9); // 追加导入
            var sp = vm.Weapons.Single(w => w.Name == "spier9");
            Assert.Equal(WeaponTypes.Smg, sp.Weapon.TypeName);
            Assert.Contains(sp.Weapon.Recipes, r => r.Name == "自适应·普通" && r.BurstRpm == 900);

            vm.SelectedWeapon = sp;
            Assert.Equal("自适应·普通", vm.CurrentRecipe!.Name);
            vm.SelectedWeapon = vm.Weapons.Single(w => w.Id == mike4.Id);
            Assert.Equal("自适应·ADS", vm.CurrentRecipe!.Name); // 切回后保持原配方
        }
        finally
        {
            vm.Shutdown();
        }
    }

    [Fact]
    public void ReleaseTail_ToggleExportsBurstWithTailOnly()
    {
        var sourceDir = LocateSourceDir();
        if (sourceDir == null) return;

        var vm = BuildLoadedViewModel(sourceDir);
        var outDir = Path.Combine(Path.GetTempPath(), "gunmix_tail_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(outDir);
            Assert.True(vm.ReleaseTailAvailable);
            Assert.False(vm.ReleaseTailEnabled);
            vm.ReleaseTailEnabled = true;
            Assert.True(vm.ReleaseTailEnabled);

            var options = new MainViewModel.ExportOptions { ExportSingle = true, ExportBurst = true, BitDepth = 24, OutputDirectory = outDir };
            var burst = vm.ExportKind(ManifestKind.Burst, options);
            var single = vm.ExportKind(ManifestKind.Single, options);
            Assert.True(burst is { Success: true }, burst?.Error);
            Assert.True(single is { Success: true }, single?.Error);
            Assert.EndsWith("mike4_burst_750rpm_10shot_tail_001.wav", burst!.FinalPath);
            Assert.EndsWith("mike4_single_001.wav", single!.FinalPath); // 单发不带尾音

            var json = File.ReadAllText(Path.ChangeExtension(burst.FinalPath!, ".recipe.json"));
            Assert.Contains("\"release_tail\": true", json);
            Assert.Contains("fire_plr_interrupt", json);
            var singleJson = File.ReadAllText(Path.ChangeExtension(single.FinalPath!, ".recipe.json"));
            Assert.DoesNotContain("weap_rex_mike4_fire_plr_interrupt_", singleJson);

            vm.DoUndo();
            Assert.False(vm.ReleaseTailEnabled); // 可撤销
        }
        finally
        {
            vm.Shutdown();
            try { Directory.Delete(outDir, true); } catch { /* 清理失败忽略 */ }
        }
    }

    [Fact]
    public void ExportKind_WritesSeparateSingleAndBurstWithRecipeJson()
    {
        var sourceDir = LocateSourceDir();
        if (sourceDir == null) return;

        var vm = BuildLoadedViewModel(sourceDir);
        var outDir = Path.Combine(Path.GetTempPath(), "gunmix_e2e_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(outDir);
            var options = new MainViewModel.ExportOptions
            {
                ExportSingle = true,
                ExportBurst = true,
                BitDepth = 24,
                Dither = true,
                DitherSeed = 20260927,
                OutputDirectory = outDir,
                Overwrite = false,
            };

            var single = vm.ExportKind(ManifestKind.Single, options);
            var burst = vm.ExportKind(ManifestKind.Burst, options);

            Assert.True(single is { Success: true }, $"单发导出失败：{single?.Error}");
            Assert.True(burst is { Success: true }, $"连发导出失败：{burst?.Error}");

            Assert.True(File.Exists(single!.FinalPath));
            Assert.True(File.Exists(burst!.FinalPath));
            Assert.True(File.Exists(Path.ChangeExtension(single.FinalPath!, ".recipe.json")));
            Assert.True(File.Exists(Path.ChangeExtension(burst.FinalPath!, ".recipe.json")));

            // 两个输出是独立文件：单发一次触发（约 3 秒级），连发 6 发（尾部叠加后更长或峰值不同）
            Assert.NotEqual(single.FinalPath, burst.FinalPath);
            Assert.EndsWith("mike4_single_001.wav", single.FinalPath, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("mike4_burst_750rpm_10shot_001.wav", burst.FinalPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(single.DurationSeconds > 1.0);
            Assert.True(burst.DurationSeconds > single.DurationSeconds - 0.5);

            // recipe JSON 记录事件与源哈希
            var json = File.ReadAllText(Path.ChangeExtension(burst.FinalPath!, ".recipe.json"));
            Assert.Contains("\"kind\": \"burst\"", json);
            Assert.Contains("\"rpm\": 750", json);
            Assert.Contains("\"adaptive_profile\": \"突击步枪\"", json);
            Assert.Contains("sha256", json);

            // 截尾验证：真实素材的自然衰减尾必须低于 -60 dBFS 阈值，截尾必须实际发生
            Assert.True(single!.TrimmedFrames > 0 || burst!.TrimmedFrames > 0,
                $"真实素材应触发截尾（单发截 {single?.TrimmedFrames} 帧，连发截 {burst?.TrimmedFrames} 帧）");
            var noTrim = new MainViewModel.ExportOptions
            {
                ExportSingle = true,
                BitDepth = 24,
                Dither = false,
                OutputDirectory = outDir,
                TrimTail = false,
            };
            var singleNoTrim = vm.ExportKind(ManifestKind.Single, noTrim);
            Assert.True(singleNoTrim is { Success: true }, singleNoTrim?.Error);
            Assert.True(single!.DurationSeconds <= singleNoTrim!.DurationSeconds + 1e-9,
                $"截尾后 ({single!.DurationSeconds}s) 不应比不截尾 ({singleNoTrim!.DurationSeconds}s) 长");
            if (single!.TrimmedFrames > 0)
            {
                Assert.True(singleNoTrim!.TrimmedFrames == 0);
                var trimmedJson = File.ReadAllText(Path.ChangeExtension(single.FinalPath!, ".recipe.json"));
                Assert.Contains("\"tail_trim\"", trimmedJson);
            }
        }
        finally
        {
            vm.Shutdown();
            try { Directory.Delete(outDir, true); } catch { /* 清理失败忽略 */ }
        }
    }
}
