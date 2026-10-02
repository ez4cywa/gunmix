using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GunMix.Core.Fire;

public sealed record EvidenceFact(string Id, string State, string Finding, string MissingLink);
public static class EvidenceLedger
{
    public static readonly EvidenceFact[] Facts = [
        new("command8", "confirmed", "类型 8 的生产、消费、回收链和标志位读写已核验", ""),
        new("secondary", "partial", "关联引用继续进入选择和播放；存在计数/兼容限制", "运行引用 +0x18 与导出 alias2 的逐行对应"),
        new("last", "partial", "末弹根与条件槽位存在", "弹药谓词到标志位和 last 根"),
        new("ads", "partial", "条件选择机制与混合 ADS 文件名容器存在", "真实 context 字段与成员对应"),
        new("release", "unknown", "interrupt 记录存在", "TriggerUp 到别名的分支"),
        new("fcg", "unknown", "FCG 记录存在", "每个 FCG 动作的独立触发条件"),
        new("numeric", "unknown", "简化导出缺失增益/音高/权重/循环字段", "本构建序列化定义与运行字段解码") ];
    public const string BuildSha256="e73eed58873bab808641de6eaf216bb35c87e45b5462a92efe87460650dd8d74";
}
public sealed class SoundDefinitionRow
{
    public int RowIndex { get; set; }
    public string Alias { get; set; }="";
    public string? Secondary { get; set; }
    public string Snd { get; set; }="";
    public string RawJson { get; set; }="";
    public string? ResolvedPath { get; set; }
    public string? SourceHash { get; set; }
    public bool AdsCandidate { get; set; }
    public double? GainDb { get; set; }
    public double? PitchRatio { get; set; }
    public double? Weight { get; set; }
    public bool? Loop { get; set; }
    public string RowId { get; set; }="";
    public static string? NormalizeHash(string value)=>Regex.IsMatch(value,"^[0-9a-fA-F]{1,16}$")?value.ToLowerInvariant().PadLeft(16,'0'):null;
}
public sealed class SoundDefinitionBank
{
    public string BankKey { get; set; }="";
    public string Name { get; set; }="";
    public string SourcePath { get; set; }="";
    public string SourceSha256 { get; set; }="";
    public string ParseCapability { get; set; }="exportedAssociationsOnly";
    public List<SoundDefinitionRow> Rows { get; set; }=[];
    public List<string> Diagnostics { get; set; }=[];
    public static SoundDefinitionBank Load(string file,string soundsRoot)
    {
        var bytes=File.ReadAllBytes(file);var sha=Convert.ToHexStringLower(SHA256.HashData(bytes));
        using var doc=JsonDocument.Parse(bytes);
        if(doc.RootElement.ValueKind!=JsonValueKind.Array)throw new InvalidDataException("不支持的 bank 格式：需要原始导出行数组。");
        var bank=new SoundDefinitionBank{Name=Path.GetFileNameWithoutExtension(file),SourcePath=Path.GetFullPath(file),SourceSha256=sha};
        bank.BankKey=$"{bank.Name}:{sha}";var root=Path.GetFullPath(soundsRoot);int index=0;
        foreach(var row in doc.RootElement.EnumerateArray())
        {
            string? Str(string key)=>row.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
            var r=new SoundDefinitionRow{RowIndex=index++,Alias=Str("alias")??"",Secondary=Str("alias2"),Snd=Str("snd")??"",RawJson=row.GetRawText()};
            r.RowId=$"{bank.BankKey}:{r.RowIndex}";r.AdsCandidate=Regex.IsMatch(r.Snd,@"(?:^|_)ads(?:_|\.)",RegexOptions.IgnoreCase);
            if(r.Alias.Length==0)bank.Diagnostics.Add($"行 {r.RowIndex}：缺少 alias");
            if(!r.Snd.StartsWith("sound_",StringComparison.Ordinal)&&r.Snd.Length>0)
            {
                var path=Path.GetFullPath(Path.Combine(root,r.Snd.Replace('/',Path.DirectorySeparatorChar)+".wav"));
                if(!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                    bank.Diagnostics.Add($"行 {r.RowIndex}：路径越出声音根目录");
                else if(File.Exists(path)){r.ResolvedPath=path;r.SourceHash=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));}
            }
            if(r.ResolvedPath==null)bank.Diagnostics.Add($"行 {r.RowIndex}：素材缺失 {r.Snd}");
            bank.Rows.Add(r);
        }
        foreach(var group in bank.Rows.GroupBy(r=>r.Alias))
            if(group.Select(r=>r.Secondary).Where(x=>!string.IsNullOrEmpty(x)).Distinct().Count()>1)bank.Diagnostics.Add($"{group.Key}：关联目标冲突");
        return bank;
    }
}
public sealed class RelationOverride
{
    public string BankKey { get; set; }="";
    public string FromAlias { get; set; }="";
    public string ToAlias { get; set; }="";
    public bool Enabled { get; set; }
    public double DelayMs { get; set; }
    public double GainDb { get; set; }
    public string RuleOrigin { get; set; }="projectAuthored";
}
public sealed class FireProfile
{
    public int SchemaVersion { get; set; }=3;
    public bool Enabled { get; set; }
    public string RuleOrigin { get; set; }="projectAuthored";
    public FireMode Mode { get; set; }=FireMode.Auto;
    public int Ammo { get; set; }=30;
    public int BurstSize { get; set; }=3;
    public long? ReleaseFrame { get; set; }
    public long ReadyFrame { get; set; }
    public long CycleFrames { get; set; }
    public bool CompleteBurstOnRelease { get; set; }
    public FireContext Context { get; set; }=new();
    public List<TriggerInput> Inputs { get; set; }=[];
    public ReleasePolicy ReleasePolicy { get; set; }
    public double FadeMs { get; set; }=30;
    public int MaxDepth { get; set; }=16;
    public int MaxInstances { get; set; }=256;
    public bool AllowFilenameContextFallback { get; set; }
    public List<SoundDefinitionBank> Banks { get; set; }=[];
    public List<RelationOverride> Relations { get; set; }=[];
    public FireProfile Clone()=>JsonSerializer.Deserialize<FireProfile>(JsonSerializer.Serialize(this))!;
    public FireScenario Scenario(int rate,int rpm,int count,bool single)
    {
        // Stored frame inputs are expressed at the project reference rate 48 kHz.
        long Scale(long v)=>checked((long)decimal.Round((decimal)v*rate/48000,0,MidpointRounding.AwayFromZero));
        long release=single?FireController.FrameAt(1,rate,rpm):ReleaseFrame is { } r?Scale(r):FireController.FrameAt(count,rate,rpm);
        return new(){SampleRate=rate,Rpm=rpm,Ammo=Ammo,AttemptLimit=single?1:count,BurstSize=BurstSize,
            Mode=single?FireMode.Single:Mode,ReadyFrame=Scale(ReadyFrame),CycleFrames=Scale(CycleFrames),Context=Context,
            CompleteBurstOnRelease=CompleteBurstOnRelease,
            Inputs=!single&&Inputs.Count>0?Inputs.Select(i=>i with{Frame=Scale(i.Frame)}).ToList():[new(0,FireEventKind.TriggerDown),new(release,FireEventKind.TriggerUp)]};
    }
}
