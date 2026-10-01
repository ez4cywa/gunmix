# 第三方声明

## CastAnimNoteDumper

本项目 `GunMix.Core/Cast/CastFile.cs` 的 CAST 读取逻辑参考自：

- 项目：CastAnimNoteDumper（https://github.com/QuentinWhisper/CastAnimNoteDumper）
- 版本：v2.0.0.0
- 许可证：MIT License
- 版权：Copyright (c) 2026 Pathfinder_FUFU

MIT License 全文见该项目仓库的 LICENSE 文件。

## DTZxPorter CAST

CastAnimNoteDumper 的内嵌读取器又改编自：

- 项目：DTZxPorter cast（https://github.com/dtzxporter/cast）
- 许可证：MIT License

## Data files, not code

`animations\*.cast`, `sounds\**\*.wav` and `sndbanks\json\weapon_rex_*.json` are
exported game assets. No such files are distributed with this repository. The
software reads them from paths you supply at runtime and never modifies them.

The CAST binary layout was understood from DTZxPorter's open-source CAST project
(MIT); `SoundBankIndex` reads the `{snd, alias, alias2}` JSON structure as-is, with
no third-party parsing library.

## 本项目的独立实现说明

`CastFile.cs` 是为 C# / .NET 10 重写的独立实现，只解析音效装配所需的最小信息（动画帧率、循环状态、通知名与关键帧），不编辑、转换或修复 `.cast` 文件。这样做的原因是把功能收在本项目内，用户不需要安装 Python 或下载外部可执行文件。

依据上述上游项目，不意味着它们背书本项目的用法、参数或结论。动画别名与音频文件的对应关系优先取 `sndbanks` 的原版配置实测，bank 无记录时才按名称启发式给出候选；不宣称任何结果是 MW4 原版还原。
