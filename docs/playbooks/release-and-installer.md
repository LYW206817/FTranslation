# 发版、打包与安装器

本文件是 `AGENTS.md` 的延伸,只在准备发布、改安装器、打便携包时读。

## 前置事实

- 主仓库 `LYW206817/FTranslation` 是**私有**的;对外只通过更新通道仓库 `LYW206817/FTranslation-Updates`(公开,含 GitHub Pages 的 `version.json`)流通。
- `github.com` 直连通常不可用,每次发版都要现测;下载与清单靠代理镜像。
- `publish/` 整个目录被 gitignore:里面被引用的脚本(`poll-manifest.sh`、`poll-manifest.ps1`、`chmread.py`)与 `publish/chm6` 都只存在于本机,换机器要重新准备。

## 打包流水线

```bash
# 1) 自包含发布目录
dotnet publish src/FastTranslation.App/FastTranslation.App.csproj -c Release -r win-x64 --self-contained true -o publish/ft-folder

# 2) 安装包(约 70 秒;ISCC 不接受 /Q,别加)
"$ISCC" installer/FastTranslation.iss
# → publish/dist/FastTranslation-<ver>-Setup.exe

# 3) 便携包
powershell -ExecutionPolicy Bypass -File tools/qa/make-portable.ps1
# → publish/dist/FastTranslation-<ver>-portable.zip(263 条目)
```

`$ISCC` 用本机 Inno 6 的 per-user 版(默认 `%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe`,当前 6.7.3)。换机器先 `where ISCC` 确认,不要把用户名硬编码进脚本。本机两个 Inno 编译器都可用,默认用 per-user 版。Inno 帮助是 `ISetup.chm`,已解包到 `publish/chm6`(读脚本 `publish/chmread.py`);类定义在 `topic_scriptclasses.htm`。

## 发布一个版本(完整顺序)

1. 提交并推送代码;记下 `git rev-parse HEAD` 的**完整 40 位 sha**。
2. 读用户环境变量 `GITHUB RELEASE` 取令牌 → 写到 `$env:TEMP` 的临时 header 文件(`Authorization: Bearer <tok>`),见「令牌操作」。
3. 主仓库建 Release:`POST https://api.github.com/repos/LYW206817/FTranslation/releases`,body 用 node 生成 JSON 文件再 `curl --data-binary @file`(PowerShell 拼 JSON 会被 `$`/`@` 吃掉)。`target_commitish` **必须传完整 40 位 sha**,短 sha 会被拒。
4. 上传两个资产(Setup.exe + portable.zip):`POST https://uploads.github.com/repos/<owner>/<repo>/releases/<id>/assets?name=<file>`,大文件用 `run_in_background` + `job_output`。
5. 更新通道仓库建同名 tag 的 Release,上传**同一份 Setup.exe**。
6. 更新 `version.json`:`GET .../contents/version.json` 先取 `sha`(blob),`PUT` 时**必须带上它**,否则 422/409。字段:`version` / `url`(指向通道资产的 `/releases/download/<tag>/<file>`)/ `sha256`(小写)/ `size` / `notes` / `releasePage`。
7. 校验:每个资产的 `state` 必须是 `uploaded`(只看到 `size` 是假成功),`digest` 与本地 hash 一致;再轮询清单源确认新版本(`publish/poll-manifest.sh`)。清单只有 Pages 与 gh-proxy 两个源,Pages 有 600 秒缓存。
8. 在 `docs/IMPL.md` 追加「发布记录」小节,提交 `docs: record the vX release and its assets`。
9. 删掉临时令牌文件与本次生成的 json。

## 令牌操作

```powershell
$tok = [Environment]::GetEnvironmentVariable('GITHUB RELEASE','User')
$hdr = Join-Path $env:TEMP ('ft-hdr-' + [guid]::NewGuid().ToString('N'))
[IO.File]::WriteAllText($hdr, "Authorization: Bearer $tok`nAccept: application/vnd.github+json`n")
```

```bash
curl -H @"$HDR" https://api.github.com/user
```

- 用 `-H @file` 让令牌不进命令行(进程参数列表对同机其他进程可见);curl 8.x 支持。
- 临时文件只放 `$env:TEMP`,不要放仓库目录;用完立即删除。
- 自检:`git grep -n 'ghp_\|github_pat_'` 应无输出,`git status --short` 应干净。

## 安装器(Inno Setup)已知边界

- `DisableDirPage=no` 是刻意的:默认值 `auto` 会在同 AppId 已安装时**自动隐藏**「选择目标位置」页。
- 外观:`WizardStyle=modern polar hidebevels`(只有 polar 给蓝色系按钮),配色常量在 `[Code]` 里,Inno 的 `TColor` 是 `$BBGGRR` 不是 RGB。
- `TNewButton` **没有 Color 成员**,`StyleElements := []` 也改不动按钮底色/文字色(主题自绘);只能改 `Font.Size` / `Font.Style`。
- `TPanel` / `TNewStaticText` 改色**必须先 `StyleElements := []`**,并且同时补 `Color := 背景色`(否则铺白块);装饰控件一律 `Parent := WizardForm`(挂 `MainPanel` 完全不渲染)。
- 布局要用 `ScaleX()/ScaleY()`;放大字号后必须 `AdjustHeight` 重排,否则标题压住下一行。
- 动效只有 `CreateCallback` + `SetTimer@user32.dll`(没有 TTimer);高对比度下要跳过动画。
- 美术资源由 `tools/qa/make-installer-art.py` 生成到 `assets/installer/`(需要 PIL)。

## 安装过程中的坑

- 脚本里 `Start-Process -Wait` 遇到静默安装会一直等:`[Run]` 会把应用拉起来;判完成要轮询 `ProductVersion`。
- UIA 在 Inno 向导里:`TNewButton` 不支持 InvokePattern,翻页只能 `SendKeys::SendWait('{ENTER}')`,且窗口要先 `SetForegroundWindow`;Inno 的文字元素 ControlType 都是 Pane。
- 托盘程序点 X 只隐藏窗口,`Restart Manager` 关不掉 → 安装器里用 `taskkill /f /im FastTranslation.exe`(走 `/UPDATEPID` 时跳过)。
- 旧 MSI(WiX,1.0.x)有「同版本改名重装 = 1603 且先删后拷」的陷阱;安装器里 `UninstallLegacyMsi()` 会在装新包前静默卸掉它。

## 发版前自检清单

- [ ] 五处版本号一致:`Directory.Build.props` / `installer/FastTranslation.iss` / `docs/portable-readme.txt` / `README.md` / `site/index.html`
- [ ] `dotnet build` 0 警告 0 错误;`dotnet test` 全绿(当前基线 702 个用例:Core 302 / Platform.Windows 97 / Providers.QwenMt 95 / Server 208;用例数会变,以本机输出为准,别拿旧数字当门)
- [ ] 安装包与便携包都在 `publish/dist/`,文件名含新版本
- [ ] 两个 Release 的资产 `state: uploaded`,digest 与本地 hash 一致
- [ ] `version.json` 已更新,两个清单源都能取到新版本
- [ ] `docs/IMPL.md` 有对应 `vN` 与「发布记录」
- [ ] 临时令牌文件与生成的 json 已删,`git status --short` 干净
