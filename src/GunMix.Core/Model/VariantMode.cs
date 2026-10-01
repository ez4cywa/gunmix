namespace GunMix.Core.Model;

/// <summary>层内多个候选样本如何选取。</summary>
public enum VariantMode
{
    /// <summary>固定：始终使用指定文件。</summary>
    Fixed,

    /// <summary>顺序轮换：按明确列表循环。</summary>
    Rotation,

    /// <summary>随机：使用保存的随机种子与算法版本，池内不连续重复。</summary>
    Random,
}

public static class VariantAlgorithms
{
    /// <summary>变体随机算法版本标识；算法变化时必须更换，保证可重现。</summary>
    public const string RandomVersion = "splitmix32-v1";
}
