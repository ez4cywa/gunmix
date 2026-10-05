using GunMix.Core.Fire;
using GunMix.Core.Model;
using GunMix.Core.Timeline;
using GunMix.Core.Audio;
using GunMix.Core.Mixing;
using Xunit;

namespace GunMix.Core.Tests;

public class SharedFireAssetTests
{
    private static (Recipe Recipe, List<AssetInfo> Assets, Guid Weapon) Fixture()
    {
        var weapon = Guid.NewGuid();
        var local = new AssetInfo { WeaponId = weapon, Sha256 = "shot", FileName = "shot.wav", Format = new(48000, 16, 1, false, 480) };
        var shared = new AssetInfo { WeaponId = Guid.NewGuid(), Sha256 = "atmo", FileName = "atmo.wav", Format = local.Format };
        var bank = new SoundDefinitionBank { BankKey = "mike4", Rows = [
            new() { Alias = "shot", Secondary = "atmo", Snd = "rex/wpn/ar_mike4/shot", SourceHash = local.Sha256 },
            new() { Alias = "atmo", Snd = "rex/wpn/atmo_npc/exterior/ar/atmo", SourceHash = shared.Sha256 }] };
        var recipe = new Recipe { FireProfile = new() { Enabled = true, Banks = [bank], Relations = [
            new() { BankKey = bank.BankKey, FromAlias = "shot", ToAlias = "atmo", Enabled = true, DelayMs = 10, GainDb = -6 }] },
            Layers = [new() { Name = "SHOT", BankKey = bank.BankKey, AliasId = "shot" }] };
        return (recipe, [local, shared], weapon);
    }

    [Fact]
    public void ExplicitBankReferenceCanMixSharedAssetWithoutChangingOwnership()
    {
        var (recipe, assets, weapon) = Fixture();
        var manifest = TimelineCompiler.BuildManifest(recipe, assets, weapon, ManifestKind.Single);
        var plan = TimelineCompiler.Compile(recipe, manifest, assets, weapon, 48000, ManifestKind.Single);
        Assert.Equal(2, plan.EventCount);
        var child = Assert.Single(plan.Events, e => e.AliasId == "atmo");
        Assert.Equal(assets[1].Id, child.AssetId);
        Assert.Equal(480, child.StartSample);
        Assert.Equal(-6, child.GainDb);
        Assert.Equal(plan.Events[0].InstanceId, child.ParentInstanceId);
        Assert.NotEqual(weapon, assets[1].WeaponId);
        Assert.DoesNotContain(plan.Issues, i => i.IsError);
        Assert.Equal("rex/wpn/atmo_npc/exterior/ar/atmo", child.SoundReference);
        Assert.Equal(assets[1].WeaponId, child.AssetOwnerWeaponId);
        var mix = MixKernel.Render(plan, _ => new AssetBuffer { Data = Enumerable.Repeat(0.5f, 480).ToArray(),
            Channels = 1, SampleRate = 48000, FrameCount = 480 });
        Assert.Equal(960, mix.Frames);
        Assert.Equal(0.5f, mix.Data[0]);
        Assert.Equal(0.5 * Math.Pow(10, -6 / 20.0), mix.Data[960], 6);
    }

    [Fact]
    public void ReferencedSharedAssetChangesInvalidateManifestButUnrelatedAssetsDoNot()
    {
        var (recipe, assets, weapon) = Fixture();
        var manifest = TimelineCompiler.BuildManifest(recipe, assets, weapon, ManifestKind.Single);
        assets.Add(new() { WeaponId = Guid.NewGuid(), Sha256 = "unrelated" });
        Assert.True(manifest.Matches(recipe, ManifestKind.Single, assets, weapon));
        assets[1].Sha256 = "changed";
        Assert.False(manifest.Matches(recipe, ManifestKind.Single, assets, weapon));
    }

    [Fact]
    public void SameFileNameWithDifferentContentCannotReplaceMissingSharedReference()
    {
        var (recipe, assets, weapon) = Fixture();
        assets[1].Sha256 = "wrong-content";
        var plan = FirePlanCompiler.Build(recipe, assets, weapon, 48000, ManifestKind.Single);
        Assert.Single(plan.Events);
        Assert.Contains(plan.Issues, i => i.IsError && i.Message.Contains("atmo"));
    }

    [Fact]
    public void FixedBankMemberUsesTheSelectedAsset()
    {
        var (recipe, assets, weapon) = Fixture();
        recipe.FireProfile!.Relations.Clear();
        recipe.FireProfile.Banks[0].Rows.Add(new() { Alias = "shot", SourceHash = assets[1].Sha256 });
        recipe.Layers[0].VariantMode = VariantMode.Fixed;
        recipe.Layers[0].FixedAssetId = assets[1].Id;
        var plan = FirePlanCompiler.Build(recipe, assets, weapon, 48000, ManifestKind.Single);
        Assert.Equal(assets[1].Id, Assert.Single(plan.Events).AssetId);
    }

    [Fact]
    public void SharedManualSelectionIsRetainedOnlyForExplicitBankMember()
    {
        var (recipe, assets, weapon) = Fixture();
        var alternative = new AssetInfo { WeaponId = Guid.NewGuid(), Sha256 = "alt-atmo", Format = assets[1].Format };
        assets.Add(alternative);
        recipe.FireProfile!.Banks[0].Rows.Add(new() { Alias = "atmo", SourceHash = alternative.Sha256 });
        var manifest = TimelineCompiler.BuildManifest(recipe, assets, weapon, ManifestKind.Single);
        manifest.Entries.Single(e => e.AssetId == assets[1].Id).AssetId = alternative.Id;
        var plan = TimelineCompiler.Compile(recipe, manifest, assets, weapon, 48000, ManifestKind.Single);
        Assert.Equal(alternative.Id, Assert.Single(plan.Events, e => e.AliasId == "atmo").AssetId);
        // A foreign asset outside the definition may not be substituted.
        recipe.FireProfile.Banks[0].Rows.RemoveAt(2);
        var fresh = TimelineCompiler.BuildManifest(recipe, assets, weapon, ManifestKind.Single);
        fresh.Entries.Single(e => e.AssetId == assets[1].Id).AssetId = alternative.Id;
        var rejected = TimelineCompiler.Compile(recipe, fresh, assets, weapon, 48000, ManifestKind.Single);
        Assert.Equal(assets[1].Id, Assert.Single(rejected.Events, e => e.AliasId == "atmo").AssetId);
    }

    [Fact]
    public void DuplicateContentPrefersExactDeclaredPath()
    {
        var (recipe, assets, weapon) = Fixture();
        var duplicate = new AssetInfo { WeaponId = weapon, Sha256 = assets[1].Sha256, FileName = "copy.wav", Format = assets[1].Format };
        assets.Insert(0, duplicate);
        var row = recipe.FireProfile!.Banks[0].Rows[1];
        var expected = assets.Single(a => a.FileName == "atmo.wav");
        expected.SourceDirectory = "D:\\shared";
        row.ResolvedPath = "D:\\shared\\atmo.wav";
        Assert.Equal(expected.Id, BankAssetBinding.Resolve(row, assets, weapon)!.Id);
    }

    [Fact]
    public void FixedMemberOutsideCurrentBankIsRejected()
    {
        var (recipe, assets, weapon) = Fixture();
        recipe.Layers[0].VariantMode = VariantMode.Fixed;
        recipe.Layers[0].FixedAssetId = assets[1].Id;
        var plan = FirePlanCompiler.Build(recipe, assets, weapon, 48000, ManifestKind.Single);
        Assert.Empty(plan.Events);
        Assert.Contains(plan.Issues, i => i.IsError && i.Message.Contains("固定样本"));
    }
}
