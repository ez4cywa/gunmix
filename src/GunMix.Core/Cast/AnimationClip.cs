using System.Text.RegularExpressions;
using GunMix.Core.Model;

namespace GunMix.Core.Cast;

/// <summary>别名到音频文件的依据来源。bank 是原版声音配置实测，名称推断只是提示。</summary>
public enum AnimSource
{
    /// <summary>soundbank 给出唯一文件：权威，可自动采用。</summary>
    BankSingle,

    /// <summary>soundbank 给出多个文件（随机容器）：成员权威，逐次选用哪一个由本工具的种子决定。</summary>
    BankContainer,

    /// <summary>soundbank 收录了该别名，但它引用的音频未随素材导出：原版确有这一层，素材缺失。</summary>
    BankMissing,

    /// <summary>没有 bank 记录，文件名与别名精确相同。</summary>
    NameExact,

    /// <summary>没有 bank 记录，同武器内只有一个前缀候选。</summary>
    NameSingle,

    /// <summary>没有 bank 记录，同武器内有多个前缀候选，需要用户挑。</summary>
    NameMultiple,

    /// <summary>没有 bank 记录，只有词元重合的近似候选，需要用户确认。</summary>
    NameFuzzy,

    /// <summary>没有任何依据。</summary>
    Unresolved,

    /// <summary>用户显式指定（覆盖以上任何来源）。</summary>
    UserAssigned,
}

/// <summary>动画里的一个音频事件（帧号与别名来自 CAST 实测，文件来自 soundbank 或用户）。</summary>
public sealed class AnimEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>CAST 内的原始帧号（实测值，不换算）。</summary>
    public int Frame { get; set; }

    /// <summary>AudioOneShot@ 之后的别名原文。</summary>
    public string Alias { get; set; } = "";

    /// <summary>已选定的音频素材；null = 待指定。</summary>
    public Guid? AssetId { get; set; }

    public AnimSource Source { get; set; } = AnimSource.Unresolved;

    /// <summary>来源 bank 文件名（soundbank 命中时）。</summary>
    public string BankName { get; set; } = "";

    /// <summary>容器成员（BankContainer 时非空；单文件时为该单文件）。</summary>
    public List<Guid> ContainerIds { get; set; } = [];

    /// <summary>容器内如何选用：随机＝贴近游戏行为，轮换/固定＝便于逐一对比。</summary>
    public VariantMode ContainerMode { get; set; } = VariantMode.Random;

    /// <summary>soundbank 收录但该别名引用的音频未导出的条目数。</summary>
    public int BankMissingCount { get; set; }

    /// <summary>候选素材 ID（名称推断或有 bank 时的展示用列表）。</summary>
    public List<Guid> CandidateIds { get; set; } = [];

    public double GainDb { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>该素材是否由软件自动采用（false = 用户显式指定）。</summary>
    public bool AutoAssigned { get; set; }

    /// <summary>是否已有可用文件。</summary>
    public bool IsResolved => AssetId != null;

    /// <summary>是否来自原版声音配置实测。</summary>
    public bool IsBankAuthoritative => Source is AnimSource.BankSingle or AnimSource.BankContainer;

    public AnimEvent Clone() => new()
    {
        Id = Id,
        Frame = Frame,
        Alias = Alias,
        AssetId = AssetId,
        Source = Source,
        BankName = BankName,
        ContainerIds = [.. ContainerIds],
        ContainerMode = ContainerMode,
        BankMissingCount = BankMissingCount,
        CandidateIds = [.. CandidateIds],
        GainDb = GainDb,
        Enabled = Enabled,
        AutoAssigned = AutoAssigned,
    };
}

/// <summary>一条动画的完整音效装配（动画与时间来自 CAST 实测，文件对应由用户确认）。</summary>
public sealed class AnimationClip
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>来源 .cast 文件名。</summary>
    public string CastFileName { get; set; } = "";

    public string CastPath { get; set; } = "";

    /// <summary>动画标识（取自文件名，如 rex_vm_ar_mike4_inspect）。</summary>
    public string Name { get; set; } = "";

    /// <summary>归属武器标识（从名称解析，如 ar_mike4）。</summary>
    public string WeaponKey { get; set; } = "";

    public double Framerate { get; set; } = 30.0;

    public bool Looping { get; set; }

    public List<AnimEvent> Events { get; set; } = [];

    /// <summary>非音频通知（IK / LOD / 结束标记），仅展示，不参与渲染。</summary>
    public List<string> OtherNotes { get; set; } = [];

    /// <summary>整体增益 dB（动画母带），导出时对所有事件生效。</summary>
    public double MasterGainDb { get; set; }

    /// <summary>容器选样的基准种子：同种子同容器成员得到同一次序（可重现）。</summary>
    public int Seed { get; set; } = 42;

    /// <summary>事件时长（秒）= 帧号 / 帧率。帧率无效时按 30 处理并标记。</summary>
    public double FrameToSeconds(int frame) => frame / (Framerate > 0 ? Framerate : 30.0);

    public AnimationClip Clone()
    {
        var copy = new AnimationClip
        {
            Id = Id,
            CastFileName = CastFileName,
            CastPath = CastPath,
            Name = Name,
            WeaponKey = WeaponKey,
            Framerate = Framerate,
            Looping = Looping,
            OtherNotes = [.. OtherNotes],
            MasterGainDb = MasterGainDb,
            Seed = Seed,
        };
        copy.Events = Events.Select(e => e.Clone()).ToList();
        return copy;
    }

    /// <summary>
    /// 重新扫描后刷新候选：保留用户已确认的 AssetId / 增益 / 启用，
    /// 只替换来源、容器成员与候选（素材目录或 bank 可能已变化）。
    /// </summary>
    public void CandidateRefresh(AnimationClip scanned)
    {
        Framerate = scanned.Framerate;
        Looping = scanned.Looping;
        OtherNotes = [.. scanned.OtherNotes];
        var byKey = scanned.Events.ToDictionary(e => (e.Frame, e.Alias));
        foreach (var ev in Events)
        {
            if (!byKey.TryGetValue((ev.Frame, ev.Alias), out var fresh)) continue;
            bool userChose = !ev.AutoAssigned && ev.AssetId != null;
            ev.CandidateIds = [.. fresh.CandidateIds];
            ev.ContainerIds = [.. fresh.ContainerIds];
            ev.Source = fresh.Source;
            ev.BankName = fresh.BankName;
            ev.BankMissingCount = fresh.BankMissingCount;
            if (userChose)
            {
                // 用户显式指定优先保留；文件已不在候选中时标记来源为待复核
                ev.Source = ev.ContainerIds.Contains(ev.AssetId!.Value) || ev.CandidateIds.Contains(ev.AssetId!.Value)
                    ? AnimSource.UserAssigned
                    : AnimSource.Unresolved;
                continue;
            }
            ev.ContainerMode = fresh.ContainerMode;
            if (fresh.Source is AnimSource.BankSingle or AnimSource.BankContainer && fresh.ContainerIds.Count > 0)
            {
                ev.AssetId = fresh.ContainerIds[0];
                ev.AutoAssigned = true;
            }
            else if (fresh.Source is AnimSource.NameExact or AnimSource.NameSingle && fresh.CandidateIds.Count == 1)
            {
                ev.AssetId = fresh.CandidateIds[0];
                ev.AutoAssigned = true;
            }
            else
            {
                ev.AssetId = null;
                ev.AutoAssigned = false;
            }
        }
    }
}

/// <summary>
/// 动画音效库：扫描声音目录，按名称把 CAST 别名解析为候选音频文件。
/// 只在同一武器的目录内匹配，绝不跨武器借用；无法唯一确定时交给用户指定。
/// </summary>
public sealed partial class AnimationSoundLibrary
{
    private readonly List<AssetInfo> _assets;
    private readonly Func<AssetInfo, string> _pathOf;

    /// <param name="assets">工程素材表（动画音频也作为 AssetInfo 保存其中）。</param>
    /// <param name="pathOf">素材 ID → 绝对路径。</param>
    public AnimationSoundLibrary(List<AssetInfo> assets, Func<AssetInfo, string> pathOf)
    {
        _assets = assets;
        _pathOf = pathOf;
    }

    [GeneratedRegex(@"^wfoly_")]
    private static partial Regex WfolyPrefix();

    [GeneratedRegex(@"^(rex|weap)_rex_")]
    private static partial Regex RexPrefix();

    [GeneratedRegex(@"_(\d{1,3})$")]
    private static partial Regex TrailingNumber();

    [GeneratedRegex(@"^rex_vm_(ar|pi|sn|sm|lm|dm)_([a-z0-9]+)")]
    private static partial Regex CastWeaponRegex();

    [GeneratedRegex(@"^rex_(?:plr|npc|vm)_?(ar|pi|sn|sm|lm|dm)_([a-z0-9]+)")]
    private static partial Regex AliasWeaponRegex();

    /// <summary>从 cast 文件名或别名中提取武器标识（如 ar_mike4）。</summary>
    public static string? WeaponKeyOf(string name)
    {
        var m = CastWeaponRegex().Match(name);
        if (m.Success) return $"{m.Groups[1].Value}_{m.Groups[2].Value}";
        m = AliasWeaponRegex().Match(name);
        if (m.Success) return $"{m.Groups[1].Value}_{m.Groups[2].Value}";
        return null;
    }

    /// <summary>ar_mike4 → rex_mike4（reloads 子目录命名）。</summary>
    public static string ReloadsDirKey(string weaponKey)
    {
        var idx = weaponKey.IndexOf('_');
        var core = idx > 0 ? weaponKey[(idx + 1)..] : weaponKey;
        return "rex_" + core;
    }

    /// <summary>
    /// 建立候选：把别名规范化后，在同一武器的素材里按名称匹配。
    /// 只在没有 soundbank 记录时作为提示使用；返回值标明来源与置信度。
    /// </summary>
    public (List<Guid> Candidates, AnimSource Source) Resolve(AnimationClip clip, AnimEvent ev)
    {
        var weaponKey = clip.WeaponKey.Length > 0 ? clip.WeaponKey : WeaponKeyOf(clip.Name);
        var scoped = string.IsNullOrEmpty(weaponKey)
            ? _assets
            : _assets.Where(a => BelongsTo(a, weaponKey)).ToList();

        // 规范化别名：去 wfoly_，统一 rex_vm_/rex_plr_/rex_npc_，去末尾事件号
        var alias = ev.Alias;
        var noPrefix = WfolyPrefix().Replace(alias, "");
        var bases = new List<string> { alias, noPrefix };
        foreach (var token in new[] { "rex_vm_", "rex_plr_", "rex_npc_", "rex_" })
        {
            var rest = RexPrefix().Replace(noPrefix, "");
            bases.Add(token + rest);
            bases.Add(TrailingNumber().Replace(token + rest, ""));
        }
        bases.Add(TrailingNumber().Replace(noPrefix, ""));
        // 动画标识本身（branched_reload_empty_01 → 音频组可能叫 branched_reload_empty）
        if (clip.Name.Length > 0)
        {
            bases.Add(clip.Name);
            bases.Add(TrailingNumber().Replace(clip.Name, ""));
        }
        var unique = bases.Where(b => b.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        // 1) 精确：素材基名等于任一规范化别名
        var exact = scoped.Where(a => unique.Contains(BaseName(a.FileName))).ToList();
        if (exact.Count > 0)
            return ([.. exact.Select(a => a.Id)], AnimSource.NameExact);

        // 2) 前缀：素材基名以某规范化别名 + "_" 开头（角色后缀 _magin/_bolt/…）
        var prefix = scoped
            .Where(a =>
            {
                var b = BaseName(a.FileName);
                return unique.Any(u => b.StartsWith(u + "_", StringComparison.Ordinal));
            })
            .OrderBy(a => BaseName(a.FileName), StringComparer.Ordinal)
            .ToList();
        if (prefix.Count == 1)
            return ([prefix[0].Id], AnimSource.NameSingle);
        if (prefix.Count > 1)
            return ([.. prefix.Select(a => a.Id)], AnimSource.NameMultiple);

        // 3) 近似：同武器内按词元重合度排序（例如动画名带 _ads 而音频未按变体拆分）。
        //    仅作候选展示，永不自动采用。
        var tokens = new HashSet<string>(
            string.Join("_", unique).Split('_', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.OrdinalIgnoreCase);
        var fuzzy = scoped
            .Select(a =>
            {
                var parts = BaseName(a.FileName).Split('_', StringSplitOptions.RemoveEmptyEntries);
                int shared = parts.Count(p => tokens.Contains(p));
                return (Asset: a, Score: shared);
            })
            .Where(x => x.Score >= 2)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => BaseName(x.Asset.FileName), StringComparer.Ordinal)
            .Take(24)
            .ToList();
        if (fuzzy.Count > 0)
            return ([.. fuzzy.Select(x => x.Asset.Id)], AnimSource.NameFuzzy);

        return ([], AnimSource.Unresolved);
    }

    /// <summary>素材是否属于该武器（自身目录名或 reloads 子目录名匹配）。</summary>
    private bool BelongsTo(AssetInfo asset, string weaponKey)
    {
        var dir = Path.GetFileName(asset.SourceDirectory.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(dir)) return false;
        if (dir.Equals(weaponKey, StringComparison.OrdinalIgnoreCase)) return true;
        if (dir.Equals(ReloadsDirKey(weaponKey), StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static string BaseName(string fileName)
    {
        var m = SuffixRegex().Match(fileName);
        return m.Success ? fileName[..m.Index] : Path.GetFileNameWithoutExtension(fileName);
    }

    [GeneratedRegex(@"\.[a-z0-9]{3}\.\d{1,4}\.\d{4,6}\.all\.wav$", RegexOptions.IgnoreCase)]
    private static partial Regex SuffixRegex();
}
