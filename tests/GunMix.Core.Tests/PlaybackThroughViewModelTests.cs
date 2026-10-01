using System.IO;
using System.Threading;
using GunMix.App.ViewModels;
using GunMix.Core.Cast;
using Xunit;

namespace GunMix.Core.Tests;

/// <summary>
/// 从用户实际入口（MainViewModel 的试听）验证真的有声：
/// 走 PlayPreview / PlayLayer / 动画试听 → 混音内核 → WASAPI，断言设备侧收到非静音数据。
/// 这是"播放无声音"bug 的回归防线（曾因 NAudio 把 Byte[] 当 float[] 回传而在播放线程抛
/// ArrayTypeMismatchException，表现为无异常但完全无声）。
/// </summary>
public class PlaybackThroughViewModelTests
{
    // 真实素材路径由环境变量 GUNMIX_ASSETS_ROOT 提供；未设置时相关测试自动跳过。
    private static string? WpnRoot => TestPaths.WpnRoot;
    private static string? ShotDir => WpnRoot == null ? null : Path.Combine(WpnRoot, "ar_mike4");

    private static bool DataAvailable() => TestPaths.Ready(ShotDir!);

    private static (MainViewModel vm, string shotPath) BuildViewModelWithRealShot()
    {
        var vm = new MainViewModel();
        var shot = Path.Combine(ShotDir!, "weap_rex_mike4_fire_plr_shot_01.qnn.85.48000.all.wav");
        var svc = new GunMix.Core.Assets.AssetService();
        var report = svc.ImportDirectory(ShotDir!, includeSubdirectories: false);
        vm.ApplyImport(report, ShotDir!);
        return (vm, shot);
    }

    /// <summary>等待服务侧出现非静音电平回调（说明数据真的到了音频引擎）。</summary>
    private static bool WaitForLevel(MainViewModel vm, int ms = 8000)
    {
        var deadline = System.DateTime.UtcNow.AddMilliseconds(ms);
        while (System.DateTime.UtcNow < deadline)
        {
            if (vm.LevelL > 0.05 || vm.LevelR > 0.05) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    [Fact]
    public void PlayPreview_RealAssets_ProducesAudibleLevel()
    {
        if (!DataAvailable()) return;
        if (new GunMix.App.Services.AudioPlaybackService().EnumerateDevices().Count == 0) return;

        var (vm, _) = BuildViewModelWithRealShot();
        try
        {
            // 手册五层对照：SHOT/MECH/LOW/SWT/ATMO 全开
            var manual = vm.Project.ActiveWeapon!.Recipes.First(r => r.Name == "手册五层对照");
            vm.SelectedRecipe = vm.Recipes.First(x => x.Recipe == manual);
            vm.BurstMode = false;      // 单发
            vm.RegenerateManifests();

            vm.PlayCommand.Execute(null);
            Assert.True(WaitForLevel(vm), "试听没有产生任何可听电平（数据未到达设备）");
            vm.Playback.Stop(immediate: true);
        }
        finally
        {
            vm.Shutdown();
        }
    }

    [Fact]
    public void PlayLayer_SingleLayer_ProducesAudibleLevel()
    {
        if (!DataAvailable()) return;
        if (new GunMix.App.Services.AudioPlaybackService().EnumerateDevices().Count == 0) return;

        var (vm, _) = BuildViewModelWithRealShot();
        try
        {
            var basic = vm.Project.ActiveWeapon!.Recipes.First(r => r.Name == "普通玩家基础");
            vm.SelectedRecipe = vm.Recipes.First(x => x.Recipe == basic);
            vm.BurstMode = false;
            vm.RegenerateManifests();

            var shotLayer = vm.Layers.First(l => l.Role == GunMix.Core.Model.LayerRoles.Shot);
            vm.PlayLayer(shotLayer);
            Assert.True(WaitForLevel(vm), "单层试听没有产生任何可听电平");
            vm.Playback.Stop(immediate: true);
        }
        finally
        {
            vm.Shutdown();
        }
    }

    [Fact]
    public void PreviewAnimation_RealAssets_ProducesAudibleLevel()
    {
        if (!DataAvailable()) return;
        var animDir = TestPaths.AnimDir!;
        var bankDir = TestPaths.BankDir!;
        if (!TestPaths.Ready(animDir, bankDir)) return;
        if (new GunMix.App.Services.AudioPlaybackService().EnumerateDevices().Count == 0) return;

        var vm = new MainViewModel();
        try
        {
            vm.ScanAnimations(animDir, [WpnRoot!], bankDir);
            // 找一条已全部解析、且事件时长够长的动画
            var clip = vm.Project.Animations
                .Where(c => c.Events.Count > 0 && c.Events.All(e => e.AssetId != null))
                .OrderByDescending(c => c.Events.Count)
                .First();

            vm.PreviewAnimation(clip, 0);
            Assert.True(WaitForLevel(vm, 12000), $"动画试听没有产生可听电平（{clip.Name}）");
            vm.Playback.Stop(immediate: true);
        }
        finally
        {
            vm.Shutdown();
        }
    }
}
