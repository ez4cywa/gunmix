namespace GunMix.Core.Audio;

/// <summary>素材的解码缓冲与波形峰值。</summary>
public sealed class AssetBuffer
{
    public required float[] Data { get; init; }

    public required int Channels { get; init; }

    public required int SampleRate { get; init; }

    public required long FrameCount { get; init; }
}

public sealed record WaveformPeaks(float[] Min, float[] Max, int Buckets);

/// <summary>
/// 按需解码与缓存：解码为原声道 float32（约 97.75 MiB/全部素材），使用预算内的 LRU 缓存；
/// 波形缩略图单独缓存，不为每条轨道复制整份素材。
/// </summary>
public sealed class AudioCache : IDisposable
{
    private readonly long _budgetBytes;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, LinkedListNode<(Guid Id, AssetBuffer Buffer)>> _map = [];
    private readonly LinkedList<(Guid Id, AssetBuffer Buffer)> _lru = [];
    private readonly Dictionary<(Guid Id, int Buckets), WaveformPeaks> _peaks = [];
    private long _bytes;

    public AudioCache(long budgetBytes = 256L * 1024 * 1024)
    {
        _budgetBytes = budgetBytes;
    }

    public event Action<string>? StatusMessage;

    /// <summary>取解码数据；缓存未命中时从磁盘解码（重采样到工程采样率）。</summary>
    public AssetBuffer Get(Guid id, string path, int targetSampleRate)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(id, out var node))
            {
                _lru.Remove(node);
                _lru.AddFirst(node);
                return node.Value.Buffer;
            }
        }

        var decoded = WavReader.Read(path);
        float[] data = decoded.Data;
        if (decoded.SampleRate != targetSampleRate)
            data = Resampler.Resample(data, decoded.Channels, decoded.SampleRate, targetSampleRate);

        long frameCount = data.LongLength / decoded.Channels;
        var buffer = new AssetBuffer { Data = data, Channels = decoded.Channels, SampleRate = targetSampleRate, FrameCount = frameCount };

        lock (_lock)
        {
            long size = data.LongLength * 4;
            while (_bytes + size > _budgetBytes && _lru.Count > 0)
            {
                var last = _lru.Last!;
                _lru.RemoveLast();
                _map.Remove(last.Value.Id);
                _bytes -= last.Value.Buffer.Data.LongLength * 4;
                StatusMessage?.Invoke("缓存已淘汰较早素材以控制内存。");
            }
            var node = _lru.AddFirst((id, buffer));
            _map[id] = node;
            _bytes += size;
        }
        return buffer;
    }

    /// <summary>波形峰值（按桶聚合 min/max），线程安全。</summary>
    public WaveformPeaks GetPeaks(Guid id, string path, int targetSampleRate, int buckets)
    {
        lock (_lock)
        {
            if (_peaks.TryGetValue((id, buckets), out var cached))
                return cached;
        }
        var buffer = Get(id, path, targetSampleRate);
        var peaks = ComputePeaks(buffer, buckets);
        lock (_lock)
        {
            _peaks[(id, buckets)] = peaks;
        }
        return peaks;
    }

    public static WaveformPeaks ComputePeaks(AssetBuffer buffer, int buckets)
    {
        var min = new float[buckets];
        var max = new float[buckets];
        Array.Fill(min, 0f);
        Array.Fill(max, 0f);
        long frames = buffer.FrameCount;
        int ch = buffer.Channels;
        var data = buffer.Data;
        for (int b = 0; b < buckets; b++)
        {
            long start = b * frames / buckets;
            long end = Math.Max(start, (b + 1) * frames / buckets);
            float lo = 0, hi = 0;
            for (long f = start; f < end; f++)
            {
                for (int c = 0; c < ch; c++)
                {
                    float v = data[f * ch + c];
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
            }
            min[b] = lo;
            max[b] = hi;
        }
        return new WaveformPeaks(min, max, buckets);
    }

    public void Invalidate(Guid id)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(id, out var node))
            {
                _bytes -= node.Value.Buffer.Data.LongLength * 4;
                _lru.Remove(node);
                _map.Remove(id);
            }
            var keys = _peaks.Keys.Where(k => k.Id == id).ToList();
            foreach (var k in keys) _peaks.Remove(k);
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _map.Clear();
            _lru.Clear();
            _peaks.Clear();
            _bytes = 0;
        }
    }

    public long CurrentBytes { get { lock (_lock) return _bytes; } }

    public void Dispose() => Clear();
}
