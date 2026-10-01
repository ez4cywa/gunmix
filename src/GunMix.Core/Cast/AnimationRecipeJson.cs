using System.Text.Json;
using System.Text.Encodings.Web;
using GunMix.Core.Model;
using GunMix.Core.Timeline;

namespace GunMix.Core.Cast;

/// <summary>
/// 动画导出配套 JSON：动画标识与帧率（实测）、逐事件帧号/时间/别名/所选文件与来源、
/// 置信度与是否自动采用、输出设置、峰值与截尾记录。
/// 别名来自动画文件实测；别名→文件的对应由用户指定，原版声音配置不在导出中，报告必须如实区分两者。
/// </summary>
public static class AnimationRecipeJson
{
    public const string SoftwareVersion = "枪声分层工作台 0.1.0";

    public static string Build(
        AnimationClip clip,
        CompiledTimeline timeline,
        IReadOnlyList<AssetInfo> assets,
        int sampleRate,
        int bitDepth,
        bool dither,
        int ditherSeed,
        double peakDbfs,
        double durationSeconds,
        double? appliedGainDb,
        (double ThresholdDb, double TailMs, long RemovedFrames)? tailTrim,
        string wavFileName,
        int bankCount = 0)
    {
        var byId = assets.ToDictionary(a => a.Id);
        var events = timeline.Events.ToLookup(e => e.LayerId);

        var doc = new
        {
            schema = "gunmix-animation/1",
            software = SoftwareVersion,
            kind = "animation",
            animation = clip.Name,
            cast_file = clip.CastFileName,
            weapon_key = clip.WeaponKey,
            framerate = clip.Framerate,
            looping = clip.Looping,
            master_gain_db = clip.MasterGainDb,
            wav = wavFileName,
            sample_rate = sampleRate,
            channels = 2,
            bit_depth = bitDepth,
            dither = new { enabled = dither, seed = dither ? ditherSeed : 0, algorithm = dither ? Audio.Quantizer.TpdfVersion : "none" },
            duration_s = Math.Round(durationSeconds, 6),
            peak_dbfs = Math.Round(peakDbfs, 3),
            attenuation_applied_db = appliedGainDb,
            tail_trim = tailTrim == null
                ? null
                : new
                {
                    threshold_db = Math.Round(tailTrim.Value.ThresholdDb, 1),
                    tail_ms = Math.Round(tailTrim.Value.TailMs, 1),
                    removed_s = Math.Round(tailTrim.Value.RemovedFrames / (double)sampleRate, 4),
                },
            evidence = new
            {
                animation_and_frames = "来自 .cast 文件实测解析（CastAnimNoteDumper 同源读法）",
                alias_to_file_mapping = "优先来自 sndbanks/json 的原版声音配置（weapon_rex_* bank）；bank 无记录时才退回同武器内的名称推断或用户指定。不宣称原版还原。",
                banks_loaded = bankCount,
            },
            events = clip.Events.Select(e =>
            {
                var rendered = events[e.Id].FirstOrDefault();
                byId.TryGetValue(e.AssetId ?? Guid.Empty, out var asset);
                return new
                {
                    frame = e.Frame,
                    time_s = Math.Round(clip.FrameToSeconds(e.Frame), 6),
                    alias = e.Alias,
                    enabled = e.Enabled,
                    gain_db = e.GainDb,
                    source = e.Source.ToString(),
                    bank = e.BankName.Length > 0 ? e.BankName : null,
                    container_size = e.ContainerIds.Count,
                    container_mode = e.ContainerIds.Count > 1 ? e.ContainerMode.ToString() : null,
                    bank_missing_files = e.BankMissingCount > 0 ? e.BankMissingCount : (int?)null,
                    auto_assigned = e.AutoAssigned,
                    file = asset?.FileName ?? "",
                    sha256 = asset?.Sha256 ?? "",
                    render_start_sample = rendered?.StartSample ?? 0,
                    candidate_count = e.CandidateIds.Count,
                };
            }),
            skipped_notes = clip.OtherNotes,
        };

        return JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }
}
