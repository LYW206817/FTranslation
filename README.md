# Fast Translation

划词即翻译的 Windows 桌面工具。选中任意应用里的文字，浮出一条工具条（复制 / 翻译 / 截图翻译），点「翻译」在原位读结果，点「替换」或「插入」把译文写回原处。除此之外还有整份文档翻译、实时语音字幕、云同步的单词本与间隔复习。

基于阿里云百炼 DashScope 的千问模型（文本 `qwen-mt-flash`，截图 `qwen-mt-image-2.0` + OCR `qwen-vl-ocr`，语音识别 `qwen-audio-3.1-asr-flash-streaming`）。不登录也能一直用：填自己的 API Key 直连官方接口；登录账号后走免费/付费额度，翻译请求由本项目的服务端转发并按字符记账，额度用完自动静默回落到你自己的 Key，翻译不会中断。

<p align="center">
  <img src="assets/screenshots/main-light.png" width="46%" alt="主窗口 · 浅色">
  <img src="assets/screenshots/main-dark.png" width="46%" alt="主窗口 · 深色">
</p>

---

## 它做什么

- **划词取词**：用 UI Automation 读取选区的文字和屏幕矩形，Chromium 类应用（VSCode、Electron、浏览器）有 6 层节点探测 + 3 次重试的兜底
- **两段式交互**：选中文字先只弹工具条（复制 / 翻译 / 截图翻译），点了「翻译」才真正请求，不浪费额度
- **原位结果卡**：译文浮在原文附近，可以复制、替换原文、插入到原处、保存到单词本、切换目标语言、重试、关掉
- **写回**：替换或插入前做三重校验（窗口句柄 + UIA RuntimeId + 归一化后的选区文本），任何一项对不上就降级成「已复制」并提示，绝不写错地方
- **剪贴板兜底**：UIA 拿不到文字时，临时占用剪贴板发 Ctrl+C 取词，取完把剪贴板所有格式原样恢复
- **截图翻译**：按 `Ctrl+Alt+S`（或点工具条上的「截图翻译」）拖框选一块屏幕区域，图片翻译与 OCR 并行出结果——译图、识别到的原文和译文一起给你；适合网页、宽界面、PPT 这类稀疏排版
- **文档翻译**：把 `.docx` / `.txt` / `.md` 整份译成另一种语言，产出 `原文件名.en.docx` 这样的新文件，原文件一个字节都不动。文件在本机拆段，只有段落文字发往翻译接口；支持断点续译，中途断了再点开始会接着译
- **语音翻译**：按 `Ctrl+Alt+V` 开一场实时字幕——电脑里正在播放的声音（会议、视频、通话）被实时转成双语字幕，约一秒出稿。采集的是系统回环声音，不上传音频，只有识别出的文字出网；按秒计费（20 字符/秒），开始前显示「本月剩余可讲约 N 分钟」
- **单词本与复习**：翻译结果一键收进单词本，按单词 / 短语 / 语句自动归类，存在服务端、跨设备同步；复习页按「忘记 / 模糊 / 熟悉 / 记住」四档评分，下次出现时间走固定间隔阶梯（10 分钟到 56 天）
- **账号与额度**：邮箱注册即得每月 10,000 字符免费额度（每日 2,000），单账号最多绑 3 台设备；额度不够可以升级套餐（激活码开通），或让翻译自动回落到你自己的 Key
- **托盘常驻**：关窗口不退出；托盘菜单打开账号、单词本、设置，发起截图翻译、语音翻译，暂停取词、检查更新、退出
- **自动更新**：启动 30 秒后静默检查、之后每 6 小时一次，发现新版本一键下载安装并重启；下载有镜像回退和 SHA256 校验

## 环境要求

| 项 | 要求 |
|---|---|
| 系统 | Windows 10 1809 及以上 / Windows 11（x64） |
| 运行时 | 不需要，安装包自带 .NET 运行时（self-contained） |
| 账号 | 可选：注册账号领免费额度，或自备一个[阿里云百炼](https://bailian.console.aliyun.com/) API Key |

## 安装

从 [Releases](../../releases) 下载 `FastTranslation-1.5.0-Setup.exe` 双击安装。安装向导里可以改安装位置，默认装到当前用户目录（`%LOCALAPPDATA%\Programs\FastTranslation`），不需要管理员权限；装过 1.0.x MSI 版的不必先卸载，安装程序会自动清掉旧版本。

喜欢绿色版就下 `FastTranslation-1.5.0-portable.zip`，解压即用。便携版没有安装目录，因此不会自动安装更新，只会提示去下载页取新版。

> **当前构建未签名。** Windows SmartScreen 会拦一次，点「更多信息」→「仍要运行」。安装包请只从本仓库 Releases 获取。

卸载：设置 → 应用 → 「Fast Translation」→ 卸载。配置和 API Key 不会被删除，需要手动清理 `%LOCALAPPDATA%\FastTranslation`。

## 首次使用

1. 启动后托盘出现图标，弹出设置窗口
2. 选一种翻译额度，两种可以混用：
   - **注册账号**：在「账号」页用邮箱注册，免费额度到手就能翻，不用自己申请 API Key
   - **自己的 Key**：在设置里填入 API Key（用 DPAPI 加密后存在 `%LOCALAPPDATA%\FastTranslation\qwenmt-api-key.bin`），直连 DashScope
3. 选目标语言（默认「自动」，中英互译时按原文语种自动决定方向）
4. 保存，然后随便在哪个应用里选一段文字试试

默认热键：`Ctrl+Alt+T` 翻译剪贴板内容、`Ctrl+Alt+S` 截图翻译、`Ctrl+Alt+V` 语音翻译，都可以在设置里改。

## 隐私

- 没有遥测、崩溃上报、使用统计，不写任何文本日志
- **用自己的 Key 时**：待翻译文本只发往 `dashscope.aliyuncs.com`，直连官方接口
- **用账号额度时**：待翻译文本发往本项目的服务端，由它转发给 DashScope；服务器只按字符记账，不保存划词与译文
- 语音翻译不上传音频，出网的只有识别出的文字（经服务端中继转给识别接口）
- 单词本里你主动保存的词条存在服务端（用于跨设备同步与复习）；除此之外本地不落盘任何选中过的文本、剪贴板内容或按键记录
- 剪贴板兜底取词期间会短暂占用剪贴板，取完立刻按原格式恢复
- 密码框不取词；终端类应用只取词、不写回

## 已知限制

- 只支持 Windows x64，没有 ARM64 构建
- Chromium 系应用首次划词偶尔需要重试，程序内部已做 3 次探测
- 终端（Windows Terminal / cmd / PowerShell / conhost）只取词不写回，这是硬拒绝
- 安装包未做代码签名，企业环境可能被策略拦截
- 单词本免费版只读，保存词条需要轻享版及以上的订阅
- 语音翻译目前只做中英互译的自动语言对，需要登录账号并按秒消耗额度（剩余不足 1 分钟拒绝开始）
- 文档翻译只支持 `.docx` / `.txt` / `.md`，单文件 ≤10 MB、可译字符 ≤20 万、段落 ≤5,000；`.xlsx` / `.pptx` / `.pdf` 尚未支持。段落级回写会保留字体、字号、颜色与段落样式，但段落内部的局部加粗/超链接等 run 级格式会被合并成一种

## 从源码构建

需要 [.NET SDK 10.0](https://dotnet.microsoft.com/download)（预览版即可）和 Git。

```powershell
git clone <this-repo>
cd FTranslation
dotnet build FastTranslation.slnx -c Release
dotnet test FastTranslation.slnx -c Release
```

跑起来：

```powershell
dotnet run --project src/FastTranslation.App
```

本地起服务端（可选，账号、单词本与额度网关的联调用）：

```powershell
dotnet run --project server/FastTranslation.Server
```

打安装包（需要 [Inno Setup 6](https://jrsoftware.org/isdl.php)，脚本在 `installer/FastTranslation.iss`）：

```powershell
dotnet publish src/FastTranslation.App -c Release -r win-x64 --self-contained true -o publish/ft-folder
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer/FastTranslation.iss
powershell -File tools/qa/make-portable.ps1
```

## 项目结构

```
src/
  FastTranslation.Core/              领域模型、设置、语言目录、协调器（零 Win32 / WPF 引用）
  FastTranslation.Platform.Windows/  UIA 取词、写回、剪贴板、鼠标钩子、托盘、浮层窗口、主题调色板
  FastTranslation.Providers.QwenMt/  千问翻译 HTTP 客户端（直连与自建网关两套）
  FastTranslation.App/               WPF 外壳：主窗口十个页面（首页 / 单词 / 短语 / 语句 / 复习 / 文档 / 语音 / 账号 / 套餐 / 设置）、截图翻译与自动更新
server/
  FastTranslation.Server/            ASP.NET Core minimal API：账号、单词本、额度与订阅闸门、开发者网关（SQLite）
site/                                产品介绍静态单页（纯 HTML/CSS，无构建步骤）
deploy/                              一台 ECS 的部署文件（systemd + Caddy）
tests/
  FastTranslation.Core.Tests/              302 个测试
  FastTranslation.Platform.Windows.Tests/  97 个测试（热键解析、写回校验、UIA 行切片、ICO 解析、调色板）
  FastTranslation.Providers.QwenMt.Tests/  95 个测试（本地假 HTTP 服务）
  FastTranslation.Server.Tests/            210 个测试（WebApplicationFactory 起真服务 + 临时 SQLite）
tools/
  qa/        截图、内存浸泡、安装校验、E2E 联调等 PowerShell 脚本
  Probe*/    启动耗时与内存探针（历史决策依据）
docs/
  PLAN.md              技术决策 D1–D9 与实测数据
  IMPL.md              迭代史 v1–v81，每个版本解决了什么
  superpowers/specs/   设计规格
```

全部 704 个测试，`dotnet test` 全绿是交付门（本仓库没有 CI，结果以本机输出为准）。

架构上有一条硬约束：**Core 不引用 Win32 也不引用 WPF**，所有平台能力走 `Interfaces.cs` 里的接口，因此 Core 的测试可以在任何机器上跑。

设计说明见 `docs/superpowers/specs/2026-10-22-fast-translation-design.md`，迭代踩坑记录见 `docs/IMPL.md`。

## 许可

[GNU AGPL v3.0](LICENSE)（GNU Affero General Public License v3.0）。Copyright (C) 2026 FastTranslation Project。

AGPL 是强 copyleft：如果你修改本项目并把它作为网络服务提供给别的用户，需要按第 13 条把对应的完整源码也提供给那些用户；只是自己用、或原样分发二进制，不受额外影响。第三方依赖与参考项目见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

本项目和阿里云、阿里巴巴集团没有隶属关系，是个人项目。
