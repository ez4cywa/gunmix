using GunMix.Core.Assets;

namespace GunMix.Core.Model;

/// <summary>一条已导入的音频素材。</summary>
public sealed class AssetInfo
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>导入时的来源目录（工程保存相对路径 + 来源信息，文件名不作为唯一身份）。</summary>
    public string SourceDirectory { get; set; } = "";

    public string FileName { get; set; } = "";

    /// <summary>内容哈希（SHA256 十六进制），用于重定位匹配。</summary>
    public string Sha256 { get; set; } = "";

    public AudioFormatInfo Format { get; set; } = new(48000, 16, 1, false, 0);

    /// <summary>命名解析结果；无法解析时为 null。</summary>
    public NameParseResult? Parsed { get; set; }

    /// <summary>当前生效的分组键；GroupSource=Manual 时为用户指定。</summary>
    public string GroupKey { get; set; } = "";

    public GroupSource GroupSource { get; set; } = GroupSource.Auto;

    public Guid WeaponId { get; set; }

    /// <summary>响度测量（导入时计算，供按枪型配平增益）；旧工程或未测量时为 null。</summary>
    public AssetLoudness? Loudness { get; set; }

    public string DisplayName => FileName;

    public string GroupDisplay => GroupKey.Length > 0 ? GroupKey : "(未分组)";

    /// <summary>下拉列表显示：分组 / 变体号 或完整文件名。</summary>
    public string VariantLabel
    {
        get
        {
            var v = Parsed?.Variant;
            if (v != null && GroupKey.Length > 0) return $"{GroupKey} / {v:00}";
            return FileName;
        }
    }

    public AssetInfo Clone() => new()
    {
        Id = Id,
        SourceDirectory = SourceDirectory,
        FileName = FileName,
        Sha256 = Sha256,
        Format = Format,
        Parsed = Parsed,
        GroupKey = GroupKey,
        GroupSource = GroupSource,
        WeaponId = WeaponId,
        Loudness = Loudness,
    };
}

/// <summary>
/// 单个素材的响度指标（PCM 数学测量，不是听感评分）。
/// EarlyRmsDb 取文件前 250 ms 的 RMS：枪声主要能量集中在起音段，整段 RMS 会被长尾稀释。
/// </summary>
public sealed record AssetLoudness(double PeakDbfs, double EarlyRmsDbfs, double Energy95Ms)
{
    public const double EarlyWindowMs = 250.0;
}

public enum GroupSource
{
    Auto,
    Manual,
}
