using System.IO;
using GunMix.App.ViewModels;
using GunMix.Core.Audio;
using GunMix.Core.Model;
using GunMix.Core.Persistence;
using GunMix.Core.Timeline;
using Xunit;

namespace GunMix.Core.Tests;

public class LayerAssetRefreshTests
{
    [Fact]
    public void ChangingPoolImmediatelyUpdatesDisplayedAssetAndBothManifests()
    {
        var (vm, assets) = CreateProject();
        try
        {
            var layer = vm.SelectedLayer!;
            vm.AssignPool(layer.Model, "replacement");
            var selected = vm.SelectedLayer!;
            Assert.Equal(layer.Id, selected.Id);
            Assert.Equal("replacement", selected.PoolDisplay);
            Assert.Equal(assets[2].FileName, selected.CurrentVariant);
            Assert.Equal(assets[2].Id, selected.FixedAssetId);
            Assert.EndsWith(assets[2].FileName, selected.PathDisplay);
            Assert.All(vm.CurrentRecipe!.SingleManifest!.Entries, e => Assert.Equal(assets[2].Id, e.AssetId));
            Assert.All(vm.CurrentRecipe.BurstManifest!.Entries, e => Assert.Equal(assets[2].Id, e.AssetId));
            Assert.False(vm.ManifestStale);
        }
        finally { vm.Shutdown(); }
    }

    [Fact]
    public void ChangingFixedSampleImmediatelyUpdatesNamePathAndPropertyNotifications()
    {
        var (vm, assets) = CreateProject();
        try
        {
            var layer = vm.SelectedLayer!;
            var changes = new List<string?>();
            layer.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
            layer.FixedAssetId = assets[1].Id;
            Assert.Equal(assets[1].FileName, layer.CurrentVariant);
            Assert.EndsWith(assets[1].FileName, layer.PathDisplay);
            Assert.Contains(nameof(LayerVm.CurrentVariant), changes);
            Assert.Contains(nameof(LayerVm.FixedAssetId), changes);
            Assert.All(vm.CurrentRecipe!.BurstManifest!.Entries, e => Assert.Equal(assets[1].Id, e.AssetId));
        }
        finally { vm.Shutdown(); }
    }

    [Fact]
    public void ContextMenuFixedSelectionIsOneUndoableEdit()
    {
        var (vm, assets) = CreateProject();
        try
        {
            var layer = vm.SelectedLayer!;
            layer.VariantMode = VariantMode.Random;
            vm.AssignFixedAsset(layer.Model, assets[1].Id);
            Assert.Equal(VariantMode.Fixed, layer.VariantMode);
            Assert.Equal(assets[1].FileName, layer.CurrentVariant);
            vm.DoUndo();
            Assert.Equal(VariantMode.Random, vm.SelectedLayer!.VariantMode);
            Assert.Equal(assets[0].Id, vm.SelectedLayer.FixedAssetId);
            vm.DoRedo();
            Assert.Equal(assets[1].FileName, vm.SelectedLayer!.CurrentVariant);
            Assert.Equal(VariantMode.Fixed, vm.SelectedLayer.VariantMode);
        }
        finally { vm.Shutdown(); }
    }

    [Fact]
    public void SwitchingOneLayersSamplePreservesOtherLayersManualEventSelections()
    {
        var (vm, assets) = CreateProject();
        try
        {
            var other = new Layer { Name = "other", Role = LayerRoles.Mech, PoolGroupKey = "original", VariantMode = VariantMode.Rotation };
            vm.CurrentRecipe!.Layers.Add(other);
            vm.RegenerateManifests();
            var manual = vm.CurrentRecipe.BurstManifest!.Entries.First(e => e.LayerId == other.Id && e.ShotIndex == 0);
            manual.AssetId = assets[1].Id;
            vm.AssignFixedAsset(vm.SelectedLayer!.Model, assets[1].Id);
            Assert.Equal(assets[1].Id, vm.CurrentRecipe.BurstManifest.Entries.First(e => e.LayerId == other.Id && e.ShotIndex == 0).AssetId);
        }
        finally { vm.Shutdown(); }
    }

    [Fact]
    public async Task StopDuringFilePreviewPreparationPreventsLatePlayback()
    {
        var (vm, assets) = CreateProject();
        try
        {
            var pending = vm.PreviewAssetAsync(assets[0]);
            vm.StopPlayback(immediate: true);
            await pending;
            await Task.Delay(100);
            Assert.False(vm.Playback.IsPlaying);
        }
        finally { vm.Shutdown(); }
    }

    private static (MainViewModel Vm, List<AssetInfo> Assets) CreateProject()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gunmix_layer_refresh_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var weapon = new Weapon { Name = "test" };
        var assets = Enumerable.Range(0, 3).Select(i => new AssetInfo
        {
            FileName = $"sample_{i}.wav", SourceDirectory = dir, WeaponId = weapon.Id,
            GroupKey = i == 2 ? "replacement" : "original", Format = new(48000, 32, 1, true, 480)
        }).ToList();
        foreach (var asset in assets)
        {
            var data = Enumerable.Repeat(0.1f, 480).ToArray(); var bytes = new byte[data.Length * 4];
            Buffer.BlockCopy(data, 0, bytes, 0, bytes.Length);
            WavWriter.Write(Path.Combine(dir, asset.FileName), 48000, 32, true, 1, bytes);
        }
        var layer = new Layer { Name = "SHOT", Role = LayerRoles.Shot, PoolGroupKey = "original",
            VariantMode = VariantMode.Fixed, FixedAssetId = assets[0].Id };
        var recipe = new Recipe { Name = "Test", Layers = [layer] };
        recipe.SingleManifest = TimelineCompiler.BuildManifest(recipe, assets, weapon.Id, ManifestKind.Single);
        recipe.BurstManifest = TimelineCompiler.BuildManifest(recipe, assets, weapon.Id, ManifestKind.Burst);
        weapon.Recipes = [recipe]; weapon.ActiveRecipeId = recipe.Id;
        var project = new GunProject { Weapons = [weapon], Assets = assets, ActiveWeaponId = weapon.Id, SourceRoot = dir };
        string path = Path.Combine(dir, "project.gunmix.json"); ProjectStore.Save(project, path);
        var vm = new MainViewModel(); vm.OpenProjectPath(path);
        Assert.True(vm.ProjectLoaded);
        return (vm, assets);
    }
}
