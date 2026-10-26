Fast Translation — 第三方声明
============================

本项目 v1.0.1 未包含任何第三方源代码。

依赖的运行时/库全部为官方 NuGet 包或其所属平台的组件,按各自许可使用:

| 组件 | 许可 | 用途 |
|---|---|---|
| .NET 10 / WPF (Microsoft.NETCore.App, Microsoft.WindowsDesktop.App) | MIT | 运行时与 UI 框架 |
| System.Security.Cryptography.ProtectedData | MIT | API Key 的 DPAPI 加密 |
| xunit / xunit.runner.visualstudio / Microsoft.NET.Test.Sdk | Apache-2.0 | 仅测试项目,不随产品分发 |
| WiX Toolset v4 | MSL (Microsoft Reciprocal License) | 仅构建期生成 MSI,不随产品分发 |

设计阶段参考了以下 MIT 项目的公开思路(Hook / UIA 取词 / 写回),
但未复制其代码,相关实现均为独立重写:

- STranslate (https://github.com/STranslate/STranslate) — MIT
- Text-Grab (https://github.com/TheJoeFin/Text-Grab) — MIT

以下项目仅作交互思路参考,GPL 系(未复制任何代码):

- Pot、Crow、CopyTranslator — GPL,只学思路

若后续引入任何第三方代码,必须在本文件补充其版权声明与许可原文。
