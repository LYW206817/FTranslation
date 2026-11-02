Fast Translation — 第三方声明
============================

本项目未包含任何第三方源代码。

依赖的运行时/库全部为官方 NuGet 包或其所属平台的组件,按各自许可使用:

| 组件 | 许可 | 用途 |
|---|---|---|
| .NET 10 / ASP.NET Core / WPF (Microsoft.NETCore.App, Microsoft.AspNetCore.App, Microsoft.WindowsDesktop.App) | MIT | 运行时与 UI / Web 框架 |
| System.Security.Cryptography.ProtectedData | MIT | API Key 的 DPAPI 加密 |
| Microsoft.Data.Sqlite | MIT | 服务端 SQLite 访问(底层 SQLite 为公有领域) |
| xunit / xunit.runner.visualstudio / Microsoft.NET.Test.Sdk / Microsoft.AspNetCore.Mvc.Testing | Apache-2.0 / MIT | 仅测试项目,不随产品分发 |

构建与分发使用:

- Inno Setup — 安装包编译器,Inno Setup License(可免费用于商业与非商业分发)
- `installer/Languages/ChineseSimplified.isl` — 简体中文语言包,来自 Inno Setup 社区翻译项目
  (https://github.com/kira-96/Inno-Setup-Chinese-Simplified-Translation),按其自身条款随 Inno Setup 使用

设计阶段参考了以下 MIT 项目的公开思路(Hook / UIA 取词 / 写回),
但未复制其代码,相关实现均为独立重写:

- STranslate (https://github.com/STranslate/STranslate) — MIT
- Text-Grab (https://github.com/TheJoeFin/Text-Grab) — MIT

以下项目仅作交互思路参考,GPL 系(未复制任何代码):

- Pot、Crow、CopyTranslator — GPL,只学思路

若后续引入任何第三方代码,必须在本文件补充其版权声明与许可原文。
