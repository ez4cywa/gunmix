using System.Text.Json;
using System.Text.Json.Serialization;

namespace GunMix.Core.Persistence;

/// <summary>旧配方 mix_recipe.json（现有对照音频的构建配方）导入模型。识别时间、文件和增益字段，不误认成原游戏配置。</summary>
public sealed class LegacyMixRecipe
{
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("source_directory")] public string SourceDirectory { get; set; } = "";
    [JsonPropertyName("sample_rate")] public int SampleRate { get; set; } = 48000;
    [JsonPropertyName("channels")] public int Channels { get; set; } = 2;
    [JsonPropertyName("duration_s")] public double DurationS { get; set; }
    [JsonPropertyName("gains_db")] public Dictionary<string, double> GainsDb { get; set; } = [];
    [JsonPropertyName("events")] public List<LegacyEvent> Events { get; set; } = [];

    public sealed class LegacyEvent
    {
        [JsonPropertyName("time_s")] public double TimeS { get; set; }
        [JsonPropertyName("file")] public string File { get; set; } = "";
        [JsonPropertyName("gain_db")] public double GainDb { get; set; }
        [JsonPropertyName("source_offset_s")] public double SourceOffsetS { get; set; }
    }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static LegacyMixRecipe Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<LegacyMixRecipe>(json, Options)
               ?? throw new InvalidDataException("无法解析配方 JSON。");
    }
}
