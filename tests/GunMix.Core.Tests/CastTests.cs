using System.IO;
using GunMix.Core.Cast;
using GunMix.Core.Model;
using GunMix.Core.SoundBanks;
using Xunit;

namespace GunMix.Core.Tests;

/// <summary>
/// CAST 解析与动画音效装配。真实动画/音频目录存在时执行实测校验，否则跳过。
/// </summary>
public class CastTests
{
    // 真实素材路径由环境变量 GUNMIX_ASSETS_ROOT 提供；未设置时相关测试自动跳过，
    // 因此开源仓库不含任何本机绝对路径。
    private static string? AnimDir => TestPaths.AnimDir;
    private static string? SoundDir => TestPaths.WpnRoot;
    private static string? SoundsRoot => TestPaths.SoundsRoot;
    private static string? BankDir => TestPaths.BankDir;

    private static bool DataAvailable() => TestPaths.Ready(AnimDir!, SoundDir!);

    [Fact]
    public void Parse_FireCast_HasNoAudioNote_OnlyAnimationNotes()
    {
        // 实测：开火动画本身不含 AudioOneShot（枪声由游戏逻辑触发，不由视图模型动画通知驱动）
        if (!DataAvailable()) return;
        var (clip, error) = CastFile.TryRead(Path.Combine(AnimDir!, "rex_vm_ar_mike4_fire.cast"));
        Assert.Null(error);
        Assert.NotNull(clip);
        Assert.Empty(clip!.AudioEvents);
        Assert.Contains(clip.OtherNotes, n => n.Name == "end");
        Assert.Equal(60.0, clip.Framerate);
    }

    [Fact]
    public void Parse_ReloadCast_ExtractsAudioAliasesWithFrames()
    {
        if (!DataAvailable()) return;
        var (clip, error) = CastFile.TryRead(Path.Combine(AnimDir!, "rex_vm_ar_mike4_inspect.cast"));
        Assert.Null(error);
        Assert.NotNull(clip);
        Assert.Equal(30.0, clip!.Framerate);
        Assert.Equal(6, clip.AudioEvents.Count);
        Assert.StartsWith("wfoly_rex_plr_ar_mike4_inspect_", clip.AudioEvents[0].Alias);
        // 帧号必须单调不减（已排序）
        for (int i = 1; i < clip.AudioEvents.Count; i++)
            Assert.True(clip.AudioEvents[i].Frame >= clip.AudioEvents[i - 1].Frame);
    }

    [Fact]
    public void Parse_AllCasts_NoCrash_AndCountsMatchProbe()
    {
        // 与 Python 探测基线一致：181 个 cast、249 个 AudioOneShot 事件。
        // 注意 TryRead 按文件合并动画节点，因此“无音频”按文件计（探测脚本按节点计）。
        if (!DataAvailable()) return;
        var files = Directory.GetFiles(AnimDir!, "*.cast", SearchOption.AllDirectories);
        Assert.Equal(181, files.Length);

        int audioTotal = 0, noAudio = 0, errors = 0;
        foreach (var f in files)
        {
            var (clip, error) = CastFile.TryRead(f);
            if (error != null) { errors++; continue; }
            Assert.NotNull(clip);
            if (clip!.AudioEvents.Count == 0) noAudio++;
            audioTotal += clip.AudioEvents.Count;
        }
        Assert.Equal(0, errors);
        Assert.Equal(249, audioTotal);
        Assert.Equal(181, noAudio + (files.Length - noAudio));
        Assert.True(noAudio > 100, $"多数动画不含音频通知（纯 IK/LOD），实测无音频文件数={noAudio}");
    }

    [Fact]
    public void Scan_WithoutBank_UsesNameHeuristicsOnly()
    {
        if (!DataAvailable()) return;
        var report = AnimationAssembler.Scan(AnimDir!, [SoundDir!], []);
        Assert.Equal(249, report.TotalEvents);
        Assert.Equal(0, report.BankEvents);
        Assert.Equal(0, report.BanksLoaded);
        // 没有 bank 时不得出现任何“权威”来源
        Assert.All(report.Clips.SelectMany(c => c.Events), ev =>
        {
            Assert.False(ev.IsBankAuthoritative);
            Assert.True(ev.AutoAssigned ? ev.Source is AnimSource.NameExact or AnimSource.NameSingle
                                        : ev.AssetId == null || !ev.AutoAssigned);
        });
    }

    [Fact]
    public void SoundBank_AutoDetectsSoundsRootWhenGivenTooDeepDir()
    {
        // GUI 传的是 …\sounds\rex\wpn（比真正的根更深）；必须自动上溯到 …\sounds 才能落地
        if (!DataAvailable() || !Directory.Exists(BankDir)) return;
        string tooDeep = SoundDir!;                    // …\sounds\rex\wpn
        string correct = SoundsRoot!;                  // …\sounds

        var fromDeep = SoundBankIndex.Load(BankDir!, tooDeep);
        var fromRoot = SoundBankIndex.Load(BankDir!, correct);

        Assert.Equal(fromRoot.ResolvedAliasCount, fromDeep.ResolvedAliasCount);
        Assert.True(fromDeep.ResolvedAliasCount > 1000,
            $"自动定位根目录失败：命中 {fromDeep.ResolvedAliasCount}，实际根 {fromDeep.SoundsRootUsed}");
    }

    [Fact]
    public void Scan_WithSoundBank_ResolvesAuthoritatively()
    {
        if (!DataAvailable() || !Directory.Exists(BankDir)) return;
        var banks = SoundBankIndex.Load(BankDir!, SoundsRoot!);
        Assert.True(banks.Banks.Count >= 150, $"应加载 weapon_rex_* bank，实际 {banks.Banks.Count}");
        Assert.True(banks.ResolvedAliasCount > 1000);

        var report = AnimationAssembler.Scan(AnimDir!, [SoundDir!], [], banks: banks);

        // 实测基线：249 事件中 173 个由 soundbank 权威落地，其中 26 个是随机容器
        Assert.Equal(249, report.TotalEvents);
        Assert.Equal(173, report.BankEvents);
        Assert.Equal(26, report.BankContainers);
        // bank 命中的事件必须自动采用且来源为 bank
        foreach (var ev in report.Clips.SelectMany(c => c.Events).Where(e => e.IsBankAuthoritative))
        {
            Assert.NotNull(ev.AssetId);
            Assert.True(ev.AutoAssigned);
            Assert.NotEmpty(ev.ContainerIds);
            Assert.Contains(ev.AssetId.Value, ev.ContainerIds);
        }
        // bank 未命中的事件不得伪装成权威
        foreach (var ev in report.Clips.SelectMany(c => c.Events).Where(e => !e.IsBankAuthoritative))
            Assert.False(ev.AutoAssigned && ev.Source is AnimSource.BankSingle or AnimSource.BankContainer);
    }

    [Fact]
    public void SoundBank_ContainerMembersAreAllRealExportedFiles()
    {
        if (!DataAvailable() || !Directory.Exists(BankDir)) return;
        var banks = SoundBankIndex.Load(BankDir!, SoundsRoot!);
        foreach (var alias in banks.Aliases.Take(400))
        {
            foreach (var e in banks.Lookup(alias))
            {
                Assert.False(e.ResolvedPath.StartsWith("sound_"), $"占位路径不应进入索引：{e.ResolvedPath}");
                Assert.True(File.Exists(e.ResolvedPath), $"bank 指向的文件不存在：{e.ResolvedPath}");
            }
        }
    }

    [Fact]
    public void SoundBank_ParsesWeaponAndPerspectiveFromName()
    {
        var a = SoundBankIndex.ParseBankName("weapon_rex_ar_mike4_plr")!;
        Assert.Equal("ar_mike4", a.WeaponKey);
        Assert.Equal("plr", a.Perspective);
        Assert.False(a.Suppressed);
        Assert.Equal("", a.Variant);

        var b = SoundBankIndex.ParseBankName("weapon_rex_dm_findia_plr_sup")!;
        Assert.Equal("dm_findia", b.WeaponKey);
        Assert.True(b.Suppressed);

        var c = SoundBankIndex.ParseBankName("weapon_rex_sm_spier9_plr_reload_drum")!;
        Assert.Equal("reload_drum", c.Variant);

        Assert.Null(SoundBankIndex.ParseBankName("merged_ingame_sp.all"));
    }

    [Fact]
    public void WeaponKey_ParsedFromCastAndAliasNames()
    {
        Assert.Equal("ar_mike4", AnimationSoundLibrary.WeaponKeyOf("rex_vm_ar_mike4_inspect"));
        Assert.Equal("pi_hotel45", AnimationSoundLibrary.WeaponKeyOf("rex_vm_pi_hotel45_knife_melee_hit_01"));
        Assert.Equal("rex_mike4", AnimationSoundLibrary.ReloadsDirKey("ar_mike4"));
        Assert.Equal("rex_hotel45", AnimationSoundLibrary.ReloadsDirKey("pi_hotel45"));
        Assert.Null(AnimationSoundLibrary.WeaponKeyOf("some_random_name"));
    }

    [Fact]
    public void BaseName_StripsEngineSuffix()
    {
        Assert.Equal("rex_vm_ar_mike4_inspect_magin",
            AnimationSoundLibrary.BaseName("rex_vm_ar_mike4_inspect_magin.lnn.85.48000.all.wav"));
        Assert.Equal("weap_rex_mike4_fire_plr_shot_01",
            AnimationSoundLibrary.BaseName("weap_rex_mike4_fire_plr_shot_01.qnn.85.48000.all.wav"));
    }

    [Fact]
    public void Scan_RealData_ResolvesWithoutCrossWeaponBorrowing()
    {
        if (!DataAvailable()) return;
        var report = AnimationAssembler.Scan(AnimDir!, [SoundDir!], []);

        Assert.Equal(181, report.CastFilesSeen);
        Assert.Empty(report.Failures);
        Assert.Equal(249, report.TotalEvents);
        Assert.True(report.ClipsWithAudio > 0);

        // 每个事件的候选必须属于同一武器目录，绝不跨武器借用
        foreach (var clip in report.Clips)
        {
            Assert.False(string.IsNullOrEmpty(clip.WeaponKey), $"动画 {clip.Name} 未解析出武器标识");
            foreach (var ev in clip.Events)
            {
                foreach (var cand in ev.CandidateIds)
                {
                    var asset = report.NewAssets.FirstOrDefault(a => a.Id == cand)
                                ?? throw new InvalidOperationException("候选不在新素材表");
                    var dir = Path.GetFileName(asset.SourceDirectory);
                    Assert.True(
                        dir.Equals(clip.WeaponKey, StringComparison.OrdinalIgnoreCase) ||
                        dir.Equals(AnimationSoundLibrary.ReloadsDirKey(clip.WeaponKey), StringComparison.OrdinalIgnoreCase),
                        $"候选 {asset.FileName}（{dir}）不属于武器 {clip.WeaponKey}");
                }
                // 无 bank 时：自动采用的必须唯一
                if (ev.AutoAssigned)
                    Assert.Single(ev.CandidateIds);
                // 多候选、近似候选与无候选不得自动采用
                if (ev.Source is AnimSource.NameMultiple or AnimSource.NameFuzzy or AnimSource.Unresolved)
                    Assert.Null(ev.AssetId);
                // 近似候选必须真的有候选可挑
                if (ev.Source == AnimSource.NameFuzzy)
                    Assert.True(ev.CandidateIds.Count > 0);
            }
        }
    }

    [Fact]
    public void ContainerSelection_IsDeterministicAndStaysInsideBankMembers()
    {
        // 随机容器：同种子同选择（可重现），换种子仍在原版成员集合内，绝不越界
        var members = Enumerable.Range(0, 4).Select(i => new AssetInfo
        {
            FileName = $"m{i}.wav",
            Format = new AudioFormatInfo(48000, 16, 1, false, 4800),
        }).ToList();
        var clip = new AnimationClip { Name = "rex_vm_ar_mike4_reload", Framerate = 30.0, Seed = 42 };
        var ev = new AnimEvent
        {
            Frame = 12,
            Alias = "wfoly_rex_plr_ar_mike4_reload_04",
            Source = AnimSource.BankContainer,
            ContainerIds = [.. members.Select(m => m.Id)],
            AssetId = members[0].Id,
            AutoAssigned = true,
            ContainerMode = VariantMode.Random,
        };
        clip.Events.Add(ev);

        var first = AnimationAssembler.PickContainerMember(clip, ev);
        var again = AnimationAssembler.PickContainerMember(clip, ev);
        Assert.Equal(first, again);                                  // 可重现
        Assert.Contains(first!.Value, ev.ContainerIds);              // 在原版成员内

        var seen = new HashSet<Guid>();
        for (int i = 0; i < 40; i++)
        {
            clip.Seed = 1000 + i;
            var pick = AnimationAssembler.PickContainerMember(clip, ev)!.Value;
            Assert.Contains(pick, ev.ContainerIds);
            seen.Add(pick);
        }
        Assert.True(seen.Count > 1, "换种子应能在容器成员间产生变化");

        // 用户显式指定优先于容器选样
        ev.AutoAssigned = false;
        ev.AssetId = members[2].Id;
        Assert.Equal(members[2].Id, AnimationAssembler.PickContainerMember(clip, ev));
    }

    [Fact]
    public void BankMissing_EventReportsUnexportedFilesInsteadOfBorrowing()
    {
        if (!DataAvailable() || !Directory.Exists(BankDir)) return;
        var banks = SoundBankIndex.Load(BankDir!, SoundsRoot!);
        var report = AnimationAssembler.Scan(AnimDir!, [SoundDir!], [], banks: banks);
        foreach (var ev in report.Clips.SelectMany(c => c.Events).Where(e => e.Source == AnimSource.BankMissing))
        {
            Assert.Null(ev.AssetId);
            Assert.True(ev.BankMissingCount > 0, "bank 未导出计数应大于 0");
            Assert.Empty(ev.ContainerIds);
        }
    }

    [Fact]
    public void BuildTimeline_UsesFrameOverFramerate()
    {
        var clip = new AnimationClip { Name = "rex_vm_ar_mike4_inspect", WeaponKey = "ar_mike4", Framerate = 30.0 };
        var asset = new AssetInfo
        {
            FileName = "rex_vm_ar_mike4_inspect_magin.lnn.85.48000.all.wav",
            Format = new AudioFormatInfo(48000, 16, 1, false, 48000),
        };
        clip.Events.Add(new AnimEvent { Frame = 30, Alias = "a", AssetId = asset.Id, Enabled = true });
        clip.Events.Add(new AnimEvent { Frame = 75, Alias = "b", AssetId = asset.Id, Enabled = true });

        var timeline = AnimationAssembler.BuildTimeline(clip, [asset], 48000);
        Assert.Empty(timeline.Issues);
        Assert.Equal(2, timeline.Events.Count);
        Assert.Equal(48000, timeline.Events[0].StartSample);   // 30 帧 / 30 fps = 1 s
        Assert.Equal(120000, timeline.Events[1].StartSample);  // 75 / 30 = 2.5 s
        Assert.Equal(120000 + 48000, timeline.TotalSamples);   // 尾部保留
    }

    [Fact]
    public void BuildTimeline_UnresolvedEventReportedNotGuessed()
    {
        var clip = new AnimationClip { Name = "rex_vm_ar_mike4_x", Framerate = 30.0 };
        clip.Events.Add(new AnimEvent { Frame = 10, Alias = "wfoly_missing", AssetId = null });
        var timeline = AnimationAssembler.BuildTimeline(clip, [], 48000);
        Assert.Empty(timeline.Events);
        var issue = Assert.Single(timeline.Issues);
        Assert.Contains("尚未指定音频文件", issue.Message);
    }

    [Fact]
    public void BuildTimeline_MasterGainAppliesToAllEvents()
    {
        var clip = new AnimationClip { Framerate = 30.0, MasterGainDb = -6.0 };
        var asset = new AssetInfo { FileName = "a.wav", Format = new AudioFormatInfo(48000, 16, 1, false, 100) };
        clip.Events.Add(new AnimEvent { Frame = 0, Alias = "x", AssetId = asset.Id, GainDb = -3.0 });
        var timeline = AnimationAssembler.BuildTimeline(clip, [asset], 48000);
        Assert.Equal(-9.0, Assert.Single(timeline.Events).GainDb);
    }
}
