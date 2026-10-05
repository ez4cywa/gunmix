using System.IO;
using GunMix.App.ViewModels;
using GunMix.Core.Cast;
using Xunit;

namespace GunMix.Core.Tests;

/// <summary>
/// 动画音效装配的应用层闭环：真实 cast 与音频目录 → 扫描 → 指定文件 → 渲染（非静音）→ 导出 WAV + JSON。
/// 素材缺失时跳过。
/// </summary>
public class AnimationPipelineTests
{
    // 真实素材路径由环境变量 GUNMIX_ASSETS_ROOT 提供；未设置时相关测试自动跳过。
    private static string? AnimDir => TestPaths.AnimDir;
    private static string? SoundDir => TestPaths.WpnRoot;

    private static bool DataAvailable() => TestPaths.Ready(AnimDir!, SoundDir!);

    [Fact]
    public void ScanAnimations_RealData_RegistersClipsAssetsAndExports()
    {
        if (!DataAvailable()) return;
        var vm = new MainViewModel();
        var outDir = Path.Combine(Path.GetTempPath(), "gunmix_anim_" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(outDir);
            var report = vm.ScanAnimations(AnimDir!, [SoundDir!]);
            Assert.Equal(Directory.GetFiles(TestPaths.AnimDir!, "*.cast", SearchOption.AllDirectories).Length, report.CastFilesSeen);
            Assert.True(report.TotalEvents >= 249);
            Assert.True(report.ClipsWithAudio > 0);
            Assert.True(vm.Project.Animations.Count > 0);

            // 选一条事件最少且候选明确的动画（drop：1 个事件）
            var clip = vm.Project.Animations
                .Where(c => c.Events.Count is > 0 and <= 3)
                .OrderBy(c => c.Events.Count)
                .First();

            // 为未解析事件显式指定第一个候选（模拟用户动作）
            foreach (var ev in clip.Events)
            {
                if (ev.AssetId == null && ev.CandidateIds.Count > 0)
                {
                    ev.AssetId = ev.CandidateIds[0];
                    ev.AutoAssigned = false;
                }
            }
            Assert.All(clip.Events, ev => Assert.NotNull(ev.AssetId));

            var timeline = vm.CompileAnimation(clip);
            Assert.Empty(timeline.Issues);
            Assert.True(timeline.Events.Count > 0);

            var path = Path.Combine(outDir, "anim_test.wav");
            var result = vm.ExportAnimation(clip, path, 24, dither: false, 1,
                attenuateToDbfs: null, trimTail: true, -60, 120);
            Assert.True(result is { Success: true }, result?.Error);
            Assert.True(File.Exists(path));
            Assert.True(result!.DurationSeconds > 0);
            Assert.True(result.PeakDbfs > -60, $"动画音效不应接近静音：{result.PeakDbfs} dBFS");

            var json = File.ReadAllText(Path.ChangeExtension(path, ".anim.json"));
            Assert.Contains("\"schema\": \"gunmix-animation/1\"", json);
            Assert.Contains("\"frame\":", json);
            Assert.Contains("\"alias\":", json);
            Assert.Contains("优先来自 sndbanks/json 的原版声音配置", json);
            Assert.Contains("\"tail_trim\"", json);

            // 事件时间必须等于帧号 / 帧率（实测时序，不是猜测）
            foreach (var ev in clip.Events)
            {
                var rendered = timeline.Events.FirstOrDefault(x => x.LayerId == ev.Id);
                Assert.NotNull(rendered);
                double expected = (long)Math.Round(ev.Frame / clip.Framerate * 48000.0);
                Assert.True(Math.Abs(rendered!.StartSample - expected) <= 1,
                    $"事件起点 {rendered.StartSample} 与帧换算 {expected} 不一致");
            }
        }
        finally
        {
            vm.Shutdown();
            try { Directory.Delete(outDir, true); } catch { /* 清理失败忽略 */ }
        }
    }

    [Fact]
    public void ExportAnimation_BlockedWhileEventsUnresolved_NoSilentBorrowing()
    {
        // 存在未指定事件时导出必须失败并说明原因，不能悄悄借用别的文件
        if (!DataAvailable()) return;
        var vm = new MainViewModel();
        var outDir = Path.Combine(Path.GetTempPath(), "gunmix_anim_" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(outDir);
            vm.ScanAnimations(AnimDir!, [SoundDir!]);
            var clip = vm.Project.Animations.First(c => c.Events.Count >= 2);
            // 只指定第一个，其余留空 → 必须拒绝导出
            clip.Events[0].AssetId = clip.Events[0].CandidateIds.Count > 0
                ? clip.Events[0].CandidateIds[0]
                : clip.Events[0].AssetId;
            for (int i = 1; i < clip.Events.Count; i++)
            {
                clip.Events[i].AssetId = null;
                clip.Events[i].Enabled = true;
            }
            Assert.NotNull(clip.Events[0].AssetId);

            var path = Path.Combine(outDir, "x.wav");
            var result = vm.ExportAnimation(clip, path, 24, false, 1, null, true, -60, 120);
            Assert.True(result is { Success: false }, "未解析事件不应导出成功");
            Assert.Contains("未解析事件", result!.Error);
            Assert.False(File.Exists(path), "失败导出不应留下文件");
        }
        finally
        {
            vm.Shutdown();
            try { Directory.Delete(outDir, true); } catch { /* 清理失败忽略 */ }
        }
    }

    [Fact]
    public void ScanAnimations_Idempotent_PreservesUserAssignments()
    {
        // 重复扫描不得覆盖用户已指定的文件（与分层工作台“手动优先”一致）
        if (!DataAvailable()) return;
        var vm = new MainViewModel();
        try
        {
            vm.ScanAnimations(AnimDir!, [SoundDir!]);
            var clip = vm.Project.Animations.First(c => c.Events.Count > 0);
            var ev = clip.Events.First();
            var chosen = ev.CandidateIds.Count > 0 ? ev.CandidateIds[0] : ev.AssetId!.Value;
            ev.AssetId = chosen;
            ev.AutoAssigned = false;
            ev.GainDb = -7.5;

            vm.ScanAnimations(AnimDir!, [SoundDir!]);
            var same = vm.Project.Animations.First(c => c.CastPath == clip.CastPath);
            var again = same.Events.First(e => e.Frame == ev.Frame && e.Alias == ev.Alias);
            Assert.Equal(chosen, again.AssetId);
            Assert.False(again.AutoAssigned);
            Assert.Equal(-7.5, again.GainDb);
        }
        finally
        {
            vm.Shutdown();
        }
    }
}
