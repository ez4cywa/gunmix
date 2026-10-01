using System.IO;

namespace GunMix.Core.Tests;

/// <summary>
/// 测试素材路径统一入口。真实素材不随仓库分发，路径由环境变量提供，
/// 未设置时相关测试自动跳过，保证开源仓库不含任何本机绝对路径。
///
///   环境变量（PowerShell）：
///     $env:GUNMIX_ASSETS_ROOT = "你的\游戏导出根目录"
///     dotnet test
///
/// 期望的目录结构：
///   {GUNMIX_ASSETS_ROOT}\animations        .cast 动画文件
///   {GUNMIX_ASSETS_ROOT}\sounds            音频树根（bank 内相对路径的基准）
///   {GUNMIX_ASSETS_ROOT}\sounds\rex\wpn    各武器声音目录
///   {GUNMIX_ASSETS_ROOT}\sndbanks\json     原版声音配置（weapon_rex_*.json）
/// </summary>
public static class TestPaths
{
    /// <summary>游戏导出根目录；未设置时所有依赖真实素材的测试跳过。</summary>
    public static string? AssetsRoot { get; } = Environment.GetEnvironmentVariable("GUNMIX_ASSETS_ROOT");

    public static bool HasAssets => !string.IsNullOrWhiteSpace(AssetsRoot) && Directory.Exists(AssetsRoot);

    /// <summary>…\animations（.cast 动画文件）。</summary>
    public static string? AnimDir => Combine("animations");

    /// <summary>…\sounds\rex\wpn（各武器声音目录）。</summary>
    public static string? WpnRoot => Combine("sounds", "rex", "wpn");

    /// <summary>…\sounds（soundbank 内相对路径的基准）。</summary>
    public static string? SoundsRoot => Combine("sounds");

    /// <summary>…\sndbanks\json（原版声音配置）。</summary>
    public static string? BankDir => Combine("sndbanks", "json");

    private static string? Combine(params string[] parts)
    {
        if (!HasAssets) return null;
        var path = Path.Combine(Path.Combine([AssetsRoot!, .. parts]));
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>仅当目录存在时才执行真实素材断言。</summary>
    public static bool Ready(params string[] dirs) =>
        dirs.All(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d));
}
