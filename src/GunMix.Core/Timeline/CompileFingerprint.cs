using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GunMix.Core.Model;

namespace GunMix.Core.Timeline;

public static class CompileFingerprint
{
    public static string Create(Recipe recipe,ManifestKind kind,IEnumerable<AssetInfo>? assets=null,Guid? weaponId=null)
    {
        var input=new{Version="plan-v3",Kind=kind,recipe.BurstRpm,recipe.BurstShotCount,recipe.RandomSeed,recipe.FireProfile,
            Layers=recipe.Layers.Select(l=>new{l.Id,l.Enabled,l.GainDb,l.DelayMs,l.PitchRatio,l.PoolGroupKey,l.PoolAssetIds,l.VariantMode,
                l.FixedAssetId,l.Seed,l.IsExperimental,l.Trigger,l.TriggerShotNumber,l.BurstVoiceLimit,l.FireTrigger,l.BankKey,l.AliasId}),
            Assets=assets?.Where(a=>weaponId==null||a.WeaponId==weaponId).OrderBy(a=>a.Id).Select(a=>new{a.Id,a.WeaponId,a.GroupKey,a.Sha256,a.Format})};
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(input))));
    }
}
