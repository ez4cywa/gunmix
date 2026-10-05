using GunMix.Core.Model;

namespace GunMix.Core.Fire;

/// <summary>Bind only the content explicitly referenced by a bank; ownership is an editing group, not an audio identity.</summary>
public static class BankAssetBinding
{
    public static AssetInfo? Resolve(SoundDefinitionRow row, IEnumerable<AssetInfo> assets, Guid weaponId)
    {
        if (string.IsNullOrEmpty(row.SourceHash)) return null;
        return assets.Where(a => string.Equals(a.Sha256, row.SourceHash, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => row.ResolvedPath != null && string.Equals(
                Path.Combine(a.SourceDirectory, a.FileName), row.ResolvedPath, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(a => a.WeaponId == weaponId)
            .ThenBy(a => a.Id)
            .FirstOrDefault();
    }

    public static bool CanOverride(ShotEvent instance, AssetInfo asset, Recipe recipe, Guid weaponId)
    {
        if (asset.WeaponId == weaponId) return true;
        var bank = recipe.FireProfile?.Banks.FirstOrDefault(b => b.BankKey == instance.BankKey);
        return bank?.Rows.Any(r => r.Alias == instance.AliasId && !string.IsNullOrEmpty(r.SourceHash)
            && string.Equals(r.SourceHash, asset.Sha256, StringComparison.OrdinalIgnoreCase)) == true;
    }
}
