using GunMix.Core.Model;

namespace GunMix.Core.Synthesis;

public enum FireMode
{
    /// <summary>全自动：按住扳机按射速连续击发。</summary>
    Auto,

    /// <summary>半自动：每次扣扳机一发，“连发”表示快速点射节奏。</summary>
    Semi,

    /// <summary>栓动：每发之间需要拉栓，连发间隔须覆盖机械层的拉栓过程。</summary>
    Bolt,

    /// <summary>泵动。</summary>
    Pump,

    /// <summary>单发装填。</summary>
    Single,
}

/// <summary>
/// 按枪型的合成模板（试混建议，不是 MW4 原版参数）。
/// 层目标以“相对主体 SHOT 的前 250 ms RMS 差值 dB”表达：素材均已峰值归一化到约 0 dBFS，
/// 文件电平本身不携带原版混音比例，因此按实测响度配平，而不是给每把枪套同一组固定增益。
/// 基准取自 mike4 手册五层对照（-12/-18/-24/-24/-24 dB 作用于实测响度后的相对关系）。
/// </summary>
public sealed record WeaponProfile(
    string TypeName,
    FireMode FireMode,
    int Rpm,
    int BurstShots,
    double ShotTargetDbfs,
    double MechRelDb,
    double LowRelDb,
    double SwtRelDb,
    double AtmoRelDb,
    int AtmoVoiceLimit,
    string Notes)
{
    public double RelFor(string role) => role switch
    {
        LayerRoles.Mech => MechRelDb,
        LayerRoles.Low => LowRelDb,
        LayerRoles.Swt => SwtRelDb,
        LayerRoles.Atmo => AtmoRelDb,
        _ => 0,
    };

    public string FireModeLabel => FireMode switch
    {
        FireMode.Auto => "全自动",
        FireMode.Semi => "半自动",
        FireMode.Bolt => "栓动",
        FireMode.Pump => "泵动",
        _ => "单发装填",
    };

    // 界面分字段显示用
    public string BurstLabel => $"{Rpm} RPM × {BurstShots} 发";

    public string ShotTargetLabel => $"{ShotTargetDbfs:0.#} dBFS RMS";

    public string LayerTargetsLabel =>
        $"MECH {MechRelDb,3:+0;-0}  LOW  {LowRelDb,3:+0;-0}\nSWT  {SwtRelDb,3:+0;-0}  ATMO {AtmoRelDb,3:+0;-0}  dB";

    public string VoiceLimitLabel => AtmoVoiceLimit > 0 ? $"ATMO ≤ {AtmoVoiceLimit} 个实例" : "不限";

    public string Summary =>
        $"{FireModeLabel} · 连发默认 {Rpm} RPM × {BurstShots} 发 · 主体目标 {ShotTargetDbfs:0.#} dBFS(前250ms RMS) · " +
        $"MECH {MechRelDb:+0.#;-0.#} / LOW {LowRelDb:+0.#;-0.#} / SWT {SwtRelDb:+0.#;-0.#} / ATMO {AtmoRelDb:+0.#;-0.#} dB" +
        (AtmoVoiceLimit > 0 ? $" · ATMO 同时发声 ≤ {AtmoVoiceLimit}" : "");
}

public static class WeaponProfiles
{
    /// <summary>mike4 教学混音推导出的基准：SHOT -10.5 dB 素材 × -12 dB 增益 ≈ -22.5 dBFS。</summary>
    public const double ReferenceShotDbfs = -22.5;

    /// <summary>自适应配方的单发峰值上限；超过时所有层同量下调（保持层间关系）。</summary>
    public const double SinglePeakCeilingDbfs = -3.0;

    /// <summary>自适应配方的连发峰值上限。</summary>
    public const double BurstPeakCeilingDbfs = -1.0;

    private static readonly Dictionary<string, WeaponProfile> Profiles = new()
    {
        [WeaponTypes.AssaultRifle] = new(WeaponTypes.AssaultRifle, FireMode.Auto, 750, 10, ReferenceShotDbfs,
            -11, -9, -11, -13, 4,
            "全自动中口径：SWT 实测为低频冲击（重心约 150–200 Hz）；长连射限制 ATMO 同时发声数，避免空间层堆高。"),
        [WeaponTypes.Smg] = new(WeaponTypes.Smg, FireMode.Auto, 900, 12, -23.5,
            -10, -10, -11, -14, 3,
            "高射速小口径：机械层短（约 0.4 s）而清晰，略提 MECH；射速高，ATMO 限 3 个实例。"),
        [WeaponTypes.MachineGun] = new(WeaponTypes.MachineGun, FireMode.Auto, 650, 20, -22.0,
            -11, -8, -10, -14, 3,
            "持续火力：LFE 较长（约 1.1 s），低频略加；无 ATMO 素材时由 SHOT 自身尾部承担空间感。"),
        [WeaponTypes.Marksman] = new(WeaponTypes.Marksman, FireMode.Semi, 300, 5, -21.5,
            -10, -9, -12, -12, 0,
            "半自动大威力：SHOT 能量拖得长（95% 能量约 350 ms）；SWT 为宽频长尾（2.6–3 s）而非低频冲击，故低于主体更多。"),
        [WeaponTypes.Pistol] = new(WeaponTypes.Pistol, FireMode.Semi, 360, 6, -23.5,
            -10, -10, -11, -13, 0,
            "半自动手枪：SWT 为低频冲击；fire_plr_fcg（击锤/扳机高频点击）按实验分支保留，默认不启用。"),
        [WeaponTypes.Sniper] = new(WeaponTypes.Sniper, FireMode.Bolt, 45, 3, -20.5,
            -8, -8, -12, -11, 0,
            "栓动：MECH 长约 3.4 s 且低频占比高，包含拉栓过程，故提高 MECH 并把连发间隔放到约 1.3 s；SWT 为宽频长尾。"),
        [WeaponTypes.Shotgun] = new(WeaponTypes.Shotgun, FireMode.Pump, 70, 4, -21.0,
            -8, -8, -11, -12, 0,
            "泵动：每发间含上膛机械声，MECH 提高；本目录暂无霰弹枪素材，模板未经实测验证。"),
        [WeaponTypes.Launcher] = new(WeaponTypes.Launcher, FireMode.Single, 30, 2, -22.0,
            -10, -8, -11, -12, 0,
            "发射器：本目录暂无素材，模板未经实测验证。"),
    };

    public static WeaponProfile For(string? typeName) =>
        typeName != null && Profiles.TryGetValue(typeName, out var p)
            ? p
            : Profiles[WeaponTypes.AssaultRifle] with { TypeName = typeName ?? WeaponTypes.Custom, Notes = "自定义类型：沿用突击步枪模板，可手动调整。" };
}
