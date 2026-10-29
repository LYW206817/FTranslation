# AGENTS.md — Fast Translation 的协作约定

本文件写给任何在本仓库里工作的 AI agent(包括未来的我)。先读它,再动手。

发版、打包、安装器相关的操作细节在 `docs/playbooks/release-and-installer.md`;动这些部分之前必须先读那一份。

## 这是什么

Windows 桌面划词翻译工具 + 一个自建后端。选中文本 → 浮动工具条(复制 / 翻译 / 截图翻译)→ 结果卡;账号、单词本、免费额度与订阅走自己的服务端。

- 客户端:WPF,.NET 10 self-contained win-x64。翻译走阿里云百炼 DashScope(`qwen-mt-flash` / `qwen-mt-plus` / `qwen-mt-image`);免费额度由自建网关转发并记账,超额后客户端静默回落到用户自己的 Key。
- `src/FastTranslation.App` — WPF 外壳:单实例、托盘、主窗口、设置页、取词浮层与结果卡、账号(`AccountCoordinator`)、单词本(`NotesWindow`)、自动更新(`UpdateCoordinator` / `UpdateWindow`)
- `src/FastTranslation.Core` — 与 UI 无关的逻辑:设置、翻译抽象、`ReleaseVersion` / `UpdateManifest` / `UpdateChecker`
- `src/FastTranslation.Platform.Windows` — Win32:取词(UIA + 剪贴板)、浮层(`OverlayWindows.cs`)、托盘原生菜单、写回、`UpdateInstaller`、`InstallLayout`
- `src/FastTranslation.Providers.QwenMt` — DashScope 实现(直连与自建网关两种 provider)
- `server/FastTranslation.Server` — ASP.NET Core minimal API(net10.0):登录与账号、单词本、开发者网关(`/v1/chat/completions`、`/v1/image`)、额度与订阅闸门;SQLite(WAL);认证是手写 HS256,不引第三方 DI / 日志 / JWT 库
- `site/` — 产品介绍静态单页(纯 HTML/CSS,没有构建步骤)
- `deploy/` — 一台 ECS 的部署文件(systemd + Caddy + SQLite),用法见 `deploy/README.md`
- `tests/` — 四个 xunit 项目(Core / Platform.Windows / Providers.QwenMt / Server)
- `docs/IMPL.md` — 按版本号累加的实现记录(当前 v48);`docs/superpowers/specs/` — 大功能设计规格;`docs/playbooks/` — 低频操作的 runbook
- `.agents/` — 交付级设计 / 问题 / 需求文档(见「文档输出规范」)
- `installer/` — Inno Setup 脚本与简中语言包;`tools/qa/` — 本机验证脚本;`publish/` — 构建产物与临时探针(**整个目录在 .gitignore 里;里面被引用的脚本只存在于本机**)

## 密钥与令牌

### GitHub 发布令牌

发布 Release、上传资产、改 `version.json` 全部用用户环境变量 `GITHUB RELEASE`(变量名里有个空格,不是下划线):

```powershell
$tok = [Environment]::GetEnvironmentVariable('GITHUB RELEASE','User')   # ghp_ 开头,40 位
```

规则:

- 令牌只落到 `$env:TEMP` 下的临时文件,不要落在仓库目录里。调 GitHub 时用 `curl -H @headerfile`(把 `Authorization: Bearer <tok>` 写进该文件),不要让令牌出现在命令行参数里;用完立即删掉临时文件。
- **永远不要**把令牌写进任何会被提交的文件、不要 `echo` 出来、不要贴在对话里、不要 `git remote` 里用(推送走 SSH `git@github.com:LYW206817/FTranslation.git`,不需要令牌)。
- 用完自检:`git grep -n 'ghp_\|github_pat_'` 应无输出;`git status --short` 应无意外文件(仓库根与 `publish/` 都要查)。
- 令牌缺失或过期时,不要反问用户「给我 PAT」,先自己重读一遍变量确认拼写(含空格),再检查是否 401/403;确实失效才说明「`GITHUB RELEASE` 返回 401,请在用户环境变量里更新它」。

### 服务端密钥

- `Server:SigningKey`(非 Development 环境必须存在且 ≥32 字节,否则进程启动即失败)、`Server:AdminToken`、`Server:Gateway:ApiKey`、`Server:Smtp:Password` 只存在于服务器上的 `/opt/fasttranslation/appsettings.Production.json` 或环境变量。
- 仓库里只保留占位符 `deploy/appsettings.Production.json.example`。**不要在仓库里创建真的 `appsettings.Production.json`**;生产配置不进构建产物是刻意的。
- 客户端密钥:API Key 走 DPAPI 存在 `%LOCALAPPDATA%\FastTranslation\qwenmt-api-key.bin`,设置在同目录 `settings.json`,都不进仓库、不随安装包走。

## 常用命令

```bash
# 全部在仓库根执行
dotnet build FastTranslation.slnx -c Release   # 必须 0 警告 0 错误(TreatWarningsAsErrors=true)
dotnet test                                    # 当前基线 4 个项目 408 全绿(138 + 58 + 87 + 125)
dotnet test tests/FastTranslation.Server.Tests # 只跑服务端
dotnet run --project server/FastTranslation.Server   # 本地起服务;Development 下缺 SigningKey 会用随机 key
```

本仓库**没有 CI**:构建、测试、截图都必须在本机跑出来,并把输出贴进验证记录。

## 版本号:必须同时改的五处

1. `Directory.Build.props` 的 `<Version>` → 程序集版本与「关于」显示
2. `installer/FastTranslation.iss` 的 `#define AppVersion`
3. `docs/portable-readme.txt` 的标题、安装包名,并在「更新记录」顶部加一节
4. `README.md` 安装段的两处包名(最近就漏改过,停在 1.1.3)
5. `site/index.html` 的四处硬编码:版本徽标、hero 说明、两张安装卡的文件名

产品版本号与 `docs/IMPL.md` 里的 `vN` 是两套编号:`vN` 是变更序号,一直累加,不等于版本号。

## 自动更新的实现要点

- 检查时机:启动 30 秒后一次 + 每 6 小时;失败静默。托盘菜单与设置页可手动检查。
- 只有「安装记录里的目录」(`InstallLayout.ReadRegisteredDirectory()` 读 `HKCU/HKLM\...\Uninstall\{8B1F2C34-…}_is1` 的 `InstallLocation`)才算可自更新;便携包降级到下载页。
- 安装参数由 `UpdateInstaller.BuildInstallerArguments()` 生成,含 `/DIR=<当前目录>`,保证升级不改安装位置。
- 清单只有两个源:`UpdateChannel.ManifestUrls` = GitHub Pages(权威)与 gh-proxy 的 raw;下载候选 `UpdateInstaller.ProxyPrefixes` 只有 `gh-proxy.com`,失败后才直连。`ghfast.top` 实测连不上已移除,jsDelivr 因为会缓存 `@main` 发出过期清单已弃用——加源前先实测,别把死源加回来。
- GitHub Pages 有 600 秒缓存,轮询清单时要留出这个窗口。

## 代码约定

- `Nullable` / `ImplicitUsings` 开启,`TreatWarningsAsErrors=true`,**警告也要修掉**,不要 `#pragma` 绕过。
- 桌面端不引 MVVM 框架、DI 容器、日志框架、自动更新框架(spec 明确禁止);需要新能力时写最小实现 + 单测。
- 服务端用 ASP.NET Core 内建的 DI / 日志 / 认证,不额外引框架;存储直接用 `Microsoft.Data.Sqlite`,不引 ORM;认证是手写 HS256,不引 JWT 库。
- `InvariantGlobalization` **必须保持 false**:invariant 模式会破坏 WPF 绑定里的区域解析,`ComboBox` 模板抛 `XamlParseException` 静默失效。
- 界面配色只用 `ThemePalette`(浅色 `#F7F9FC` / `#FFFFFF` / `#2562FF`,深色 `#16191F` / `#1F242C` / `#4A82FF`),字体 `Segoe UI, Microsoft YaHei UI`。
- 界面改动要能拿截图当证据(见 `tools/qa/` 里的 `shot-*.ps1`),不要只说「已改好」。

## 踩过的坑(省时间用)

- **改代码前先看 `docs/IMPL.md`**:同一个坑可能已经踩过并写在那里。文件已近 900 行,按关键词 grep,再看最近几节,不要从头读。
- `docs/IMPL.md` 追加中文:不要用 bash heredoc(会报 `Unexpected character`),先用写文件工具落到 `publish/*.md` 再 `cat >> docs/IMPL.md`。
- PowerShell 5.1 读**无 BOM** 的 UTF-8 脚本 + 中文注释 → 按 GBK 解码报错;脚本一律存成带 BOM 的 UTF-8,或干脆 ASCII-only。输出中文前设 `[Console]::OutputEncoding = [Text.Encoding]::UTF8`。
- 内联 `powershell -Command` 里的 `$_` 会被 shell 包装吞掉 → 先写 `.ps1` 再 `-File`。
- `Add-Type` 的 P/Invoke 结构体大小必须精确(x64 `INPUT` = 40 字节,多两个 `int pad` 会让 `SendInput` 返回 0);`Add-Type` 用 `-Namespace '' -Name 'X'`。
- 安装器、便携包、发布过程中的坑(UIA 在 Inno 向导里、`taskkill`、旧 MSI 1603 等)见 `docs/playbooks/release-and-installer.md`。

## 完成的标准

一次改动算完成,需要同时满足:

1. `dotnet build` 0 警告 0 错误;`dotnet test` 408 全绿(新增行为带新测试)。
2. 桌面端界面 / 安装器改动有截图或像素证据,并说明取样位置;`site/` 改动同样给截图(改前改后各一张)。
3. 服务端改动:`tests/FastTranslation.Server.Tests` 必绿;不提交 `data/`、`*.db` 与生产配置。
4. `deploy/` 改动:所有 shell 脚本 `bash -n` 通过;Caddyfile 用 `caddy fmt` / `caddy adapt` 验证(装有 caddy 时)。
5. `docs/IMPL.md` 追加对应 `vN` 小节:为什么这样改、验证数据、残留风险。
6. 变更已提交并 `git push origin master:main`;需要发布时按 `docs/playbooks/release-and-installer.md` 走完,并核对 `state: uploaded` 与 digest。
7. 临时文件(令牌、探针产物、生成的 json)清理干净,`git status --short` 里没有意外文件。

## 文档输出规范

1. 设计文档放 `.agents/design`,实现计划放同目录的 `{日期}-{描述}-实现计划.md`,不要塞进设计文档正文。
2. 问题分析放 `.agents/issue`;需求文档放 `.agents/requirement`。
3. 大功能的方案讨论放 `docs/superpowers/specs/`;`specs` 写方案与取舍,`.agents/design` 写交付设计(含效果图与实现计划),两边不要重复写同一份内容。
4. 同一个需求的文档尽量写在一起(超过 500 行再分批),不要平铺一堆同主题不同版本的文档。
5. 文件名 `{需求日期}-{需求描述}-{输出类型}.{扩展名}`,例如 `2023-05-01-新增固定域开关-设计文档.md`。
6. 文本文件用 UTF-8;PowerShell 脚本按上面「踩坑」存成带 BOM 的 UTF-8。
7. 除非用户指明,不写独立的总结报告;`docs/IMPL.md` 是实现记录,属于必须写的那类。

## 提交与合并请求

- 提交说明用中文,遵循仓库历史的约定式提交;scope 用真实模块名:`app` / `core` / `platform` / `providers` / `server` / `site` / `deploy` / `installer` / `docs` / `qa`。例:`feat(server): 新增额度重置接口`、`fix(overlay): 结果卡在缩放屏上偏移`。
- 每次提交只聚焦一个逻辑变更。
- 合并请求写清:修改目的、受影响模块 / 路径、风险说明;界面改动附截图或动图;说明配置或数据迁移的影响。
- 推送分支:`git push origin master:main`。
