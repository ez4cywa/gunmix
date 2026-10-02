using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GunMix.Core.SoundBanks;

/// <summary>一条导出记录：音频引用、别名、执行语义尚未完全证实的候选关联。</summary>
public sealed record SoundBankEntry
{
    [JsonPropertyName("snd")] public string Snd { get; init; } = "";
    [JsonPropertyName("alias")] public string? Alias { get; init; }
    [JsonPropertyName("alias2")] public string? Alias2 { get; init; }
}

/// <summary>bank 归属信息：武器、视角、是否消音、附加变体（reload_xmag 等）。</summary>
public sealed record SoundBankInfo(string FileName, string WeaponKey, string Perspective, bool Suppressed, string Variant)
{
    public string Label => Variant.Length == 0
        ? $"{WeaponKey} {Perspective}{(Suppressed ? " 消音" : "")}"
        : $"{WeaponKey} {Perspective}{(Suppressed ? " 消音" : "")} · {Variant}";
}

/// <summary>
/// 声音库索引：读取 <c>sndbanks/json/weapon_rex_*.json</c>，把动画与射击音效的别名解析为真实音频文件。
/// 这是原版声音配置的实测来源，优先于按文件名猜测；snd 指向未导出条目（sound_* 占位）时不算命中。
/// </summary>
public sealed class SoundBankIndex
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>别名 → 已落地的文件（去重，保持 bank 内原顺序）。</summary>
    private readonly Dictionary<string, List<ResolvedEntry>> _byAlias = new(StringComparer.Ordinal);

    /// <summary>别名 → 引用了但未导出的条目数（用于界面说明“原版有这一层，素材未导出”）。</summary>
    private readonly Dictionary<string, int> _missingByAlias = new(StringComparer.Ordinal);

    /// <summary>别名 → 同时触发的配对别名（具名，去重）。</summary>
    private readonly Dictionary<string, SortedSet<string>> _pairs = new(StringComparer.Ordinal);

    public List<SoundBankInfo> Banks { get; } = [];

    public int EntryCount { get; private set; }

    public IReadOnlyCollection<string> Aliases => _byAlias.Keys;

    public int ResolvedAliasCount => _byAlias.Count;

    public int PairedAliasCount => _pairs.Count;

    /// <summary>实际采用的 sounds 根目录（可能与传入不同）。</summary>
    public string SoundsRootUsed { get; private set; } = "";

    public readonly record struct ResolvedEntry(string Bank, string Snd, string ResolvedPath);

    /// <summary>
    /// 加载目录下所有 <c>weapon_rex_*.json</c>。<paramref name="soundsRoot"/> 是 bank 内相对路径的基准（通常是 <c>sounds</c>）。
    /// 传入的根不对时自动向上寻找能落地最多文件的祖先目录（bank 内路径形如 <c>rex/wpn/…</c>）。
    /// </summary>
    public static SoundBankIndex Load(string bankDirectory, string soundsRoot, Action<int, int>? progress = null)
    {
        var index = new SoundBankIndex();
        if (!Directory.Exists(bankDirectory)) return index;

        var files = Directory.EnumerateFiles(bankDirectory, "weapon_rex_*.json", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (files.Count == 0)
            files = Directory.EnumerateFiles(bankDirectory, "*.json", SearchOption.AllDirectories)
                .Where(p => Path.GetFileNameWithoutExtension(p).StartsWith("weapon_rex_", StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();

        var root = ChooseSoundsRoot(files, soundsRoot);
        var wavIndex = BuildWavIndex(root);
        index._wavIndex = wavIndex;
        index.SoundsRootUsed = root;

        for (int i = 0; i < files.Count; i++)
        {
            progress?.Invoke(i + 1, files.Count);
            var file = files[i];
            string bankName = Path.GetFileNameWithoutExtension(file);
            if (bankName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                bankName = bankName[..^5];
            var info = ParseBankName(bankName);
            if (info == null) continue;
            index.Banks.Add(info);

            List<SoundBankEntry>? entries;
            try
            {
                entries = JsonSerializer.Deserialize<List<SoundBankEntry>>(File.ReadAllText(file), JsonOpts);
            }
            catch
            {
                continue; // 损坏的 bank 跳过，不影响其余
            }
            if (entries == null) continue;

            foreach (var e in entries)
            {
                index.EntryCount++;
                if (string.IsNullOrEmpty(e.Alias)) continue;

                if (!string.IsNullOrEmpty(e.Alias2))
                {
                    if (!index._pairs.TryGetValue(e.Alias, out var set))
                        index._pairs[e.Alias] = set = new SortedSet<string>(StringComparer.Ordinal);
                    set.Add(e.Alias2!);
                }

                if (e.Snd.StartsWith("sound_", StringComparison.Ordinal))
                {
                    index._missingByAlias.TryGetValue(e.Alias, out int m);
                    index._missingByAlias[e.Alias] = m + 1;
                    continue;
                }
                if (!wavIndex.TryGetValue(e.Snd.ToLowerInvariant(), out var resolved))
                {
                    index._missingByAlias.TryGetValue(e.Alias, out int m);
                    index._missingByAlias[e.Alias] = m + 1;
                    continue;
                }
                var list = index.GetOrCreateList(e.Alias);
                if (!list.Any(x => x.ResolvedPath == resolved))
                    list.Add(new ResolvedEntry(bankName, e.Snd, resolved));
            }
        }
        return index;
    }

    private Dictionary<string, string> _wavIndex = new(StringComparer.Ordinal);

    private List<ResolvedEntry> GetOrCreateList(string alias)
    {
        if (!_byAlias.TryGetValue(alias, out var list))
            _byAlias[alias] = list = [];
        return list;
    }

    /// <summary>
    /// 选择能落地最多 bank 路径的目录作为 sounds 根：从传入目录开始逐级向上评分，取命中数最大的那个。
    /// bank 里的 snd 混用了两种写法（<c>rex/wpn/ar_mike4/…</c> 与 <c>ar_mike4/…</c>），
    /// 因此不能一见命中就停，必须比较各级祖先的总命中数。
    /// </summary>
    private static string ChooseSoundsRoot(List<string> bankFiles, string requested)
    {
        var samples = SampleSndPaths(bankFiles);
        if (samples.Count == 0) return requested;

        var candidate = Directory.Exists(requested) ? Path.GetFullPath(requested) : null;
        string best = requested;
        int bestHits = int.MinValue;
        for (int depth = 0; depth < 6 && candidate != null; depth++)
        {
            int hits = CountHits(candidate, samples);
            if (hits > bestHits)
            {
                bestHits = hits;
                best = candidate;
            }
            var parent = Directory.GetParent(candidate);
            if (parent == null || parent.FullName == candidate) break;
            candidate = parent.FullName;
        }
        return best;
    }

    private static List<string> SampleSndPaths(List<string> bankFiles)
    {
        var samples = new List<string>();
        foreach (var file in bankFiles)
        {
            List<SoundBankEntry>? entries;
            try
            {
                entries = JsonSerializer.Deserialize<List<SoundBankEntry>>(File.ReadAllText(file), JsonOpts);
            }
            catch
            {
                continue;
            }
            if (entries == null) continue;
            foreach (var e in entries)
            {
                if (e.Snd.Length > 0 && !e.Snd.StartsWith("sound_", StringComparison.Ordinal))
                {
                    samples.Add(e.Snd);
                    if (samples.Count >= 200) return samples;
                }
            }
        }
        return samples;
    }

    private static int CountHits(string root, List<string> samples)
    {
        if (!Directory.Exists(root)) return 0;
        int hits = 0;
        foreach (var s in samples)
        {
            var full = Path.Combine(root, s.Replace('/', Path.DirectorySeparatorChar) + ".wav");
            if (File.Exists(full)) hits++;
        }
        return hits;
    }

    private static Dictionary<string, string> BuildWavIndex(string soundsRoot)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(soundsRoot)) return map;
        foreach (var wav in Directory.EnumerateFiles(soundsRoot, "*.wav", new EnumerationOptions
                     { RecurseSubdirectories = true, IgnoreInaccessible = true }))
        {
            var rel = Path.GetRelativePath(soundsRoot, wav).Replace('\\', '/');
            var key = rel[..^4].ToLowerInvariant(); // 去掉 .wav
            map[key] = wav;
        }
        return map;
    }

    /// <summary>weapon_rex_ar_mike4_plr_reload_xmag.all → 归属信息。</summary>
    public static SoundBankInfo? ParseBankName(string bankName)
    {
        const string head = "weapon_rex_";
        if (!bankName.StartsWith(head, StringComparison.OrdinalIgnoreCase)) return null;
        var rest = bankName[head.Length..];
        var parts = rest.Split('_');
        if (parts.Length < 2) return null;
        var weapon = $"{parts[0]}_{parts[1]}";
        var tags = parts.Skip(2).ToList();
        var perspective = tags.Contains("plr") ? "plr" : tags.Contains("npc") ? "npc" : "?";
        bool sup = tags.Contains("sup");
        var variant = string.Join("_", tags.Where(t => t is not ("plr" or "npc" or "sup")));
        return new SoundBankInfo(bankName, weapon, perspective, sup, variant);
    }

    private static bool IsHash(string s) =>
        s.Length == 16 && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>别名在 bank 中的落地文件列表；未收录返回空。</summary>
    public IReadOnlyList<ResolvedEntry> Lookup(string alias) =>
        _byAlias.TryGetValue(alias, out var list) ? list : Array.Empty<ResolvedEntry>();

    /// <summary>Explicit scoped lookup; the one-argument overload remains for legacy animation projects.</summary>
    public IReadOnlyList<ResolvedEntry> Lookup(string bank, string alias) => Lookup(alias).Where(e=>e.Bank==bank).ToArray();

    /// <summary>该别名引用但未导出的条目数（&gt;0 表示原版确有这一层，素材缺失）。</summary>
    public int MissingCount(string alias) => _missingByAlias.GetValueOrDefault(alias, 0);

    /// <summary>bank 中的候选关联，不代表已确认同时触发。</summary>
    public IReadOnlyList<string> PairedWith(string alias) =>
        _pairs.TryGetValue(alias, out var set) ? [.. set] : Array.Empty<string>();

    public bool Contains(string alias) => _byAlias.ContainsKey(alias);
}
