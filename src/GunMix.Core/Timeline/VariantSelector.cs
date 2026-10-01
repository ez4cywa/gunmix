using GunMix.Core.Audio;
using GunMix.Core.Model;

namespace GunMix.Core.Timeline;

/// <summary>
/// 变体选择：固定 / 顺序轮换 / 带种子的随机（池内不连续重复；池只有一个文件时允许重复）。
/// 随机算法版本固定为 splitmix32-v1，同种子同池结果可重现。
/// </summary>
public static class VariantSelector
{
    /// <summary>按层生成 shotCount 次选择（shot 索引 0 起）。</summary>
    public static List<Guid> SelectForLayer(Layer layer, Recipe recipe, List<AssetInfo> pool, int shotCount, ManifestKind kind)
    {
        var result = new List<Guid>(shotCount);
        if (pool.Count == 0) return result;

        switch (layer.VariantMode)
        {
            case VariantMode.Fixed:
            {
                var fixedId = layer.FixedAssetId ?? pool[0].Id;
                for (int i = 0; i < shotCount; i++) result.Add(fixedId);
                break;
            }
            case VariantMode.Rotation:
            {
                // 轮换：按明确列表循环。单发固定取列表第一个，保证“手册五层对照”教学配方可重现。
                if (kind == ManifestKind.Single)
                {
                    var id = layer.FixedAssetId is { } f && pool.Any(p => p.Id == f) ? f : pool[0].Id;
                    result.Add(id);
                }
                else
                {
                    for (int i = 0; i < shotCount; i++) result.Add(pool[i % pool.Count].Id);
                }
                break;
            }
            case VariantMode.Random:
            {
                int seed = layer.Seed ?? recipe.RandomSeed;
                uint state = Hash(seed, layer.Id);
                var rng = new SplitMix32(state);
                int prevIndex = -1;
                for (int i = 0; i < shotCount; i++)
                {
                    int index = (int)(rng.Next() % (uint)pool.Count);
                    if (index == prevIndex && pool.Count > 1)
                        index = (index + 1) % pool.Count; // 池内不连续重复
                    prevIndex = index;
                    result.Add(pool[index].Id);
                }
                break;
            }
        }
        return result;
    }

    public static uint Hash(int seed, Guid id)
    {
        uint h = (uint)seed ^ 0x811C9DC5u;
        foreach (var b in id.ToByteArray())
        {
            h ^= b;
            h *= 16777619u;
        }
        return h;
    }
}
