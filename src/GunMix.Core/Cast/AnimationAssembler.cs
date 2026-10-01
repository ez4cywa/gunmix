using GunMix.Core.Assets;
using GunMix.Core.Audio;
using GunMix.Core.Model;
using GunMix.Core.SoundBanks;
using GunMix.Core.Timeline;

namespace GunMix.Core.Cast;

public sealed record AnimScanReport
{
    public List<AnimationClip> Clips { get; init; } = [];

    /// <summary>新登记的动画音频素材（已存在的按 ID 复用）。</summary>
    public List<AssetInfo> NewAssets { get; init; } = [];

    /// <summary>素材 ID → 绝对路径（供工程登记）。</summary>
    public Dictionary<Guid, string> AssetPaths { get; init; } = [];

    public List<string> Failures { get; init; } = [];

    public int CastFilesSeen { get; init; }

    public int ClipsWithAudio { get; init; }

    public int TotalEvents { get; init; }

    public int AutoResolved { get; init; }

    /// <summary>由 soundbank（原版声音配置）落地的事件数。</summary>
    public int BankEvents { get; init; }

    /// <summary>其中属于随机容器（多文件）的事件数。</summary>
    public int BankContainers { get; init; }

    /// <summary>bank 收录但音频未导出（或落地失败）的事件数。</summary>
    public int BankMissing { get; init; }

    public int BanksLoaded { get; init; }
}

/// <summary>
/// 动画音效装配：扫描 .cast 得到动画与音频事件（实测），用 soundbank 把别名解析为原版指定的音频文件；
/// 没有 bank 记录时退回同武器内的名称推断，只有唯一命中才自动采用，其余留给用户指定。
/// </summary>
public static class AnimationAssembler
{
    /// <summary>
    /// 扫描。existingAssets 为工程已有素材表（按路径去重，避免重复登记）。
    /// <paramref name="banks"/> 为空时只做名称推断。
    /// </summary>
    public static AnimScanReport Scan(
        string animationDirectory,
        IReadOnlyList<string> soundDirectories,
        IReadOnlyCollection<AssetInfo> existingAssets,
        Action<int, int>? progress = null,
        Func<bool>? isCancelled = null,
        SoundBankIndex? banks = null)
    {
        var castFiles = Directory.EnumerateFiles(animationDirectory, "*.cast",
                new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 1) 素材表：扫描声音目录（只读元数据 + 哈希）
        var byPath = existingAssets
            .Select(a => (a, Path.Combine(a.SourceDirectory, a.FileName)))
            .Where(x => !string.IsNullOrEmpty(x.Item2))
            .ToDictionary(x => Norm(x.Item2), x => x.a, StringComparer.OrdinalIgnoreCase);

        var newAssets = new List<AssetInfo>();
        var assetPaths = new Dictionary<Guid, string>();
        var failures = new List<string>();
        var pool = new List<AssetInfo>(existingAssets);

        int seen = 0;
        foreach (var dir in soundDirectories)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var wav in Directory.EnumerateFiles(dir, "*.wav",
                         new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (isCancelled?.Invoke() == true) break;
                seen++;
                var norm = Norm(wav);
                if (byPath.TryGetValue(norm, out var existing))
                {
                    assetPaths[existing.Id] = wav;
                    continue;
                }
                var (info, error) = WavReader.ReadFormat(wav);
                if (info == null)
                {
                    failures.Add($"{wav}：{error}");
                    continue;
                }
                var asset = new AssetInfo
                {
                    SourceDirectory = Path.GetDirectoryName(wav) ?? "",
                    FileName = Path.GetFileName(wav),
                    Format = info,
                    Parsed = NameParser.Parse(Path.GetFileName(wav)),
                    GroupKey = AnimationSoundLibrary.BaseName(Path.GetFileName(wav)),
                    GroupSource = GroupSource.Auto,
                    WeaponId = Guid.Empty, // 动画素材不属于任何武器分层
                    Sha256 = SafeHash(wav),
                };
                newAssets.Add(asset);
                pool.Add(asset);
                byPath[norm] = asset;
                assetPaths[asset.Id] = wav;
            }
        }

        // 2) 动画：解析 cast，先按 soundbank（原版配置）解析别名，无记录时退回名称推断
        var library = new AnimationSoundLibrary(pool, a => assetPaths.TryGetValue(a.Id, out var p) ? p : "");
        var assetByPath = new Dictionary<string, AssetInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in pool)
        {
            var full = Path.Combine(a.SourceDirectory, a.FileName);
            assetByPath[full] = a;
        }
        var clips = new List<AnimationClip>();
        int castCount = 0, withAudio = 0, totalEvents = 0, autoResolved = 0;
        int bankEvents = 0, bankContainers = 0, bankMissing = 0;
        int index = 0;
        foreach (var cast in castFiles)
        {
            if (isCancelled?.Invoke() == true) break;
            index++;
            progress?.Invoke(index, castFiles.Count);

            var (raw, error) = CastFile.TryRead(cast);
            if (raw == null)
            {
                failures.Add($"{Path.GetFileName(cast)}：{error}");
                continue;
            }
            castCount++;
            if (raw.AudioEvents.Count == 0) continue; // 纯 IK/LOD 动画：不进入音效装配

            var name = Path.GetFileNameWithoutExtension(cast);
            var clip = new AnimationClip
            {
                CastFileName = Path.GetFileName(cast),
                CastPath = cast,
                Name = string.IsNullOrEmpty(raw.Name) ? name : raw.Name,
                WeaponKey = AnimationSoundLibrary.WeaponKeyOf(name) ?? "",
                Framerate = raw.Framerate,
                Looping = raw.Looping,
                OtherNotes = raw.OtherNotes.Select(n => $"[{n.Frame}] {n.Name}").ToList(),
            };
            foreach (var note in raw.AudioEvents)
            {
                var ev = new AnimEvent { Frame = note.Frame, Alias = note.Alias };
                var bankEntries = banks?.Lookup(note.Alias) ?? [];
                if (bankEntries.Count > 0)
                {
                    // 原版配置：容器成员权威。跨 bank 的重复路径去重后保持稳定顺序。
                    var ids = new List<Guid>();
                    foreach (var b in bankEntries)
                    {
                        if (!assetByPath.TryGetValue(b.ResolvedPath, out var asset)) continue;
                        if (!ids.Contains(asset.Id)) ids.Add(asset.Id);
                    }
                    if (ids.Count > 0)
                    {
                        ev.ContainerIds = ids;
                        ev.Source = ids.Count == 1 ? AnimSource.BankSingle : AnimSource.BankContainer;
                        ev.BankName = bankEntries[0].Bank;
                        ev.AssetId = ids[0];
                        ev.AutoAssigned = true;
                        autoResolved++;
                        bankEvents++;
                        if (ids.Count > 1) bankContainers++;
                    }
                    else
                    {
                        ev.Source = AnimSource.BankMissing;
                        ev.BankName = bankEntries[0].Bank;
                        ev.BankMissingCount = bankEntries.Count;
                        bankMissing++;
                    }
                }
                else if (banks != null && banks.MissingCount(note.Alias) > 0)
                {
                    // bank 收录了该别名，但引用的音频没有导出
                    ev.Source = AnimSource.BankMissing;
                    ev.BankMissingCount = banks.MissingCount(note.Alias);
                    bankMissing++;
                    var (candidates, src) = library.Resolve(clip, ev);
                    ev.CandidateIds = candidates;
                }
                else
                {
                    var (candidates, src) = library.Resolve(clip, ev);
                    ev.CandidateIds = candidates;
                    ev.Source = src;
                    if (candidates.Count == 1 && src is AnimSource.NameExact or AnimSource.NameSingle)
                    {
                        ev.AssetId = candidates[0];
                        ev.AutoAssigned = true;
                        autoResolved++;
                    }
                }
                clip.Events.Add(ev);
                totalEvents++;
            }
            clip.Events.Sort((a, b) => a.Frame.CompareTo(b.Frame));
            clips.Add(clip);
            withAudio++;
        }

        return new AnimScanReport
        {
            Clips = clips,
            NewAssets = newAssets,
            AssetPaths = assetPaths,
            Failures = failures,
            CastFilesSeen = castCount,
            ClipsWithAudio = withAudio,
            TotalEvents = totalEvents,
            AutoResolved = autoResolved,
            BankEvents = bankEvents,
            BankContainers = bankContainers,
            BankMissing = bankMissing,
            BanksLoaded = banks?.Banks.Count ?? 0,
        };
    }

    private static string SafeHash(string path)
    {
        try { return AssetService.ComputeHash(path); }
        catch { return ""; }
    }

    private static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    /// <summary>
    /// 重定位动画素材：优先工程登记的绝对路径，其次原来源目录，最后在已知根目录按文件名与哈希匹配。
    /// 找不到返回 null（界面显示“素材缺失”，不静默替换）。
    /// </summary>
    public static string? LocateAsset(AssetInfo asset, IReadOnlyDictionary<string, string> registeredPaths, Guid assetId)
    {
        if (registeredPaths.TryGetValue(assetId.ToString(), out var registered) && File.Exists(registered))
            return registered;
        var direct = Path.Combine(asset.SourceDirectory, asset.FileName);
        if (File.Exists(direct)) return direct;
        return null;
    }

    /// <summary>
    /// 把动画事件编译为渲染时间轴（复用分层混音的事件模型）。
    /// 起点 = 帧号 / 帧率，按绝对时间四舍五入到采样，不累加取整误差。
    /// 容器事件按种子从原版成员中选一个：同种子同成员集合得到同一选择，可重现；换种子即换样本。
    /// </summary>
    public static CompiledTimeline BuildTimeline(
        AnimationClip clip, IReadOnlyList<AssetInfo> assets, int sampleRate)
    {
        var byId = assets.ToDictionary(a => a.Id);
        var events = new List<ShotEvent>();
        var issues = new List<TimelineValidationIssue>();
        long maxEnd = 0;

        foreach (var ev in clip.Events)
        {
            if (!ev.Enabled) continue;
            var picked = PickContainerMember(clip, ev) ?? ev.AssetId;
            if (picked is not { } id)
            {
                issues.Add(new TimelineValidationIssue($"帧 {ev.Frame}",
                    ev.Source == AnimSource.BankMissing
                        ? $"别名 {ev.Alias} 在 soundbank 有记录，但引用的 {ev.BankMissingCount} 个音频未随素材导出。"
                        : $"别名 {ev.Alias} 尚未指定音频文件。"));
                continue;
            }
            if (!byId.TryGetValue(id, out var asset))
            {
                issues.Add(new TimelineValidationIssue($"帧 {ev.Frame}", "所选素材不在工程素材表中。"));
                continue;
            }
            long start = (long)Math.Round(clip.FrameToSeconds(ev.Frame) * sampleRate, MidpointRounding.ToEven);
            long assetSamples = asset.Format.FrameCount * sampleRate / Math.Max(1, asset.Format.SampleRate);
            maxEnd = Math.Max(maxEnd, start + assetSamples);
            events.Add(new ShotEvent
            {
                LayerId = ev.Id,
                AssetId = id,
                ShotIndex = ev.Frame,
                StartSample = start,
                GainDb = ev.GainDb + clip.MasterGainDb,
                SourceOffsetSamples = 0,
            });
        }

        events.Sort((a, b) => a.StartSample.CompareTo(b.StartSample));
        var referenced = events.Select(e => e.AssetId).Distinct().ToHashSet();
        var assetById = byId.Where(kv => referenced.Contains(kv.Key))
                            .ToDictionary(kv => kv.Key, kv => kv.Value);
        return new CompiledTimeline
        {
            Events = events,
            TotalSamples = maxEnd,
            AssetById = assetById,
            Issues = issues,
        };
    }

    /// <summary>
    /// 容器选样。用户显式指定（非自动采用）时优先尊重用户选择；
    /// 随机模式按 (种子, 别名, 帧号) 稳定哈希取成员——重扫不改选择，换种子才换样本。
    /// </summary>
    public static Guid? PickContainerMember(AnimationClip clip, AnimEvent ev)
    {
        if (ev.ContainerIds.Count == 0) return ev.AssetId;
        if (!ev.AutoAssigned && ev.AssetId != null) return ev.AssetId;   // 用户指定优先
        return ev.ContainerMode switch
        {
            VariantMode.Fixed => ev.ContainerIds[0],
            VariantMode.Rotation => ev.ContainerIds[0],
            _ => ev.ContainerIds[(int)(StableHash(clip.Seed, ev.Alias, ev.Frame) % (uint)ev.ContainerIds.Count)],
        };
    }

    /// <summary>与 Guid 无关的稳定哈希：别名与帧号决定，重新扫描不改变选择。</summary>
    private static uint StableHash(int seed, string alias, int frame)
    {
        uint h = ((uint)seed ^ 0x811C9DC5u) * 16777619u;
        foreach (var c in alias)
        {
            h ^= c;
            h *= 16777619u;
        }
        h ^= (uint)frame;
        h *= 16777619u;
        return h;
    }
}
