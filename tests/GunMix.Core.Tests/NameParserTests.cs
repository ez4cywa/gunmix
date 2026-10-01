using GunMix.Core.Assets;
using Xunit;

namespace GunMix.Core.Tests;

public class NameParserTests
{
    [Theory]
    [InlineData("weap_rex_mike4_fire_plr_shot_01.qnn.85.48000.all.wav", "mike4", "fire", "plr", "shot", 1, "qnn", "85")]
    [InlineData("weap_rex_mike4_fire_plr_lfe.hnn.100.48000.all.wav", "mike4", "fire", "plr", "lfe", null, "hnn", "100")]
    [InlineData("weap_rex_mike4_sup_plr_last_shot_02.hnn.85.48000.all.wav", "mike4", "sup", "plr", "last_shot", 2, "hnn", "85")]
    [InlineData("weap_rex_mike4_fire_plr_mech_ads_07.qnn.85.48000.all.wav", "mike4", "fire", "plr", "mech_ads", 7, "qnn", "85")]
    [InlineData("weap_rex_mike4_fire_npc_shot_med_mech_03.qnn.85.48000.all.wav", "mike4", "fire", "npc", "shot_med_mech", 3, "qnn", "85")]
    [InlineData("weap_rex_mike4_fcg_deadtrig_plr_02.hnn.85.48000.all.wav", "mike4", "fcg", "plr", "fcg_deadtrig_plr", 2, "hnn", "85")]
    [InlineData("weap_rex_mike4_fire_plr_interrupt_int_04.qnn.85.48000.all.wav", "mike4", "fire", "plr", "interrupt_int", 4, "qnn", "85")]
    public void ParsesKnownNames(string file, string weapon, string category, string perspective, string group, int? variant, string code, string quality)
    {
        var r = NameParser.Parse(file);
        Assert.Equal(weapon, r.Weapon);
        Assert.Equal(category, r.Category);
        Assert.Equal(perspective, r.Perspective);
        Assert.Equal(group, r.Group);
        Assert.Equal(variant, r.Variant);
        Assert.Equal(code, r.SuffixCode);
        Assert.Equal(quality, r.SuffixQuality);
    }

    [Fact]
    public void SuffixQualityIsNeverVolume()
    {
        // 不得把 .85 自动转成音量：后缀仅作为信息保留
        var r = NameParser.Parse("weap_rex_mike4_fire_plr_shot_05.qnn.85.48000.all.wav");
        Assert.Equal("85", r.SuffixQuality);
    }

    [Fact]
    public void UnknownNameGoesUngrouped()
    {
        var r = NameParser.Parse("my_custom_sound_a.wav");
        Assert.Null(r.Weapon);
        Assert.Equal("my_custom_sound_a", r.Group);
        Assert.Equal("my_custom_sound_a", r.GroupKey);
    }

    [Fact]
    public void GroupKeyCompose()
    {
        var r = NameParser.Parse("weap_rex_mike4_fire_plr_shot_01.qnn.85.48000.all.wav");
        Assert.Equal("fire_plr_shot", r.GroupKey);
    }

    [Theory]
    [InlineData("fire_plr_shot", "普通玩家")]
    [InlineData("fire_plr_mech_ads", "ADS 候选")]
    [InlineData("sup_plr_shot", "消音")]
    [InlineData("fire_npc_shot", "NPC 距离候选")]
    [InlineData("fcg_prefire_plr", "实验分支（未启用）")]
    [InlineData("fire_plr_last_shot", "实验分支（未启用）")]
    [InlineData("wfoly_plr_dm_findia_apex_altmode_bar_sht_inspect_end", "换弹 / Foley")]
    [InlineData("rex_vm_pi_hotel45_reload_empty_magout", "换弹 / Foley")]
    [InlineData("apex_knife_plr_swt", "其他")]
    [InlineData("", "未分组")]
    public void BucketsAreCorrect(string groupKey, string bucket)
    {
        Assert.Equal(bucket, NameParser.BucketOf(groupKey));
    }
}
