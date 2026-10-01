using System.Security.Cryptography;
using GunMix.Core.Audio;
using GunMix.Core.Model;
using GunMix.Core.Synthesis;

namespace GunMix.Core.Assets;

public sealed record ImportFailure(string FilePath, string Reason);

public sealed class ImportReport
{
    public List<AssetInfo> Assets { get; set; } = [];

    public List<ImportFailure> Failures { get; set; } = [];

    public int TotalFiles { get; set; }

    public List<string> WeaponsFound { get; set; } = [];
}

/// <summary>
/// 素材服务：扫描、解码、读取格式、分组、哈希、重定位与缓存。
/// 导入不改变素材本体；扫描在后台线程执行，单个文件失败不使全部导入失败。
/// </summary>
public sealed class AssetService
{
    /// <summary>
    /// 扫描目录。进度回调 (已检查, 总数)；返回取消标志为 true 时停止。
    /// includeSubdirectories 用于导入包含多把武器的目录。
    /// </summary>
    public ImportReport ImportDirectory(
        string directory,
        Action<int, int>? progress = null,
        Func<bool>? isCancelled = null,
        bool includeSubdirectories = true,
        IReadOnlyDictionary<string, string>? manualGroupOverrides = null)
    {
        var report = new ImportReport();
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"目录不存在：{directory}");

        var files = Directory.EnumerateFiles(directory, "*.wav",
                new EnumerationOptions { RecurseSubdirectories = includeSubdirectories, IgnoreInaccessible = true })
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
        report.TotalFiles = files.Count;

        int checkedCount = 0;
        foreach (var file in files)
        {
            if (isCancelled?.Invoke() == true) break;
            try
            {
                var (info, error) = WavReader.ReadFormat(file);
                if (info == null)
                {
                    report.Failures.Add(new ImportFailure(file, error ?? "未知错误"));
                    continue;
                }
                if (info.SampleRate != 48000 || info.BitsPerSample is not (16 or 24 or 32) || info.Channels > 2)
                {
                    // 格式仍在支持范围但会在使用时转换；此处仅记录真实格式
                }
                var parsed = NameParser.Parse(Path.GetFileName(file));
                var asset = new AssetInfo
                {
                    SourceDirectory = Path.GetDirectoryName(file) ?? "",
                    FileName = Path.GetFileName(file),
                    Format = info,
                    Parsed = parsed,
                    WeaponId = Guid.Empty, // 由调用方按武器归属分配
                    GroupKey = manualGroupOverrides != null &&
                               manualGroupOverrides.TryGetValue(Path.GetFileName(file), out var g) && g.Length > 0
                        ? g
                        : parsed.GroupKey,
                    GroupSource = manualGroupOverrides != null && manualGroupOverrides.ContainsKey(Path.GetFileName(file))
                        ? GroupSource.Manual
                        : GroupSource.Auto,
                };
                asset.Sha256 = ComputeHash(file);
                if (parsed.Category != null)
                {
                    // 开火类素材测响度，供按枪型配平；换弹等其他素材不解码以节省导入时间
                    try { asset.Loudness = LoudnessMeter.Measure(WavReader.Read(file)); }
                    catch (WavDecodeException) { /* 格式头可读但数据无法解码：保留素材，使用时再报具体错误 */ }
                }
                report.Assets.Add(asset);
                var weaponName = WeaponNameOf(asset);
                if (weaponName != null && !report.WeaponsFound.Contains(weaponName))
                    report.WeaponsFound.Add(weaponName);
            }
            catch (Exception ex)
            {
                report.Failures.Add(new ImportFailure(file, ex.Message));
            }
            finally
            {
                checkedCount++;
                progress?.Invoke(checkedCount, report.TotalFiles);
            }
        }
        return report;
    }

    /// <summary>武器归属：文件名前缀优先，其次所在目录名（ar_mike4 / rex_findia）；都无法识别返回 null。</summary>
    public static string? WeaponNameOf(AssetInfo asset) =>
        asset.Parsed?.Weapon ?? NameParser.ParseWeaponFolder(Path.GetFileName(asset.SourceDirectory))?.Weapon;

    /// <summary>由素材所在目录（逐级向上）的类别前缀推断武器类型；找不到返回 null。</summary>
    public static string? TypeFromDirectories(IEnumerable<AssetInfo> assets)
    {
        foreach (var dir in assets.Select(a => a.SourceDirectory).Distinct())
        {
            for (var d = dir; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            {
                var type = WeaponTypes.FromClassPrefix(NameParser.ParseWeaponFolder(Path.GetFileName(d))?.ClassPrefix);
                if (type != null) return type;
            }
        }
        return null;
    }

    public static string ComputeHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// 重定位素材：按原相对路径及哈希匹配；同名但内容不同不得自动替代。
    /// 返回解析后的路径；无法解析返回 null（冲突时返回原路径并把 conflict 置 true）。
    /// </summary>
    public static string? Relocate(AssetInfo asset, string projectDirectory, string sourceRoot, out bool conflict)
    {
        conflict = false;
        var candidates = new List<string>();
        if (Path.IsPathRooted(asset.SourceDirectory) && File.Exists(Path.Combine(asset.SourceDirectory, asset.FileName)))
            candidates.Add(Path.Combine(asset.SourceDirectory, asset.FileName));

        foreach (var root in new[] { projectDirectory, sourceRoot })
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            try
            {
                var found = Directory.EnumerateFiles(root, asset.FileName,
                    new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true,
                        MatchCasing = MatchCasing.CaseInsensitive }).Take(5);
                candidates.AddRange(found);
            }
            catch
            {
                // 忽略不可访问目录
            }
        }

        foreach (var candidate in candidates.Distinct())
        {
            try
            {
                if (ComputeHash(candidate) == asset.Sha256) return candidate;
            }
            catch
            {
                // 无法读取的候选文件跳过
            }
        }
        if (candidates.Count > 0)
        {
            conflict = true;
            return candidates[0];
        }
        return null;
    }

    /// <summary>解析层的素材池：显式 ID 列表优先，否则按分组键在素材库中查找（按变体号/文件名排序）。</summary>
    public static List<AssetInfo> ResolvePool(Layer layer, IEnumerable<AssetInfo> allAssets, Guid weaponId)
    {
        var assets = allAssets.Where(a => a.WeaponId == weaponId).ToList();
        List<AssetInfo> pool;
        if (layer.PoolAssetIds.Count > 0)
        {
            var byId = assets.ToDictionary(a => a.Id);
            pool = layer.PoolAssetIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }
        else if (layer.PoolGroupKey.Length > 0)
        {
            pool = assets
                .Where(a => a.GroupKey == layer.PoolGroupKey)
                .OrderBy(a => a.Parsed?.Variant ?? int.MaxValue)
                .ThenBy(a => a.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            pool = [];
        }
        return pool;
    }
}
