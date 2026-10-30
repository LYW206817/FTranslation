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
- `docs/IMPL.md` — 按版本号累加的实现记录(以文末最新的 `## vN` 小节为准);`docs/superpowers/specs/` — 大功能设计规格;`docs/playbooks/` — 低频操作的 runbook
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
dotnet test                                    # 4 个项目必须全绿,用例数与本机输出为准
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

## 写代码的习惯

### 注释:只写「为什么」

必须留注释的地方(核心逻辑与踩坑点):

1. **算法与不变量**:复习档位与下次间隔、额度记账公式、版本比较、命中测试与坐标换算——写出「为什么是这个顺序/公式」和边界条件。
2. **踩坑修复**:开头标版本号,一句现象、一句根因、一句现在的防法。例:
   `// v58: 冷启动首次翻译慢 5 秒——网关探测失败被当成永久失败,连接被拒不会重试。`
   已有先例:`Directory.Build.props` 的 `InvariantGlobalization` 注释、`ServerDatabase.cs` 的迁移注释、`UpdateInstaller.ProxyPrefixes` 的镜像取舍。
3. **平台与 API 边界**:Win32 结构体大小、UIA 节点探测层数、DPI 换算、剪贴板时序、`SendInput` 的 40 字节约束。
4. **并发与生命周期**:为什么跑在这条线程、锁保护什么、为什么要 `Dispatcher` 调度、`Freeze()` / `Dispose()` 归谁。
5. **刻意的怪写法**:故意的兜底、允许吞掉的异常(写清为什么可以吞、日志在哪)、降级路径。

不要写:

- 复述代码的注释(`// 把 x 设为 1`);
- 注释掉的旧代码(直接删;要留就进 `docs/IMPL.md`);
- 会过期的 TODO;必须临时留时写成 `// TODO(vN): 原因`,并在 IMPL 对应小节记一条;
- 与 `///` XML 文档重复的说明。

格式:

- **一律用中文**:注释(含 `///` XML 文档注释、XAML/XML 注释、脚本与配置里的注释、行尾注释)统一写中文;标识符、类型名、路径、URL、命令、版本号、错误码与代码片段保持原样,不要硬译。第三方文件(如 `installer/Languages/ChineseSimplified.isl`)不动。
- 公开 API 一句 `/// <summary>`;有单位、范围、失败语义时补 `<param>` / `<returns>` / `<exception>`。
- 注释紧贴被解释的语句上方;多行注释说整块逻辑时放在块的开头。

### C# 代码规范

首先就是代码实现一定要简洁,比如一个逻辑写了200行，但是能写50行就有同样的效果，那就重写，务必保证代码精简。

- 语言与工程:`LangVersion preview` / `Nullable` / `TreatWarningsAsErrors` 已开。依赖约束见下节「代码约定」,新能力先写最小实现 + 单测。
- 类型:类默认 `sealed`(仓库 82 处),只有明确要继承的才不封;不可变数据、API 契约、事件载荷用 `record` / `record struct`;小工具类型用 `internal static`。
- 命名:类型 / 方法 / 属性 PascalCase;私有实例字段 `_camelCase`;`static readonly` 与常量 PascalCase;局部变量与参数 camelCase;异步方法以 `Async` 结尾;布尔属性用 `Is` / `Has` / `Can` 开头;界面事件处理器 `Xxx_Click`;XAML `x:Name` 用 PascalCase。
- 空值:`null` 用模式匹配推进控制流(`is null` / `??` / `??=`),少用 `!`;公共入口用 `ArgumentNullException.ThrowIfNull` / `ArgumentException(…, nameof(x))`;不要 `catch` 后返默认值蒙混过关。
- 异步:库与服务端一律 `await` 到底,非 UI 代码加 `ConfigureAwait(false)`(仓库已有 118 处);UI 线程回调不加;禁止 `.Result` / `.Wait()`;`async void` 只允许事件处理器,且函数体内必须 `try/catch` 兜住,否则异常直接崩进程。
- 异常:只捕获能处理的;异常消息用英文、面向开发者;面向用户的错误文案在 UI 层转成中文;吞异常必须注释原因。
- 资源:`IDisposable` 用 `using` 声明;`CancellationToken` 放参数末尾并向下传;WPF 资源在 `Unloaded` / `Closing` 释放,事件订阅成对退订。
- 字符串与数字:比较用 `StringComparison.Ordinal` / `OrdinalIgnoreCase`(路径、协议、键名);数字与日期的格式化/解析传 `CultureInfo.InvariantCulture`;中文只出现在用户可见文案。
- 集合:返回 `IReadOnlyList<T>` / `IReadOnlyCollection<T>`,参数收 `IEnumerable<T>` 且循环外先 `Count()`;不要返回 `null` 集合,返回空集合。
- 文件与结构:文件名概括内容,相关小类型可以同文件(`OverlayWindows.cs` 就是);成员顺序 字段 → 构造 → 公共成员 → 私有方法;优先 guard clause 提前返回,嵌套不超过 3 层;单方法超过约 50 行就拆私有方法,文件超过约 700 行按职责拆页或拆文件。
- 魔法值:协议常量、超时、重试次数、限流窗口提成命名常量或 options,不要散落裸数字。
- 界面:颜色与字体只从 `ThemePalette` / `FtTheme` 取,不在 XAML 或代码里写裸色值;新控件沿用现有的圆角、内边距、虚线 token。
- 测试:命名用可读英文短语(仓库现状,如 `Login_sends_the_error_code_the_server_returned`);xunit `[Fact]` / `[Theory]`,一个测试一个主题;新增行为必须带测试。
- 禁止:`#pragma warning disable`(要抑制就用 `[SuppressMessage]` 加理由并说明)、注释掉的代码、无主的 `TODO`、跨层直接 `new` 依赖。

### 提交前整洁清单

- [ ] 没有注释掉的代码、没有失效的 `#if`
- [ ] 没有为过编译加的空实现、`!`、宽泛 `catch`
- [ ] 没有 `tmp` / `data2` / `foo` 这类临时命名
- [ ] 核心逻辑与本次修 bug 的位置都有「为什么」注释
- [ ] `dotnet build` 0 警告 0 错误,`dotnet test` 全绿

### 功能完成后自测启动

用户要求:每完成一个功能,直接启动应用自己走一遍,不要只跑测试就说「完成」。纯注释 / 纯文档 / 只改发版脚本的改动可以跳过。

```powershell
# 1) 先构建(测试通过后再启动,免得拉起旧二进制)
dotnet build FastTranslation.slnx -c Release

# 2) 单实例互斥锁叫 FastTranslation.SingleInstance:不关掉旧进程,新实例会直接退出,你验证的还是旧版本
Get-Process FastTranslation -ErrorAction SilentlyContinue | Stop-Process -Force

# 3) 启动刚构建的桌面端(托盘程序:窗口默认收在托盘,从托盘菜单打开主界面)
Start-Process 'src\FastTranslation.App\bin\Release\net10.0-windows\FastTranslation.exe'

# 服务端改动则后台起服务;Development 下缺 SigningKey 会用随机 key
# dotnet run --project server/FastTranslation.Server
```

- 启动后至少确认:进程 5 秒后仍存活、主窗口能打开、与本次改动相关的路径能走通;把启动命令和结果(截图或日志)写进验证记录。
- 验证用 Release 构建产物,不要用安装目录里上一版的 exe;只有验证安装包/升级链路时才用安装目录的。
- 验证完把进程关掉再跑打包或安装,避免文件占用导致构建/安装失败。

### 给我的注释回报

每次交付(包含小改动)在回复末尾附一小段,方便我直接 review:

```
注释:
- src/FastTranslation.Platform.Windows/UpdateInstaller.cs:31 — 为什么只保留这两个镜像前缀
- server/FastTranslation.Server/Gateway/GatewayEndpoints.cs:334 — 图片计费的固定折算
规范缺口:App.xaml.cs 的某个方法本次没拆,原因是 …(做不到就明说,别糊过去)
```

核心逻辑与踩坑点没有「为什么」注释,不算完成。

## 代码约定

- `Nullable` / `ImplicitUsings` 开启,`TreatWarningsAsErrors=true`,**警告也要修掉**,不要 `#pragma` 绕过。
- 桌面端不引 MVVM 框架、DI 容器、日志框架、自动更新框架(spec 明确禁止);需要新能力时写最小实现 + 单测。
- 服务端用 ASP.NET Core 内建的 DI / 日志 / 认证,不额外引框架;存储直接用 `Microsoft.Data.Sqlite`,不引 ORM;认证是手写 HS256,不引 JWT 库。
- `InvariantGlobalization` **必须保持 false**:invariant 模式会破坏 WPF 绑定里的区域解析,`ComboBox` 模板抛 `XamlParseException` 静默失效。
- 界面配色只用 `ThemePalette`(浅色 `#F7F9FC` / `#FFFFFF` / `#2562FF`,深色 `#16191F` / `#1F242C` / `#4A82FF`),字体 `Segoe UI, Microsoft YaHei UI`。
- 界面改动要能拿截图当证据(见 `tools/qa/` 里的 `shot-*.ps1`),不要只说「已改好」。

## 踩过的坑(省时间用)

- **改代码前先看 `docs/IMPL.md`**:同一个坑可能已经踩过并写在那里。文件已过 1300 行,按关键词 grep,再看最近几节,不要从头读。
- `docs/IMPL.md` 追加中文:不要用 bash heredoc(会报 `Unexpected character`),先用写文件工具落到 `publish/*.md` 再 `cat >> docs/IMPL.md`。
- PowerShell 5.1 读**无 BOM** 的 UTF-8 脚本 + 中文注释 → 按 GBK 解码报错;脚本一律存成带 BOM 的 UTF-8,或干脆 ASCII-only。输出中文前设 `[Console]::OutputEncoding = [Text.Encoding]::UTF8`。
- 内联 `powershell -Command` 里的 `$_` 会被 shell 包装吞掉 → 先写 `.ps1` 再 `-File`。
- `Add-Type` 的 P/Invoke 结构体大小必须精确(x64 `INPUT` = 40 字节,多两个 `int pad` 会让 `SendInput` 返回 0);`Add-Type` 用 `-Namespace '' -Name 'X'`。
- 安装器、便携包、发布过程中的坑(UIA 在 Inno 向导里、`taskkill`、旧 MSI 1603 等)见 `docs/playbooks/release-and-installer.md`。

## 完成的标准

一次改动算完成,需要同时满足:

1. `dotnet build` 0 警告 0 错误;`dotnet test` 全绿(新增行为带新测试)。
2. 桌面端界面 / 安装器改动有截图或像素证据,并说明取样位置;`site/` 改动同样给截图(改前改后各一张)。
3. 服务端改动:`tests/FastTranslation.Server.Tests` 必绿;不提交 `data/`、`*.db` 与生产配置。
4. `deploy/` 改动:所有 shell 脚本 `bash -n` 通过;Caddyfile 用 `caddy fmt` / `caddy adapt` 验证(装有 caddy 时)。
5. `docs/IMPL.md` 追加对应 `vN` 小节:为什么这样改、验证数据、残留风险。
6. 变更已提交并 `git push origin master:main`;需要发布时按 `docs/playbooks/release-and-installer.md` 走完,并核对 `state: uploaded` 与 digest。
7. 临时文件(令牌、探针产物、生成的 json)清理干净,`git status --short` 里没有意外文件。
8. 回复末尾按「给我的注释回报」附一段:本次新增的核心逻辑/踩坑注释,以及没做到规范的地方(明说原因)。
9. 完成一个功能后按「功能完成后自测启动」把应用真正拉起来走一遍(先杀掉旧进程,避免单实例互斥锁让新实例直接退出),启动结果写进验证记录;纯注释 / 纯文档 / 只改发版脚本的改动可跳过。

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
