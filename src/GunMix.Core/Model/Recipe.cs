namespace GunMix.Core.Model;

/// <summary>一个混音配方（方案）。同一武器可保存多个配方；普通/ADS/消音/NPC 通过“复制为新配方”建立。</summary>
public sealed class Recipe
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    /// <summary>教学预设标记（普通玩家基础 / 手册五层对照），可编辑。</summary>
    public bool IsTeachingPreset { get; set; }

    /// <summary>由“按枪型自适应”生成时记录所用类型；重新生成只替换带此标记的配方。复制后清空。</summary>
    public string? AdaptiveProfile { get; set; }

    /// <summary>配方说明（自适应配方记录配平依据、缺失素材与复用情况）。</summary>
    public string Notes { get; set; } = "";

    public List<Layer> Layers { get; set; } = [];

    /// <summary>连发设置：射速 6–1800 RPM，发数 1–100。</summary>
    public int BurstRpm { get; set; } = 600;

    public int BurstShotCount { get; set; } = 6;

    /// <summary>配方级默认随机种子。</summary>
    public int RandomSeed { get; set; } = 42;

    /// <summary>已生成的事件清单（单发 / 连发）；保存于工程，播放与导出复用，改增益不重新抽样。</summary>
    public EventManifest? SingleManifest { get; set; }

    public EventManifest? BurstManifest { get; set; }

    public Recipe Clone() => new()
    {
        Id = Guid.NewGuid(),
        Name = Name,
        IsTeachingPreset = false,
        AdaptiveProfile = null,
        Notes = Notes,
        Layers = Layers.Select(l => l.Clone()).ToList(),
        BurstRpm = BurstRpm,
        BurstShotCount = BurstShotCount,
        RandomSeed = RandomSeed,
        SingleManifest = SingleManifest?.Clone(),
        BurstManifest = BurstManifest?.Clone(),
    };
}
