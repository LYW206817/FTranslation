# AGENTS.md — Fast Translation 的协作约定

本文件写给任何在本仓库里工作的 AI agent(包括未来的我)。先读它,再动手。

## 这是什么

Windows 托盘划词翻译工具:选中文本 → 浮动工具条(复制 / 翻译 / 截图翻译)→ 结果卡。
.NET 10 self-contained win-x64,WPF。翻译走阿里云百炼 DashScope(qwen-mt-flash / qwen-mt-plus / qwen-mt-image)。

- `src/FastTranslation.App` — WPF 外壳:单实例、托盘、主窗口、设置页、更新协调(`UpdateCoordinator`)、更新提示(`UpdateWindow`)
- `src/FastTranslation.Core` — 与 UI 无关的逻辑:设置、翻译抽象、`ReleaseVersion` / `UpdateManifest` / `UpdateChecker`
- `src/FastTranslation.Platform.Windows` — Win32:取词(UIA + 剪贴板)、浮层(`OverlayWindows.cs`)、托盘原生菜单、写回、`UpdateInstaller`、`InstallLayout`
- `src/FastTranslation.Providers.QwenMt` — DashScope 实现
- `tests/` — 三个 xunit 项目;`docs/IMPL.md` — 按版本号累加的实现记录(v1…v42);`docs/superpowers/specs/` — 设计规格
- `installer/` — Inno Setup 脚本与简中语言包;`tools/qa/` — 验证脚本;`publish/` — 构建产物与临时探针(**整个目录在 .gitignore 里**)

## GitHub 令牌:不要再问用户要 PAT

发布 Release、上传资产、改 `version.json` 全部用 **用户环境变量 `GITHUB RELEASE`**(注意变量名里有个空格,不是下划线):

```powershell
$tok = [Environment]::GetEnvironmentVariable('GITHUB RELEASE','User')   # ghp_ 开头,40 位
```

规则:

- 需要令牌时的标准做法是把它写进 `publish/.tok`(`[IO.File]::WriteAllText`),用 `curl -H "Authorization: Bearer $TOK"` 调用,**用完立即删除**,并 `grep -rl 'ghp_\|github_pat_' publish/` 自检。
- **永远不要**把令牌写进任何会被提交的文件、不要 `echo` 出来、不要贴在对话里、不要 `git remote` 里用(推送走 SSH `git@github.com:LYW206817/FTranslation.git`,不需要令牌)。
- 令牌缺失或过期时,不要反问用户「给我 PAT」,先自己重读一遍变量确认拼写(含空格),再检查是否 401/403;确实失效才说明「`GITHUB RELEASE` 返回 401,请在用户环境变量里更新它」。

## 常用命令

```bash
cd /d/FTranslation
dotnet build FastTranslation.slnx -c Release     # 必须 0 警告 0 错误(TreatWarningsAsErrors=true)
dotnet test                                      # 当前基线 75 + 83 + 40 = 198 全绿
```

## 提交与合并请求规范
- 遵循项目历史中使用的约定式提交规范，例如 `feat(领域模块): 新增固定域开关` 和 `fix(用户模块): 规范化性别展示`。
- 每次提交仅聚焦一项逻辑变更。
- 合并请求（PR）需包含：修改目的、受影响模块/路径、风险说明；界面改动需附上截图或动图。
- 关联相关问题/任务，并明确说明配置或数据迁移方面的影响。
- 提交说明必须使用中文。

### 输出规范

1. 输出文档位置:
   1.1. 设计文档: [.agents/design](.agents/design)，todo 跟在设计文档后面。
   1.2. 问题分析文档: [.agents/issue](.agents/issue)
   1.3. 需求文档: [.agents/requirement](.agents/requirement)
2. 相同需求文档，尽量写在一起（内容超过 500 行，可以分批写入），或者创建要给目录一起管理，不要随意平铺一堆不同版本的相同问题的文档。
3. 文件输出，使用正确的编码格式，例如UTF-8。
4. 除非用户指明，否则不要编写总结报告。
5. 输出文件命名，使用 `{需求日期}-{需求描述}-{输出类型}.{文件格式}` 的格式，例如 `2023-05-01-新增固定域开关-设计文档.md`。


## 改版本号的三处(必须同时改)

1. `Directory.Build.props` 的 `<Version>` → 决定程序集版本与「关于」显示
2. `installer/FastTranslation.iss` 的 `#define AppVersion`(第 7 行附近)
3. `docs/portable-readme.txt` 的标题、安装包名,并在「更新记录」顶部加一节

## 打包流水线

```bash
# 1) 自包含发布目录
dotnet publish src/FastTranslation.App/FastTranslation.App.csproj -c Release -r win-x64 --self-contained true -o publish/ft-folder

# 2) 安装包(约 70 秒;ISCC 不接受 /Q,别加)
"/c/Users/刘永威/AppData/Local/Programs/Inno Setup 6/ISCC.exe" installer/FastTranslation.iss
# → publish/dist/FastTranslation-<ver>-Setup.exe

# 3) 便携包
powershell -ExecutionPolicy Bypass -File tools/qa/make-portable.ps1
# → publish/dist/FastTranslation-<ver>-portable.zip(263 条目)
```

本机两个 Inno 编译器都可用,默认用 Inno 6 的 per-user 版:
`C:\Users\刘永威\AppData\Local\Programs\Inno Setup 6\ISCC.exe`(6.7.3)。
Inno 帮助是 `ISetup.chm`,已解包到 `publish/chm6`(读脚本 `publish/chmread.py`);类定义在 `topic_scriptclasses.htm`。

## 发布一个版本(完整顺序)

前置:主仓库 `LYW206817/FTranslation` 是**私有**的,对外只通过更新通道仓库 `LYW206817/FTranslation-Updates`(公开,含 GitHub Pages 的 `version.json`)流通。

1. 提交并推送代码;记下 `git rev-parse HEAD` 的**完整 40 位 sha**。
2. 遍历用户环境变量 `GITHUB RELEASE` 取令牌 → `publish/.tok`。
3. 主仓库建 Release:`POST https://api.github.com/repos/LYW206817/FTranslation/releases`,body 用 node 生成 JSON 文件再 `curl --data-binary @file`(PowerShell 拼 JSON 会被 `$`/`@` 吃掉)。`target_commitish` **必须传完整 40 位 sha**,短 sha 会被拒。
4. 上传两个资产到该 Release(Setup.exe + portable.zip):`POST https://uploads.github.com/repos/<owner>/<repo>/releases/<id>/assets?name=<file>`,大文件用 `run_in_background` + `job_output`。
5. 更新通道仓库建同名 tag 的 Release,上传**同一份 Setup.exe**。
6. 更新 `version.json`:`GET .../contents/version.json` 先取 `sha`(blob),`PUT` 时 **必须带上它**,否则 422/409。内容字段:`version` / `url`(指向通道资产的 `/releases/download/<tag>/<file>`)/ `sha256`(小写)/ `size` / `notes` / `releasePage`。
7. 校验:每个资产的 `state` 必须是 `uploaded`(只看到 `size` 是假成功),且 `digest` 与本地 hash 一致;然后轮询四个清单源确认新版本(`publish/poll-manifest.sh`)。
8. 在 `docs/IMPL.md` 追加「发布记录」小节,提交 `docs: record the vX release and its assets`。
9. 删掉 `publish/.tok` 与本次生成的 json。

外力事实(每次发布都要现测,网络环境会变):`github.com` 直连通常不可用;下载/清单要靠代理镜像。清单四顺位定义在 `src/FastTranslation.Core/UpdateChecker.cs:55`(Pages → gh-proxy raw → ghfast.top raw),下载候选在 `src/FastTranslation.Platform.Windows/UpdateInstaller.cs:31`(`ProxyPrefixes`,代理优先于直连)。GitHub Pages 有 600 秒缓存,jsdelivr 的 `@main` 缓存可能几小时。

## 自动更新的实现要点

- 检查时机:启动 30 秒后一次 + 每 6 小时;失败静默。托盘菜单与设置页可手动检查。
- 只有「安装记录里的目录」(`InstallLayout.ReadRegisteredDirectory()` 读 `HKCU/HKLM\...\Uninstall\{8B1F2C34-…}_is1` 的 `InstallLocation`)才算可自更新;便携包降级到下载页。
- 安装参数由 `UpdateInstaller.BuildInstallerArguments()` 生成,含 `/DIR=<当前目录>`,保证升级不改安装位置。
- 令牌、密钥、设置都在 `%LOCALAPPDATA%\FastTranslation\`(`settings.json`、`qwenmt-api-key.bin` 走 DPAPI、`update.log`),不进仓库、不随安装包走。

## 安装器(Inno Setup)已知边界

- `DisableDirPage=no` 是刻意的:默认值 `auto` 会在同 AppId 已安装时**自动隐藏**「选择目标位置」页。
- 外观:`WizardStyle=modern polar hidebevels`(只有 polar 给蓝色系按钮),配色常量在 `[Code]` 里,Inno 的 `TColor` 是 `$BBGGRR` 不是 RGB。
- `TNewButton` **没有 Color 成员**,`StyleElements := []` 也改不动按钮底色/文字色(主题自绘);只能改 `Font.Size` / `Font.Style`。
- `TPanel` / `TNewStaticText` 改色**必须先 `StyleElements := []`**,并且同时补 `Color := 背景色`(否则铺白块);装饰控件一律 `Parent := WizardForm`(挂 `MainPanel` 完全不渲染)。
- 布局要用 `ScaleX()/ScaleY()`;放大字号后必须 `AdjustHeight` 重排,否则标题压住下一行。
- 动效只有 `CreateCallback` + `SetTimer@user32.dll`(没有 TTimer);高对比度下要跳过动画。
- 美术资源由 `tools/qa/make-installer-art.py` 生成到 `assets/installer/`(需要 PIL)。

## 踩过的坑(省时间用)

- **改代码前先看 `docs/IMPL.md`**:同一个坑可能已经踩过并写在那里,按版本号搜。
- `docs/IMPL.md` 追加中文:不要用 bash heredoc(会报 `Unexpected character`),先用写文件工具落到 `publish/*.md` 再 `cat >> docs/IMPL.md`。
- PowerShell 5.1 读**无 BOM** 的 UTF-8 脚本 + 中文注释 → 按 GBK 解码报错;脚本一律存成带 BOM 的 UTF-8,或干脆 ASCII-only。输出中文前设 `[Console]::OutputEncoding = [Text.Encoding]::UTF8`。
- 脚本里 `Start-Process -Wait` 遇到静默安装会一直等:`[Run]` 会把应用拉起来;判完成要轮询 `ProductVersion`。
- 内联 `powershell -Command` 里的 `$_` 会被 shell 包装吞掉 → 先写 `.ps1` 再 `-File`。
- `Add-Type` 的 P/Invoke 结构体大小必须精确(x64 `INPUT` = 40 字节,多两个 `int pad` 会让 `SendInput` 返回 0);`Add-Type` 用 `-Namespace '' -Name 'X'`。
- UIA 在 Inno 向导里:`TNewButton` 不支持 InvokePattern,翻页只能 `SendKeys::SendWait('{ENTER}')`,且窗口要先 `SetForegroundWindow`;Inno 的文字元素 ControlType 都是 Pane。
- 托盘程序点 X 只隐藏窗口,`Restart Manager` 关不掉 → 安装器里用 `taskkill /f /im FastTranslation.exe`(走 `/UPDATEPID` 时跳过)。
- 旧 MSI(WiX,1.0.x)有「同版本改名重装 = 1603 且先删后拷」的陷阱;安装器里 `UninstallLegacyMsi()` 会在装新包前静默卸掉它。

## 代码约定

- `Nullable` / `ImplicitUsings` 开启,`TreatWarningsAsErrors=true`,**警告也要修掉**,不要 `#pragma` 绕过。
- 不引入 MVVM 框架、DI 容器、日志框架、自动更新框架(spec 明确禁止);需要新能力时写最小实现 + 单测。
- `InvariantGlobalization` **必须保持 false**:invariant 模式会破坏 WPF 绑定里的区域解析,`ComboBox` 模板抛 `XamlParseException` 静默失效。
- 界面配色只用 `ThemePalette`(浅色 `#F7F9FC` / `#FFFFFF` / `#2562FF`,深色 `#16191F` / `#1F242C` / `#4A82FF`),字体 `Segoe UI, Microsoft YaHei UI`。
- UI 改动要能拿截图当证据(见 `tools/qa/` 里的 `shot-*.ps1`),不要只说「已改好」。

## 完成的标准

一次改动算完成,需要同时满足:

1. `dotnet build` 0 警告 0 错误;`dotnet test` 全绿(新增行为带新测试)。
2. 界面/安装器改动有截图或像素证据,并说明取样位置。
3. `docs/IMPL.md` 有对应版本小节(写了为什么这样改、验证数据、残留风险)。
4. 变更已提交并 `git push origin master:main`;需要发布时,按上面的发布顺序做完并核对 `state: uploaded` 与 digest。
5. 临时文件(令牌、探针产物、生成的 json)清理干净,`git status --short` 里没有意外文件。
