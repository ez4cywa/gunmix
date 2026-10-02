using System.Text.Json.Serialization;

namespace GunMix.Core.Model;

/// <summary>时间线中的一条声音事件：某层在第几采样点、以多少增益播放哪个文件。</summary>
public sealed record ShotEvent
{
    public Guid LayerId { get; init; }

    public Guid AssetId { get; init; }
    public string InstanceId { get; init; } = "";
    public string? ParentInstanceId { get; init; }
    public string TriggerEventId { get; init; } = "";
    public string CommandId { get; init; } = "";
    public string? BankKey { get; init; }
    public string? AliasId { get; init; }
    public int? RowIndex { get; init; }
    public string SourceHash { get; init; } = "";
    public string RuleOrigin { get; init; } = "legacyProjectRules";
    public string SelectionReason { get; init; } = "";
    public Fire.FireContext? ContextSnapshot { get; init; }
    public double PitchRatio { get; init; } = 1;

    /// <summary>0 起的发号；单发恒为 0。</summary>
    public int ShotIndex { get; init; }

    /// <summary>事件起点（相对输出的采样点，非负）。</summary>
    public long StartSample { get; init; }

    public double GainDb { get; init; }

    /// <summary>源文件内起点（第一版恒为 0，保留完整文件本体）。</summary>
    public long SourceOffsetSamples { get; init; }

    /// <summary>实例抢占淡出起点（输出采样点）；null = 完整播放到文件结束。</summary>
    public long? FadeOutStartSample { get; init; }

    /// <summary>淡出长度（采样），淡出结束后该实例停止。</summary>
    public long FadeOutSamples { get; init; }

    [JsonIgnore]
    public double StartSeconds => StartSample / 48000.0;
}
