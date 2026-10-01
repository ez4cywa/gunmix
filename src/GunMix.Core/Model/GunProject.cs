using System.Text.Json.Serialization;
using GunMix.Core.Assets;

namespace GunMix.Core.Model;

/// <summary>顶层工程。保存格式版本、采样率、武器列表与素材；扩展名 .gunmix.json。</summary>
public sealed class GunProject
{
    public const int CurrentSchemaVersion = 1;
    public const string FileExtension = ".gunmix.json";

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string ProjectName { get; set; } = "未命名工程";

    public int SampleRate { get; set; } = 48000;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime LastSavedAt { get; set; }

    /// <summary>素材来源根目录（相对路径的基准）。</summary>
    public string SourceRoot { get; set; } = "";

    public List<Weapon> Weapons { get; set; } = [];

    public List<AssetInfo> Assets { get; set; } = [];

    /// <summary>动画音效装配（来自 .cast 的实测时间轴 + 用户确认的文件对应）。</summary>
    public List<Cast.AnimationClip> Animations { get; set; } = [];

    /// <summary>动画音频事件的绝对路径登记（素材表只存目录与文件名）。</summary>
    public Dictionary<string, string> AnimationAssetPaths { get; set; } = [];

    public Guid ActiveWeaponId { get; set; }

    /// <summary>A/B 比较快照：各保存一份配方结构与事件清单。</summary>
    public AbSnapshot? SnapshotA { get; set; }

    public AbSnapshot? SnapshotB { get; set; }

    /// <summary>分组规则版本（规则变化时递增；手动分组始终优先）。</summary>
    public int GroupingRuleVersion { get; set; } = NameParser.RuleVersion;

    /// <summary>手动分组覆盖：文件名 → 分组键。</summary>
    public Dictionary<string, string> ManualGroupOverrides { get; set; } = [];

    [JsonIgnore]
    public Weapon? ActiveWeapon => Weapons.FirstOrDefault(w => w.Id == ActiveWeaponId) ?? Weapons.FirstOrDefault();

    public GunProject Clone() => new()
    {
        SchemaVersion = SchemaVersion,
        ProjectName = ProjectName,
        SampleRate = SampleRate,
        CreatedAt = CreatedAt,
        LastSavedAt = LastSavedAt,
        SourceRoot = SourceRoot,
        Weapons = Weapons.Select(w => w.Clone()).ToList(),
        Assets = Assets.Select(a => a.Clone()).ToList(),
        Animations = Animations.Select(c => c.Clone()).ToList(),
        AnimationAssetPaths = new Dictionary<string, string>(AnimationAssetPaths),
        ActiveWeaponId = ActiveWeaponId,
        SnapshotA = SnapshotA?.Clone(),
        SnapshotB = SnapshotB?.Clone(),
        GroupingRuleVersion = GroupingRuleVersion,
        ManualGroupOverrides = new Dictionary<string, string>(ManualGroupOverrides),
    };
}

/// <summary>A/B 快照：配方 + 明确的样本序列，避免随机抽样变化污染比较。</summary>
public sealed class AbSnapshot
{
    public string Label { get; set; } = "";

    public Guid RecipeId { get; set; }

    public Recipe Recipe { get; set; } = new();

    /// <summary>监听音量补偿 dB（仅影响监听，不进入导出）。</summary>
    public double MonitorCompensationDb { get; set; }

    public AbSnapshot Clone() => new()
    {
        Label = Label,
        RecipeId = RecipeId,
        Recipe = Recipe.Clone(),
        MonitorCompensationDb = MonitorCompensationDb,
    };
}
