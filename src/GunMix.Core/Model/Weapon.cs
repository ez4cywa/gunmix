namespace GunMix.Core.Model;

/// <summary>一把武器：独立素材、配方、类型和导出设置。切换武器不沿用上一把枪的样本池、射速或导出名称。</summary>
public sealed class Weapon
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    public string TypeName { get; set; } = WeaponTypes.AssaultRifle;

    public List<Recipe> Recipes { get; set; } = [];

    public Guid ActiveRecipeId { get; set; }

    public ExportSettings Export { get; set; } = new();

    public Recipe? ActiveRecipe => Recipes.FirstOrDefault(r => r.Id == ActiveRecipeId) ?? Recipes.FirstOrDefault();

    public Weapon Clone() => new()
    {
        Id = Guid.NewGuid(),
        Name = Name,
        TypeName = TypeName,
        Recipes = Recipes.Select(r => r.Clone()).ToList(),
        ActiveRecipeId = ActiveRecipeId,
        Export = Export.Clone(),
    };
}

/// <summary>导出设置（每把武器独立保存）。</summary>
public sealed class ExportSettings
{
    /// <summary>名称前缀，如 mike4 → mike4_single_001.wav。</summary>
    public string NamePrefix { get; set; } = "weapon";

    /// <summary>输出目录；空 = 与工程同目录。</summary>
    public string OutputDirectory { get; set; } = "";

    /// <summary>导出位深：16 / 24 / 32(float)。</summary>
    public int BitDepth { get; set; } = 24;

    public bool DitherEnabled { get; set; } = true;

    public int DitherSeed { get; set; } = 20260927;

    /// <summary>越界时的显式衰减（dBFS 目标，通常 -1）；null = 不自动处理。</summary>
    public double? AttenuateToDbfs { get; set; }

    /// <summary>自动截尾：截掉尾部低于阈值的静音，保留的自然尾巴毫秒数。随工程保存。</summary>
    public bool TrimTail { get; set; } = true;

    public double TrimThresholdDb { get; set; } = -60.0;

    public double TrimTailMs { get; set; } = 120.0;

    /// <summary>Source 引擎（L4D2 / GMod）导出目标：44.1 kHz / 16 bit + game_sounds 脚本。可选项，随工程保存。</summary>
    public bool SourceEngineTarget { get; set; }

    /// <summary>Source 目标下是否降混为单声道。</summary>
    public bool SourceMono { get; set; }

    /// <summary>自定义文件长度（秒）；null = 自动（按事件结束或截尾）。</summary>
    public double? CustomLengthSeconds { get; set; }

    public int SingleCounter { get; set; } = 1;

    public int BurstCounter { get; set; } = 1;

    public ExportSettings Clone() => new()
    {
        NamePrefix = NamePrefix,
        OutputDirectory = OutputDirectory,
        BitDepth = BitDepth,
        DitherEnabled = DitherEnabled,
        DitherSeed = DitherSeed,
        AttenuateToDbfs = AttenuateToDbfs,
        TrimTail = TrimTail,
        TrimThresholdDb = TrimThresholdDb,
        TrimTailMs = TrimTailMs,
        SourceEngineTarget = SourceEngineTarget,
        SourceMono = SourceMono,
        CustomLengthSeconds = CustomLengthSeconds,
        SingleCounter = SingleCounter,
        BurstCounter = BurstCounter,
    };
}
