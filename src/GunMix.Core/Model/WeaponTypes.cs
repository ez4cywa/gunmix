namespace GunMix.Core.Model;

/// <summary>武器类型：分类与合成模板（见 WeaponProfiles），不决定文件数量或原版触发规则。</summary>
public static class WeaponTypes
{
    public const string AssaultRifle = "突击步枪";
    public const string Smg = "冲锋枪";
    public const string MachineGun = "机枪";
    public const string Marksman = "精确射手步枪";
    public const string Pistol = "手枪 / 半自动";
    public const string Shotgun = "霰弹枪";
    public const string Sniper = "狙击 / 栓动";
    public const string Launcher = "发射器";
    public const string Custom = "自定义";

    public static readonly string[] All =
    [
        AssaultRifle, Smg, MachineGun, Marksman, Pistol, Shotgun, Sniper, Launcher, Custom,
    ];

    /// <summary>目录类别前缀（ar_mike4 → ar）对应的类型；这是素材目录自带的分类，比按名称猜测可靠。</summary>
    public static string? FromClassPrefix(string? prefix) => prefix?.ToLowerInvariant() switch
    {
        "ar" => AssaultRifle,
        "sm" => Smg,
        "lm" => MachineGun,
        "dm" => Marksman,
        "pi" => Pistol,
        "sh" => Shotgun,
        "sn" => Sniper,
        "la" => Launcher,
        _ => null,
    };

    /// <summary>由名称猜测类型（仅在命名可信时提示，可随时修改）。</summary>
    public static string GuessFromName(string weaponName)
    {
        var n = weaponName.ToLowerInvariant();
        var prefix = n.Length > 3 && n[2] == '_' ? FromClassPrefix(n[..2]) : null;
        if (prefix != null) return prefix;
        if (n.Contains("sniper") || n.Contains("bolt") || n.Contains("kar")) return Sniper;
        if (n.Contains("lmg") || n.Contains("mg") || n.Contains("m249") || n.Contains("pkp")) return MachineGun;
        if (n.Contains("smg") || n.Contains("mp5") || n.Contains("vector")) return Smg;
        if (n.Contains("pistol") || n.Contains("p92") || n.Contains("glock") || n.Contains("deagle")) return Pistol;
        if (n.Contains("shotgun") || n.Contains("sg_") || n.Contains("r870") || n.Contains("s12")) return Shotgun;
        if (n.Contains("rpg") || n.Contains("launcher") || n.Contains("grenade")) return Launcher;
        return AssaultRifle;
    }
}
