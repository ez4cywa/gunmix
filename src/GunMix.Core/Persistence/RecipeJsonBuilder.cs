using System.Text;
using System.Text.Json;
using GunMix.Core.Audio;
using GunMix.Core.Model;
using GunMix.Core.Timeline;

namespace GunMix.Core.Persistence;

/// <summary>导出配套 JSON：实际事件、输出设置、源哈希、最终峰值和软件版本。</summary>
public static class RecipeJsonBuilder
{
    public const string SoftwareVersion = "枪声分层工作台 0.3.0";

    public static string Build(
        GunProject project,
        Weapon weapon,
        Recipe recipe,
        EventManifest manifest,
        CompiledTimeline timeline,
        IReadOnlyDictionary<Guid, AssetInfo> assets,
        ManifestKind kind,
        int bitDepth,
        bool dither,
        int ditherSeed,
        double peakDbfs,
        double durationSeconds,
        double? appliedGainDb,
        string wavFileName,
        (double ThresholdDb, double TailMs, long RemovedFrames)? tailTrim = null)
    {
        var doc = new
        {
            schema = "gunmix-recipe/1",
            software = SoftwareVersion,
            kind = kind == ManifestKind.Single ? "single" : "burst",
            weapon = weapon.Name,
            weapon_type = weapon.TypeName,
            recipe = recipe.Name,
            input_fingerprint = timeline.InputFingerprint,
            fire_rules = recipe.FireProfile,
            domain_events = timeline.DomainEvents,
            commands = timeline.Commands,
            ammo_after = timeline.AmmoAfter,
            diagnostics = timeline.Issues,
            evidence = Fire.EvidenceLedger.Facts,
            reference_build_sha256 = Fire.EvidenceLedger.BuildSha256,
            adaptive_profile = recipe.AdaptiveProfile,
            recipe_notes = recipe.Notes.Length > 0 ? recipe.Notes : null,
            project = project.ProjectName,
            sample_rate = project.SampleRate,
            channels = 2,
            bit_depth = bitDepth,
            dither = new { enabled = dither, seed = dither ? ditherSeed : 0, algorithm = dither ? Quantizer.TpdfVersion : "none", version = VariantAlgorithms.RandomVersion },
            burst = kind == ManifestKind.Burst ? new
                {
                    rpm = recipe.BurstRpm,
                    shots = recipe.BurstShotCount,
                    release_tail = Synthesis.ReleaseTail.IsEnabled(recipe),
                    release_s = Synthesis.ReleaseTail.IsEnabled(recipe)
                        ? Math.Round(TimelineCompiler.ReleaseMomentSample(recipe, kind, project.SampleRate) / (double)project.SampleRate, 6)
                        : (double?)null,
                } : null,
            wav = wavFileName,
            duration_s = Math.Round(durationSeconds, 6),
            peak_dbfs = Math.Round(peakDbfs, 3),
            attenuation_applied_db = appliedGainDb,
            tail_trim = tailTrim == null
                ? null
                : new
                {
                    threshold_db = Math.Round(tailTrim.Value.ThresholdDb, 1),
                    tail_ms = Math.Round(tailTrim.Value.TailMs, 1),
                    removed_s = Math.Round(tailTrim.Value.RemovedFrames / (double)project.SampleRate, 4),
                },
            layers = recipe.Layers.Select(l => new
            {
                id = l.Id,
                name = l.Name,
                role = l.Role,
                enabled = l.Enabled,
                gain_db = l.GainDb,
                delay_ms = l.DelayMs,
                variant_mode = l.VariantMode.ToString(),
                pool = l.PoolGroupKey,
                burst_voice_limit = l.BurstVoiceLimit > 0 ? l.BurstVoiceLimit : (int?)null,
            }),
            events = timeline.Events.Select(e =>
            {
                var asset = assets.TryGetValue(e.AssetId, out var a) ? a : null;
                var layer = recipe.Layers.FirstOrDefault(l => l.Id == e.LayerId);
                return new
                {
                    layer = layer?.Name ?? "",
                    instance_id = e.InstanceId,
                    parent_instance_id = e.ParentInstanceId,
                    trigger_event_id = e.TriggerEventId,
                    command_id = e.CommandId,
                    bank_key = e.BankKey,
                    alias_id = e.AliasId,
                    row_index = e.RowIndex,
                    rule_origin = e.RuleOrigin,
                    selection_reason = e.SelectionReason,
                    context = e.ContextSnapshot,
                    pitch_ratio = e.PitchRatio,
                    shot = e.ShotIndex,
                    time_s = Math.Round(e.StartSample / (double)project.SampleRate, 6),
                    file = asset?.FileName ?? "",
                    gain_db = e.GainDb,
                    source_offset_s = e.SourceOffsetSamples / (double)project.SampleRate,
                    sha256 = asset?.Sha256 ?? "",
                    fade_out_s = e.FadeOutStartSample is { } fs
                        ? Math.Round(fs / (double)project.SampleRate, 6)
                        : (double?)null,
                };
            }),
            // 只列本次导出实际用到的素材（AssetById 是整个素材库）
            sources = timeline.Events.Select(e => e.AssetId).Distinct()
                .Where(timeline.AssetById.ContainsKey)
                .Select(id => timeline.AssetById[id])
                .Select(a => new { file = a.FileName, sha256 = a.Sha256 })
                .OrderBy(s => s.file, StringComparer.OrdinalIgnoreCase),
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }
}
