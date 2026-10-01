using System.Text.RegularExpressions;

namespace GunMix.Core.Assets;

/// <summary>MW 风格命名解析结果。</summary>
public sealed record NameParseResult(
    string? Weapon,
    string? Category,
    string? Perspective,
    string Group,
    int? Variant,
    string? SuffixCode,
    string? SuffixQuality,
    bool IsExperimental)
{
    /// <summary>完整分组键，如 fire_plr_shot；无类别时为原始主体。</summary>
    public string GroupKey => Category != null && Perspective != null ? $"{Category}_{Perspective}_{Group}" : Group;
}

/// <summary>
/// 针对样本目录的命名识别：识别武器前缀、组名与末尾编号，保留原始名字与点号后缀。
/// 分组规则具备版本号；手动分组优先，重新扫描不覆盖已有调整。
/// mike4 仅是其中一个武器标识，解析器不把枪名固定。
/// </summary>
public static partial class NameParser
{
    /// <summary>v2：前缀的 rex/release 段可省略（weap_findia_…）；fire_plr_fcg 类组归入实验分支；可由目录名推断武器。</summary>
    public const int RuleVersion = 2;

    /// <summary>点号后缀：.qnn.85.48000.all.wav → code=qnn, quality=85, rate=48000。不得把 .85 当成音量。</summary>
    [GeneratedRegex(@"\.([a-z0-9]{2,4})\.(\d{1,4})\.(\d{4,6})\.all$", RegexOptions.IgnoreCase)]
    private static partial Regex SuffixRegex();

    /// <summary>武器前缀：weap_rex_mike4_ → weapon=mike4；weap_findia_ → weapon=findia（部分素材缺 rex 段）。</summary>
    [GeneratedRegex(@"^(?:weap|wpn)_(?:(?:rex|release)_)?([A-Za-z0-9]+)_(.+)$")]
    private static partial Regex WeaponPrefixRegex();

    /// <summary>武器目录名：ar_mike4 / sn_svictor98 / rex_findia → 类别前缀 + 武器名。</summary>
    [GeneratedRegex(@"^(?:(ar|sm|lm|pi|dm|sn|sh|la)_|rex_)([A-Za-z0-9]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WeaponFolderRegex();

    /// <summary>由目录名推断武器（文件名无法识别时的归属依据）。无法识别返回 null。</summary>
    public static (string Weapon, string? ClassPrefix)? ParseWeaponFolder(string? folderName)
    {
        if (string.IsNullOrEmpty(folderName)) return null;
        var m = WeaponFolderRegex().Match(folderName);
        if (!m.Success) return null;
        return (m.Groups[2].Value, m.Groups[1].Success ? m.Groups[1].Value.ToLowerInvariant() : null);
    }

    /// <summary>末尾变体编号：_01 / _001。</summary>
    [GeneratedRegex(@"^(.+)_(\d{1,3})$")]
    private static partial Regex TrailingNumberRegex();

    private static readonly string[] Categories = ["fire", "sup", "dist"];
    private static readonly string[] Perspectives = ["plr", "npc"];

    public static NameParseResult Parse(string fileName)
    {
        string baseName = Path.GetFileNameWithoutExtension(fileName);

        string? code = null, quality = null;
        var suffix = SuffixRegex().Match(baseName);
        if (suffix.Success)
        {
            code = suffix.Groups[1].Value;
            quality = suffix.Groups[2].Value;
            baseName = baseName[..suffix.Index];
        }

        string? weapon = null;
        var prefix = WeaponPrefixRegex().Match(baseName);
        if (prefix.Success)
        {
            weapon = prefix.Groups[1].Value;
            baseName = prefix.Groups[2].Value;
        }

        int? variant = null;
        var trailing = TrailingNumberRegex().Match(baseName);
        if (trailing.Success)
        {
            variant = int.Parse(trailing.Groups[2].Value);
            baseName = trailing.Groups[1].Value;
        }

        var body = baseName.ToLowerInvariant();
        var bodyParts = body.Split('_');
        bool experimental = body.StartsWith("fcg_") || bodyParts.Contains("fcg")
                            || bodyParts.Contains("interrupt") || bodyParts.Contains("last");
        string group;
        string? category = null, perspective = null;

        if (body.StartsWith("fcg_"))
        {
            // fcg_deadtrig_plr / fcg_disconnector_plr_ads
            group = body;
            category = "fcg";
            if (bodyParts.Length >= 3 && Perspectives.Contains(bodyParts[^1])) perspective = bodyParts[^1];
            else if (bodyParts.Length >= 4 && bodyParts[^1] == "ads") perspective = bodyParts[^2];
        }
        else
        {
            if (bodyParts.Length >= 2 && Categories.Contains(bodyParts[0]))
            {
                category = bodyParts[0];
                int idx = 1;
                if (Perspectives.Contains(bodyParts[idx])) { perspective = bodyParts[idx]; idx++; }
                group = string.Join('_', bodyParts[idx..]);
            }
            else
            {
                group = body; // 无法识别：进入“未分组”，不丢弃文件
            }
        }

        return new NameParseResult(weapon, category, perspective, group, variant, code, quality, experimental);
    }

    /// <summary>素材分组在左侧库中的显示桶。</summary>
    public static string BucketOf(string groupKey)
    {
        if (string.IsNullOrEmpty(groupKey)) return "未分组";
        if (groupKey.StartsWith("wfoly") || groupKey.StartsWith("zwfoly") || groupKey.StartsWith("rex_vm_")
            || groupKey.Contains("reload")) return "换弹 / Foley";
        if (groupKey.StartsWith("fcg_") || groupKey.Split('_').Contains("fcg")) return "实验分支（未启用）";
        if (groupKey.Contains("interrupt") || groupKey.Contains("last")) return "实验分支（未启用）";
        if (groupKey.StartsWith("fire_plr_") && groupKey.EndsWith("_ads")) return "ADS 候选";
        if (groupKey.StartsWith("fire_plr_")) return "普通玩家";
        if (groupKey.StartsWith("sup_plr_")) return "消音";
        if (groupKey.Contains("npc")) return "NPC 距离候选";
        return "其他";
    }
}
