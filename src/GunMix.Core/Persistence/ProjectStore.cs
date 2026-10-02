using System.Text.Json;
using System.Text.Json.Serialization;
using GunMix.Core.Model;

namespace GunMix.Core.Persistence;

/// <summary>
/// 工程持久化：.gunmix.json。临时文件写完后原子替换；
/// 自动恢复文件独立存放，不覆盖最后一次明确保存的工程。
/// </summary>
public static class ProjectStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    /// <summary>原子保存：先写临时文件，成功后替换目标。</summary>
    public static void Save(GunProject project, string path)
    {
        if(project.MigrationReport!=null&&File.Exists(path))
        {
            using var original=JsonDocument.Parse(File.ReadAllText(path));
            if(!original.RootElement.TryGetProperty("schemaVersion",out var version)||version.GetInt32()<3)
                throw new InvalidOperationException("迁移工程请另存为新文件，原工程保留用于试听对比。");
        }
        project.LastSavedAt = DateTime.Now;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(project, JsonOptions));
        if (File.Exists(path))
            File.Replace(temp, path, null);
        else
            File.Move(temp, path);
    }

    public static GunProject Load(string path)
    {
        var json = File.ReadAllText(path);
        var project = JsonSerializer.Deserialize<GunProject>(json, JsonOptions)
                      ?? throw new InvalidDataException("无法解析工程文件。");
        if(project.SchemaVersion>GunProject.CurrentSchemaVersion||project.SchemaVersion<1)
            throw new InvalidDataException("不支持的工程版本。");
        if(project.SchemaVersion<3)
        {
            project.MigrationReport="旧工程按 legacyProjectRules 读取，原有混音参数保留；未自动启用开火场景。另存为新工程后写入版本 3。";
            project.SchemaVersion=3;
        }
        return project;
    }

    /// <summary>
    /// 自动恢复文件目录（独立于工程本体）。
    /// 测试可设置 <see cref="RecoveryDirectoryOverride"/> 指向临时目录，避免污染用户数据。
    /// </summary>
    public static string RecoveryDirectory
    {
        get
        {
            var dir = RecoveryDirectoryOverride
                      ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GunMix", "recovery");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>恢复文件目录覆盖（仅测试使用；null = 用户目录）。</summary>
    public static string? RecoveryDirectoryOverride { get; set; }

    public static void SaveRecovery(GunProject project, string? projectPath)
    {
        var name = string.IsNullOrEmpty(projectPath) ? project.ProjectName : Path.GetFileNameWithoutExtension(projectPath);
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        var path = Path.Combine(RecoveryDirectory, name + ".recover.json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(project, JsonOptions));
        }
        catch
        {
            // 恢复文件写入失败不影响编辑
        }
    }

    public static string? LatestRecoveryPath => Directory.EnumerateFiles(RecoveryDirectory, "*.recover.json")
        .Select(p => (Path: p, Time: File.GetLastWriteTime(p)))
        .OrderByDescending(x => x.Time)
        .Select(x => x.Path)
        .FirstOrDefault();
}
