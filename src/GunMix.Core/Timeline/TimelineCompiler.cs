using GunMix.Core.Assets;
using GunMix.Core.Model;

namespace GunMix.Core.Timeline;

public sealed record TimelineValidationIssue(string LayerName, string Message, bool IsError = true);

public sealed record CompiledTimeline
{
    public required List<ShotEvent> Events { get; init; }

    /// <summary>输出总长（采样）：所有事件结束位置最大值，确保尾部保留。</summary>
    public required long TotalSamples { get; init; }

    public required Dictionary<Guid, AssetInfo> AssetById { get; init; }

    public List<TimelineValidationIssue> Issues { get; init; } = [];

    public int EventCount => Events.Count;
    public List<Fire.DomainEvent> DomainEvents { get; init; } = [];
    public List<Fire.FireCommand> Commands { get; init; } = [];
    public int? AmmoAfter { get; init; }
    public long? ReleaseFrame { get; init; }
    public string InputFingerprint { get; init; } = "";
}

/// <summary>
/// 时间线编译器：将参数编译为明确的样本事件；解决变体和时序，不访问音频设备。
/// 连发按每发绝对时间计算并四舍五入到最近采样，不反复累加取整后的间隔。
/// </summary>
public static class TimelineCompiler
{
    public const int MinRpm = 6;
    public const int MaxRpm = 1800;
    public const int MinShots = 1;
    public const int MaxShots = 100;

    /// <summary>生成事件清单（不改变音频）。需要先更新预览时由失效检测触发重建。</summary>
    public static EventManifest BuildManifest(Recipe recipe, IEnumerable<AssetInfo> allAssets, Guid weaponId, ManifestKind kind)
    {
        var assets = allAssets.ToList();
        int shotCount = kind == ManifestKind.Single ? 1 : Math.Clamp(recipe.BurstShotCount, MinShots, MaxShots);
        var manifest = new EventManifest { Kind = kind, Rpm = recipe.BurstRpm, ShotCount = shotCount,
            InputFingerprint = CompileFingerprint.Create(recipe,kind), AssetFingerprint = CompileFingerprint.Create(recipe,kind,assets,weaponId) };
        if (recipe.FireProfile?.Enabled == true)
        {
            var plan = Fire.FirePlanCompiler.Build(recipe,assets,weaponId,48000,kind);
            manifest.Entries = plan.Events.Select(e=>new ManifestEntry{LayerId=e.LayerId,AssetId=e.AssetId,ShotIndex=e.ShotIndex,InstanceId=e.InstanceId}).ToList();
            return manifest;
        }
        foreach (var layer in recipe.Layers)
        {
            if (!layer.Enabled) continue;
            // 松扳机（释放时刻）只属于连发序列：单发导出保持一次干净的击发
            if (kind == ManifestKind.Single && layer.IsReleaseTail) continue;
            var pool = AssetService.ResolvePool(layer, assets, weaponId);
            if (pool.Count == 0)
            {
                // 缺素材的层留待验证报告；清单中不偷偷借用其他层
                continue;
            }
            var picks = VariantSelector.SelectForLayer(layer, recipe, pool, shotCount, kind);
            for (int i = 0; i < picks.Count; i++)
            {
                if (layer.IsExperimental && layer.Trigger == ExperimentalTrigger.ShotN && i + 1 != layer.TriggerShotNumber)
                    continue;
                if (layer.IsExperimental && layer.Trigger == ExperimentalTrigger.ReleaseMoment && i != shotCount - 1)
                    continue;
                manifest.Entries.Add(new ManifestEntry { LayerId = layer.Id, ShotIndex = i, AssetId = picks[i] });
            }
        }
        return manifest;
    }

    /// <summary>将清单编译为样本事件。参数无效时返回具体问题，不使用未显示的替代值。</summary>
    public static CompiledTimeline Compile(
        Recipe recipe, EventManifest manifest, IReadOnlyList<AssetInfo> allAssets, Guid weaponId,
        int sampleRate, ManifestKind kind)
    {
        if (recipe.FireProfile?.Enabled == true)
        {
            var plan = Fire.FirePlanCompiler.Build(recipe,allAssets,weaponId,sampleRate,kind);
            if(manifest.Matches(recipe,kind,allAssets,weaponId))
            {
                var overrides = manifest.Entries.Where(e=>e.InstanceId.Length>0).ToDictionary(e=>e.InstanceId);
                var updated=plan.Events.Select(e=>overrides.TryGetValue(e.InstanceId,out var m)&&plan.AssetById.TryGetValue(m.AssetId,out var a)&&Fire.BankAssetBinding.CanOverride(e,a,recipe,weaponId)
                    ?e with{AssetId=m.AssetId,SourceHash=a.Sha256,AssetOwnerWeaponId=a.WeaponId,SelectionReason=e.AssetId==m.AssetId?e.SelectionReason:"项目清单手动覆盖（原定义行保留作参考）"}:e).ToList();
                return plan with{Events=updated,TotalSamples=updated.Count==0?0:updated.Max(e=>EventEnd(e,plan.AssetById,sampleRate))};
            }
            return plan;
        }
        if (manifest.InputFingerprint.Length>0 && !manifest.Matches(recipe,kind,allAssets,weaponId))
            manifest=BuildManifest(recipe,allAssets,weaponId,kind);
        var issues = new List<TimelineValidationIssue>();
        var events = new List<ShotEvent>();
        var assetById = allAssets.ToDictionary(a => a.Id);
        long maxEnd = 0;

        double intervalSeconds = 60.0 / Math.Max(1, recipe.BurstRpm);

        if (kind == ManifestKind.Burst)
        {
            if (recipe.BurstRpm is < MinRpm or > MaxRpm)
                issues.Add(new TimelineValidationIssue("(连发)", $"射速 {recipe.BurstRpm} RPM 超出范围 {MinRpm}–{MaxRpm}。"));
            if (recipe.BurstShotCount is < MinShots or > MaxShots)
                issues.Add(new TimelineValidationIssue("(连发)", $"发数 {recipe.BurstShotCount} 超出范围 {MinShots}–{MaxShots}。"));
        }

        foreach (var layer in recipe.Layers)
        {
            if (!layer.Enabled) continue;
            if (layer.GainDb is < -60 or > 6)
                issues.Add(new TimelineValidationIssue(layer.Name, $"增益 {layer.GainDb:0.#} dB 超出范围 -60 ～ +6。"));
            if (layer.DelayMs is < 0 or > 1000)
                issues.Add(new TimelineValidationIssue(layer.Name, $"延时 {layer.DelayMs:0.##} ms 超出范围 0 ～ 1000。"));

            long delaySamples = (long)Math.Round(layer.DelayMs / 1000.0 * sampleRate, MidpointRounding.ToEven);
            var entries = manifest.Entries.Where(e => e.LayerId == layer.Id).OrderBy(e => e.ShotIndex).ToList();

            if (entries.Count == 0)
            {
                if (AssetService.ResolvePool(layer, allAssets, weaponId).Count == 0)
                    issues.Add(new TimelineValidationIssue(layer.Name, "素材池为空或素材缺失，该层没有事件。"));
                continue;
            }

            if (layer.IsExperimental && layer.Trigger == ExperimentalTrigger.ShotN &&
                (layer.TriggerShotNumber < 1 || layer.TriggerShotNumber > (kind == ManifestKind.Burst ? recipe.BurstShotCount : 1)))
            {
                issues.Add(new TimelineValidationIssue(layer.Name,
                    $"实验触发指定第 {layer.TriggerShotNumber} 发，超出序列长度 {(kind == ManifestKind.Burst ? recipe.BurstShotCount : 1)}。"));
                continue;
            }

            int layerFirstEvent = events.Count;
            foreach (var entry in entries)
            {
                if (!assetById.TryGetValue(entry.AssetId, out var asset))
                {
                    issues.Add(new TimelineValidationIssue(layer.Name, $"清单引用的素材不存在（{entry.AssetId}）。"));
                    continue;
                }
                long start = kind == ManifestKind.Single
                    ? 0
                    : (long)Math.Round(entry.ShotIndex * intervalSeconds * sampleRate, MidpointRounding.ToEven);
                if (layer.IsExperimental && layer.Trigger == ExperimentalTrigger.ReleaseMoment)
                {
                    // 释放时刻 = 最后一发起点 + 一个射击间隔（不是最长音频的结束位置）
                    start = ReleaseMomentSample(recipe, kind, sampleRate);
                }
                long startSample = Math.Max(0, start + delaySamples);
                long assetSamples = (long)Math.Ceiling(asset.Format.FrameCount * (double)sampleRate / asset.Format.SampleRate / layer.PitchRatio);
                maxEnd = Math.Max(maxEnd, startSample + assetSamples);
                events.Add(new ShotEvent
                {
                    LayerId = layer.Id,
                    AssetId = entry.AssetId,
                    ShotIndex = entry.ShotIndex,
                    StartSample = startSample,
                    GainDb = layer.GainDb,
                    PitchRatio = layer.PitchRatio,
                    SourceOffsetSamples = 0,
                });
            }

            if (kind == ManifestKind.Burst && layer.BurstVoiceLimit > 0)
                ApplyVoiceLimit(events, layerFirstEvent, layer.BurstVoiceLimit, assetById, sampleRate);

        }

        events.Sort((a, b) => a.StartSample.CompareTo(b.StartSample));
        if (recipe.Layers.Any(l => l.Enabled && l.BurstVoiceLimit > 0) && kind == ManifestKind.Burst)
            maxEnd = events.Count == 0 ? 0 : events.Max(e => EventEnd(e, assetById, sampleRate));
        return new CompiledTimeline
        {
            Events = events,
            TotalSamples = maxEnd,
            AssetById = assetById,
            Issues = issues,
            InputFingerprint = CompileFingerprint.Create(recipe,kind,allAssets,weaponId),
        };
    }

    /// <summary>
    /// 同层实例上限：第 i 个实例起点时，仍在发声的最旧实例从该点开始淡出（经典的 voice stealing）。
    /// 只处理本层自 firstIndex 起新增的事件；输出总长由调用方在全部层处理后重算。
    /// </summary>
    internal static void ApplyVoiceLimit(List<ShotEvent> events, int firstIndex, int limit,
        Dictionary<Guid, AssetInfo> assetById, int sampleRate)
    {
        long fade = Math.Max(1, (long)Math.Round(Layer.VoiceStealFadeMs / 1000.0 * sampleRate));
        var active = new List<int>(); // 按起点顺序的在发声实例索引
        var order = Enumerable.Range(firstIndex, events.Count - firstIndex)
            .OrderBy(i => events[i].StartSample).ToList();
        foreach (int i in order)
        {
            long now = events[i].StartSample;
            active.RemoveAll(j => EventEnd(events[j], assetById, sampleRate) <= now);
            while (active.Count >= limit)
            {
                int oldest = active[0];
                active.RemoveAt(0);
                events[oldest] = events[oldest] with { FadeOutStartSample = now, FadeOutSamples = fade };
            }
            active.Add(i);
        }
    }

    /// <summary>事件在输出中的结束位置（考虑抢占淡出）。</summary>
    public static long EventEnd(ShotEvent e, IReadOnlyDictionary<Guid, AssetInfo> assetById, int sampleRate)
    {
        long natural = e.StartSample;
        if (assetById.TryGetValue(e.AssetId, out var asset))
            natural += (long)Math.Ceiling(asset.Format.FrameCount * (double)sampleRate / asset.Format.SampleRate / e.PitchRatio);
        return e.FadeOutStartSample is { } fs ? Math.Min(natural, fs + e.FadeOutSamples) : natural;
    }

    private static long MaxStartOf(List<ManifestEntry> entries, ManifestKind kind, double intervalSeconds, int sampleRate)
    {
        if (kind == ManifestKind.Single) return 0;
        int maxShot = entries.Max(e => e.ShotIndex);
        return (long)Math.Round(maxShot * intervalSeconds * sampleRate, MidpointRounding.ToEven);
    }

    /// <summary>释放时刻采样点（用于界面显示）。</summary>
    public static long ReleaseMomentSample(Recipe recipe, ManifestKind kind, int sampleRate)
    {
        if (kind == ManifestKind.Single) return 0;
        if(recipe.FireProfile?.Enabled==true)
            return Fire.FireController.Compile(recipe.FireProfile.Scenario(sampleRate,recipe.BurstRpm,recipe.BurstShotCount,false)).ReleaseFrame;
        long interval = (long)Math.Round(60.0 / recipe.BurstRpm * sampleRate, MidpointRounding.ToEven);
        long last = (long)Math.Round((recipe.BurstShotCount - 1) * (60.0 / recipe.BurstRpm) * sampleRate, MidpointRounding.ToEven);
        return Fire.FireController.FrameAt(recipe.BurstShotCount, sampleRate, recipe.BurstRpm);
    }
}
