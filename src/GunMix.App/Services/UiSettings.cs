using System.IO;
using System.Text.Json;

namespace GunMix.App.Services;

/// <summary>
/// 每用户界面设置（记住上次用过的目录）。
/// 存在 %APPDATA%\GunMix\settings.json，不写入仓库、不含任何内置路径。
/// </summary>
public sealed class UiSettings
{
    public string AnimationDir { get; set; } = "";
    public string SoundDir { get; set; } = "";
    public string BankDir { get; set; } = "";
    public string OutputDir { get; set; } = "";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GunMix", "settings.json");

    public static UiSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(FilePath)) ?? new UiSettings();
        }
        catch
        {
            // 设置损坏不影响启动
        }
        return new UiSettings();
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 记不住设置不影响使用
        }
    }
}
