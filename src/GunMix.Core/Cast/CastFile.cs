namespace GunMix.Core.Cast;

/// <summary>
/// CAST 动画文件读取器：只解析音效装配需要的最小信息（动画帧率、循环、通知名与关键帧）。
/// 格式与解析逻辑参考 CastAnimNoteDumper v2.0.0（MIT，Copyright (c) 2026 Pathfinder_FUFU），
/// 其内嵌读取器又改编自 DTZxPorter 的开源 CAST 项目（MIT）。此处为独立的 C# 实现，
/// 不依赖 Python 或外部可执行文件；只读源文件，不修改。
/// </summary>
public static class CastFile
{
    private const uint Magic = 0x74736163; // "cast"
    private const uint NodeAnim = 0x6D696E61;
    private const uint NodeNotif = 0x6669746E;
    private const int NodeHeaderSize = 0x18;

    public sealed class Node
    {
        public uint Identifier { get; init; }
        public Dictionary<string, List<object>> Properties { get; init; } = new();
        public List<Node> Children { get; init; } = [];

        public List<object>? Prop(string name) => Properties.GetValueOrDefault(name);

        public string? StringProp(string name)
        {
            var v = Prop(name);
            return v is { Count: > 0 } && v[0] is string s ? s : null;
        }

        public double DoubleProp(string name, double fallback)
        {
            var v = Prop(name);
            if (v is { Count: > 0 } && v[0] is IConvertible)
                return Convert.ToDouble(v[0]);
            return fallback;
        }

        public bool BoolProp(string name)
        {
            var v = Prop(name);
            return v is { Count: > 0 } && v[0] is IConvertible && Convert.ToDouble(v[0]) != 0;
        }

        public List<long> LongProp(string name)
        {
            var v = Prop(name);
            if (v == null) return [];
            return v.Where(x => x is IConvertible).Select(x => Convert.ToInt64(x)).ToList();
        }
    }

    public sealed class CastFormatException(string message) : Exception(message);

    /// <summary>读取 .cast 的全部根节点。文件损坏或格式不符时抛出具体原因。</summary>
    public static List<Node> Read(string path)
    {
        using var fs = File.Open(path, FileMode.Open, FileAccess.Read);
        using var f = new BinaryReader(fs);
        if (fs.Length < 16)
            throw new CastFormatException("文件小于 16 字节，不是有效的 CAST 文件。");
        uint magic = f.ReadUInt32();
        if (magic != Magic)
            throw new CastFormatException($"CAST 魔数不匹配（读到 0x{magic:X8}，期望 0x{Magic:X8}）。");
        _ = f.ReadUInt32(); // version
        uint rootCount = f.ReadUInt32();
        _ = f.ReadUInt32(); // reserved

        if (rootCount > 4096)
            throw new CastFormatException($"根节点数量异常（{rootCount}），文件可能已损坏。");

        var roots = new List<Node>();
        for (uint i = 0; i < rootCount; i++)
        {
            var node = ReadNode(f);
            if (node != null) roots.Add(node);
        }
        return roots;
    }

    private static Node? ReadNode(BinaryReader f)
    {
        if (f.BaseStream.Length - f.BaseStream.Position < NodeHeaderSize) return null;
        uint identifier = f.ReadUInt32();
        _ = f.ReadUInt32();   // node size（未使用）
        _ = f.ReadUInt64();   // hash
        uint propCount = f.ReadUInt32();
        uint childCount = f.ReadUInt32();

        if (propCount > 4096 || childCount > 65536)
            throw new CastFormatException($"节点 0x{identifier:X8} 的属性/子节点数量异常（{propCount}/{childCount}）。");

        var node = new Node { Identifier = identifier };
        for (uint i = 0; i < propCount; i++)
        {
            var (name, values) = ReadProperty(f);
            node.Properties[name] = values;
        }
        for (uint i = 0; i < childCount; i++)
        {
            var child = ReadNode(f);
            if (child != null) node.Children.Add(child);
        }
        return node;
    }

    private static (string Name, List<object> Values) ReadProperty(BinaryReader f)
    {
        byte[] header = f.ReadBytes(8);
        if (header.Length < 8) throw new CastFormatException("属性头不完整，文件被截断。");
        string typeId = System.Text.Encoding.ASCII.GetString(header, 0, 2).TrimEnd('\0');
        int nameLen = BitConverter.ToUInt16(header, 2);
        uint count = BitConverter.ToUInt32(header, 4);

        byte[] nameBytes = f.ReadBytes(nameLen);
        string name = System.Text.Encoding.UTF8.GetString(nameBytes).TrimEnd('\0');

        if (!TypeSizes.TryGetValue(typeId, out var info))
            return (name, []); // 未收录的类型：跳过取值，不影响通知解析

        var values = new List<object>((int)Math.Min(count, 1024));
        if (info.IsString)
        {
            values.Add(ReadCString(f));
            return (name, values);
        }

        long need = (long)info.Size * count;
        if (f.BaseStream.Length - f.BaseStream.Position < need)
            throw new CastFormatException($"属性 {name}（类型 {typeId}）数据不足，文件被截断。");

        for (uint i = 0; i < count; i++)
            values.Add(ReadValue(f, typeId, info));
        return (name, values);
    }

    private static object ReadValue(BinaryReader f, string typeId, TypeInfo info) => typeId switch
    {
        "b" => f.ReadByte(),
        "h" => f.ReadInt16(),
        "i" => f.ReadInt32(),
        "l" => f.ReadUInt64(),
        "f" => f.ReadSingle(),
        "d" => f.ReadDouble(),
        "2v" => new VectorValue([f.ReadSingle(), f.ReadSingle()]),
        "3v" => new VectorValue([f.ReadSingle(), f.ReadSingle(), f.ReadSingle()]),
        "4v" => new VectorValue([f.ReadSingle(), f.ReadSingle(), f.ReadSingle(), f.ReadSingle()]),
        _ => 0d,
    };

    private static string ReadCString(BinaryReader f)
    {
        var sb = new System.Text.StringBuilder();
        while (f.BaseStream.Position < f.BaseStream.Length)
        {
            byte b = f.ReadByte();
            if (b == 0) break;
            sb.Append((char)b);
        }
        return sb.ToString();
    }

    private readonly record struct TypeInfo(int Size, bool IsString);

    private static readonly Dictionary<string, TypeInfo> TypeSizes = new()
    {
        ["b"] = new(1, false),
        ["h"] = new(2, false),
        ["i"] = new(4, false),
        ["l"] = new(8, false),
        ["f"] = new(4, false),
        ["d"] = new(8, false),
        ["s"] = new(0, true),
        ["2v"] = new(8, false),
        ["3v"] = new(12, false),
        ["4v"] = new(16, false),
    };

    /// <summary>向量值（本工具不解读，仅保留以便将来扩展）。</summary>
    public sealed record VectorValue(float[] Components);

    /// <summary>音频通知别名前缀：AudioOneShot@&lt;别名&gt;。</summary>
    public const string OneShotPrefix = "AudioOneShot@";

    /// <summary>
    /// 从根节点提取动画剪辑。通知节点在动画子树内递归收集（嵌套在曲线组下时也能找到），
    /// 但不深入通知节点自身，避免重复计数。
    /// </summary>
    public static List<AnimClipRaw> ExtractClips(IEnumerable<Node> roots)
    {
        var clips = new List<AnimClipRaw>();
        foreach (var root in roots)
            Walk(root, clips);
        return clips;
    }

    private static void Walk(Node node, List<AnimClipRaw> clips)
    {
        if (node.Identifier == NodeAnim)
        {
            var clip = new AnimClipRaw
            {
                Name = node.StringProp("n") ?? "",
                Framerate = node.DoubleProp("fr", 30.0),
                Looping = node.BoolProp("lo"),
            };
            if (clip.Framerate <= 0) clip.Framerate = 30.0;
            CollectNotifications(node, clip);
            clips.Add(clip);
            return; // 动画节点之下不再嵌套动画
        }
        foreach (var child in node.Children)
            Walk(child, clips);
    }

    private static void CollectNotifications(Node node, AnimClipRaw clip)
    {
        foreach (var child in node.Children)
        {
            if (child.Identifier == NodeNotif)
            {
                string name = child.StringProp("n") ?? "";
                var frames = child.LongProp("kb");
                foreach (var frame in frames)
                {
                    if (name.StartsWith(OneShotPrefix, StringComparison.Ordinal))
                        clip.AudioEvents.Add(new AudioNote((int)frame, name[OneShotPrefix.Length..]));
                    else
                        clip.OtherNotes.Add(new OtherNote((int)frame, name));
                }
                continue; // 不深入通知节点
            }
            CollectNotifications(child, clip);
        }
    }

    public sealed record AudioNote(int Frame, string Alias);
    public sealed record OtherNote(int Frame, string Name);

    public sealed class AnimClipRaw
    {
        public string Name { get; set; } = "";
        public double Framerate { get; set; } = 30.0;
        public bool Looping { get; set; }
        public List<AudioNote> AudioEvents { get; } = [];
        public List<OtherNote> OtherNotes { get; } = [];
    }

    /// <summary>解析单个文件；失败时返回具体原因，不抛出到界面层。</summary>
    public static (AnimClipRaw? Clip, string? Error) TryRead(string path, double fallbackFps = 30.0)
    {
        try
        {
            var roots = Read(path);
            var clips = ExtractClips(roots);
            if (clips.Count == 0)
                return (null, "未找到动画节点。");
            var clip = clips[0];
            if (clips.Count > 1)
            {
                // 少数文件含多个动画节点：合并全部音频事件，保留第一个的帧率
                for (int i = 1; i < clips.Count; i++)
                {
                    clip.AudioEvents.AddRange(clips[i].AudioEvents);
                    clip.OtherNotes.AddRange(clips[i].OtherNotes);
                }
                clip.AudioEvents.Sort((a, b) => a.Frame.CompareTo(b.Frame));
                clip.OtherNotes.Sort((a, b) => a.Frame.CompareTo(b.Frame));
            }
            return (clip, null);
        }
        catch (CastFormatException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex)
        {
            return (null, $"读取失败：{ex.Message}");
        }
    }
}
