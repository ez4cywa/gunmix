# 枪声分层工作台 / GunMix

Windows 桌面工具：导入武器枪声素材，逐层试听调整（主体／机械／低频／修饰／空间），分别导出**单发**与**连发** WAV；也可把动画文件的音频通知装配成完整动画音效。

- 技术栈：C# / .NET 10 / WPF / NAudio
- 界面：中文，1440×900 起，1280×720 可用
- 授权：MIT，见 [LICENSE](LICENSE)

## 能做什么

| 功能 | 说明 |
| --- | --- |
| 素材导入 | 扫描目录，按文件名自动识别武器与分组；无法识别的文件不丢弃，可手动指定 |
| 分层编辑 | 每层独立开关、独听、增益（-60～+6 dB）、延时（0～1000 ms）；撤销／重做 |
| 变体策略 | 固定／顺序轮换／带种子随机（池内不连续重复，同种子可重现） |
| 试听 | 单层、单发、固定发数连发；A/B 配方对比；WASAPI 输出，设备拔出安全停止 |
| 导出 | 单发与连发作为**两个独立选项**分别输出；WAV + recipe JSON 双份 |
| 导出目标 | 可选「通用 WAV（48 kHz / 24 bit）」或「**Source 引擎 · Left 4 Dead 2 / Garry's Mod**（44.1 kHz / 16 bit + game_sounds 脚本）」 |
| 尾部处理 | 默认自动截掉尾部静音（阈值与保留尾长可调），截掉时长记入报告 |
| 动画音效 | 读取 `.cast` 的音频通知，按 soundbank 权威映射装配完整动画音效并导出 |
| 工程 | `.gunmix.json` 保存配方／素材哈希／事件清单；素材丢失可按哈希重定位 |

## 构建

需要 .NET 10 SDK：

```powershell
dotnet build GunMix.slnx
dotnet test tests/GunMix.Core.Tests/GunMix.Core.Tests.csproj
```

自包含发布（无需安装 .NET）：

```powershell
.\发布.cmd
```

安装包（需要 [Inno Setup 6](https://jrsoftware.org/isinfo.php)）：

```powershell
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer\setup.iss
# 产物：dist\GunMix_Setup_<版本>.exe
```

## 测试

大部分测试不依赖外部素材，直接跑即可。依赖真实游戏素材的测试（动画解析、soundbank 映射、多武器适配）在 `GUNMIX_ASSETS_ROOT` 未设置时**直接返回并通过**，不会失败：

```powershell
$env:GUNMIX_ASSETS_ROOT = "你的\游戏导出根目录"
dotnet test
```

期望的目录结构见 `tests/GunMix.Core.Tests/TestPaths.cs` 顶部注释。

## 运行时素材要求

本仓库**不包含**任何游戏素材、声音库或动画文件。软件在运行时从你指定的目录读取，并且只读不写：

- 声音目录（各武器的 `.wav`）
- 动画目录（`.cast`）
- soundbank 目录（`weapon_rex_*.json`，给出「别名→音频文件」的原版对应关系）

没有 soundbank 时动画音效仍可用，但别名只能靠名称推断，界面会明确标注来源，不会伪装成权威结果。

## 边界与免责

- 这是一个**制作工具**，不是还原工具。所有增益、射速、配平数值均为试混建议，不是原版参数。
- 原版声音配置未完整公开的部分（如某些别名引用的音频没有导出），界面与导出 JSON 都会如实标注为「素材缺失」，不会悄悄借用其他文件。
- 开火枪声由游戏逻辑触发，不由视图模型动画驱动，因此 `.cast` 的开火动画通常不含音频通知，属正常现象。

## 第三方

见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。CAST 读取为本项目内置 C# 实现，参考 CastAnimNoteDumper（MIT）与 DTZxPorter CAST 项目（MIT）。
