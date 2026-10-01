namespace GunMix.Core.Model;

/// <summary>一次渲染（单发或连发）的样本事件清单。播放、停止再播、导出都复用当前清单；导出从不可变快照读取。</summary>
public sealed class EventManifest
{
    /// <summary>生成时的清单类型。</summary>
    public ManifestKind Kind { get; set; }

    /// <summary>连发参数快照（Kind=Burst 时有效）。</summary>
    public int Rpm { get; set; }

    public int ShotCount { get; set; }

    /// <summary>变体策略与种子快照，用于判断清单是否失效。</summary>
    public string AlgorithmVersion { get; set; } = VariantAlgorithms.RandomVersion;

    public List<ManifestEntry> Entries { get; set; } = [];

    public EventManifest Clone() => new()
    {
        Kind = Kind,
        Rpm = Rpm,
        ShotCount = ShotCount,
        AlgorithmVersion = AlgorithmVersion,
        Entries = Entries.Select(e => e.Clone()).ToList(),
    };

    /// <summary>清单是否与当前配方参数匹配（发数、池内容、策略改变会使其失效）。</summary>
    public bool Matches(Recipe recipe, ManifestKind kind)
    {
        if (Kind != kind) return false;
        if (AlgorithmVersion != VariantAlgorithms.RandomVersion) return false;
        if (Entries.Count == 0) return false;
        if (kind == ManifestKind.Burst && (Rpm != recipe.BurstRpm || ShotCount != recipe.BurstShotCount)) return false;
        // 池内容 / 策略变化检测：清单中每层的每次选择仍必须存在于池中且策略指纹一致。
        return FingerprintsMatch(recipe, kind);
    }

    private bool FingerprintsMatch(Recipe recipe, ManifestKind kind)
    {
        foreach (var layer in recipe.Layers)
        {
            var entries = Entries.Where(e => e.LayerId == layer.Id).ToList();
            var enabledExpected = layer.Enabled && !(kind == ManifestKind.Single && layer.IsReleaseTail);
            if (enabledExpected != entries.Count > 0) return false;
            foreach (var e in entries)
            {
                if (!PoolContains(layer, e.AssetId)) return false;
            }
        }
        return true;
    }

    private static bool PoolContains(Layer layer, Guid assetId)
    {
        if (layer.PoolAssetIds.Count > 0) return layer.PoolAssetIds.Contains(assetId);
        return true; // 按分组键引用的池由调用方结合素材库检查。
    }
}

public enum ManifestKind
{
    Single,
    Burst,
}

public sealed class ManifestEntry
{
    public Guid LayerId { get; set; }

    /// <summary>0 起的发号；单发恒为 0。</summary>
    public int ShotIndex { get; set; }

    public Guid AssetId { get; set; }

    public ManifestEntry Clone() => new() { LayerId = LayerId, ShotIndex = ShotIndex, AssetId = AssetId };
}
