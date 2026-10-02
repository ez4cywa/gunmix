namespace GunMix.Core.Model;

/// <summary>混音层。一层引用一个素材池（分组），按变体策略在每发上选择具体文件。</summary>
public sealed class Layer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = "";

    /// <summary>层角色标签（SHOT/MECH/LOW/SWT/ATMO/自定义），仅作显示与模板提示。</summary>
    public string Role { get; set; } = LayerRoles.Custom;

    /// <summary>开关：关闭本层（相当于 Mute）。Mute 优先于 Solo。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>独听：仅影响监听，不改变导出层。</summary>
    public bool Solo { get; set; }

    /// <summary>增益 dB，范围 -60 ～ +6。</summary>
    public double GainDb { get; set; }

    /// <summary>延时 ms，范围 0 ～ 1000，支持 0.1 ms 输入。</summary>
    public double DelayMs { get; set; }

    /// <summary>素材池：分组键（来自命名识别或手动指定）。</summary>
    public string PoolGroupKey { get; set; } = "";

    /// <summary>素材池：显式资产 ID 列表（与分组键二选一，显式列表优先）。</summary>
    public List<Guid> PoolAssetIds { get; set; } = [];

    public VariantMode VariantMode { get; set; } = VariantMode.Rotation;

    /// <summary>VariantMode=Fixed 时使用的资产。</summary>
    public Guid? FixedAssetId { get; set; }

    /// <summary>VariantMode=Random 使用的随机种子；null 时使用配方默认种子。</summary>
    public int? Seed { get; set; }

    /// <summary>实验触发层（last/interrupt/FCG）：默认关闭；启用后显示“用户设定，原版条件未确认”。</summary>
    public bool IsExperimental { get; set; }

    /// <summary>实验触发方式：仅当 IsExperimental 时有意义。</summary>
    public ExperimentalTrigger Trigger { get; set; } = ExperimentalTrigger.PerShot;

    /// <summary>Trigger=ShotN 时的发号（1 起）。</summary>
    public int TriggerShotNumber { get; set; } = 1;

    /// <summary>
    /// 连发同时发声上限（0 = 不限制）。超出时最旧的实例在新一发起点开始 VoiceStealFadeMs 淡出，
    /// 用于全自动长连射时空间层不持续堆高（试混建议的实例管理，不是原版参数）。单发不受影响。
    /// </summary>
    public int BurstVoiceLimit { get; set; }

    public Fire.LayerFireTrigger FireTrigger { get; set; } = Fire.LayerFireTrigger.EveryShot;
    public string? BankKey { get; set; }
    public string? AliasId { get; set; }
    public double PitchRatio { get; set; } = 1;

    public const double VoiceStealFadeMs = 30.0;

    /// <summary>松扳机尾音层：实验层 + 序列释放时刻触发；只进入连发。</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsReleaseTail => IsExperimental && Trigger == ExperimentalTrigger.ReleaseMoment;

    public Layer Clone() => new()
    {
        Id = Id,
        Name = Name,
        Role = Role,
        Enabled = Enabled,
        Solo = Solo,
        GainDb = GainDb,
        DelayMs = DelayMs,
        PoolGroupKey = PoolGroupKey,
        PoolAssetIds = [.. PoolAssetIds],
        VariantMode = VariantMode,
        FixedAssetId = FixedAssetId,
        Seed = Seed,
        IsExperimental = IsExperimental,
        Trigger = Trigger,
        TriggerShotNumber = TriggerShotNumber,
        BurstVoiceLimit = BurstVoiceLimit,
        FireTrigger = FireTrigger,
        BankKey = BankKey,
        AliasId = AliasId,
        PitchRatio = PitchRatio,
    };
}

public static class LayerRoles
{
    public const string Shot = "SHOT";
    public const string Mech = "MECH";
    public const string Low = "LOW";
    public const string Swt = "SWT";
    public const string Atmo = "ATMO";
    public const string Custom = "自定义";

    public static readonly string[] Builtin = [Shot, Mech, Low, Swt, Atmo, Custom];
}

public enum ExperimentalTrigger
{
    /// <summary>每发都触发。</summary>
    PerShot,

    /// <summary>指定第 N 发触发（1 起）。</summary>
    ShotN,

    /// <summary>序列释放时刻触发（最后一发起点 + 一个射击间隔）。</summary>
    ReleaseMoment,
}
