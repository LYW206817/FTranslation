# Fast Translation 实施日志 · v21-40

> 从 v21 起。全文索引与「新条目该写进哪一册」的规则见 [../IMPL.md](../IMPL.md)。

## v21 千问页兜底链路(2026-10-26 补录)✅

- **补录说明**:本次迭代当时未在本文档留小节(v20 直接跳到 v22)。以下内容依据源码中 3 处 `(v21 …)` 注释与 v22 正文的"v21 前"引用还原,时间窗口落在 2026-10-26 03:06–03:14 那批改动内
- **真问题**:千问页(Chromium/Tailwind)划词后,UIA `GetSelection().GetText()` 返回空、gate 把目标判为非文本、且 `CapturedSelection.Empty(anchor)` 把源窗口句柄一起丢掉 —— 三个缺陷叠加,使唯一能救场的 Ctrl+C 兜底恰好在那类页面上不可达
- **修复 1:兜底保留源窗口** —— `WindowsSelectionCapture.ReadSelectionAt` 失败路径改返 `CapturedSelection.Failed(point, processName, fallbackWindowHandle)`;UIA 读取成功但元素 windowless(Chromium 的 `NativeWindowHandle=0`)时,用 trigger 的 GA_ROOT 补位(`WindowsSelectionCapture.cs:168-185`)
- **修复 2:兜底前切回源窗口** —— 新增 `WindowsSelectionCapture.SwitchSourceToForeground(long windowHandle)`(:137,GA_ROOT 归一后 `SwitchToThisWindow`);`App.xaml.cs:325-327` 在 Ctrl+C 前调用它并 `Task.Delay(250)` 等前台稳定(用户点「复制」即明确意图,跨窗口切回是预期行为)
- **修复 3:gate 语义判定加深加宽** —— `UiaSelectionReader.LooksTextual` 由 4 层改走 6 层(Tailwind 页在命中元素与 Document 之间叠 4+ 层匿名 Group);并由单次探测改为 3 次重试、最后一次不 sleep(`UiaSelectionReader.cs:146-166`)。原注释:"a single miss killed every selection after the first"
- **修复 4:几何行重建** —— `UiaSelectionReader.RebuildFromVisibleLines`(:229)在 selection range 有 rect 无 text 时,拿 selection 矩形与 `GetVisibleRanges()` 逐行求交、按 x 覆盖率比例切分该行已知文本;`Width <= 4` 的 1-2px caret 矩形先被过滤,防产出垃圾单字(:183-190)
- **测试方法论** —— `tools/qa/qwen-e2e.ps1:103` 固化「先发 MOVE 再发 DOWN/UP」:SendInput 不带 `MOUSEEVENTF_MOVE` 时点击发生在当前光标位置
- 回归 30/30。本条与 v22 的「任务 1」是同一条排查链的前后两段:v21 打通链路,v22 复现验证并叠加 Bugbot 审查

## v22 千问页兼容 + Bugbot 审查修复(2026-10-26 夜,用户睡眠期间自主执行)✅

**任务 1:千问页"翻译/复制不成功"排查(真实注入测试复现→修复→验证)**
- **根因链**(逐步揪出):①千问页(Chromium)UIA `GetSelection().GetText()` 永远空(只有 caret 矩形);②v21 前 `CapturedSelection.Empty` 把窗口句柄一起丢掉 → Ctrl+C 剪贴板兜底在**唯一需要它的页面**不可达;③gate 的 LooksTextual 只走 4 层(Tailwind 页 Document 在 4 层边界外)+ 首次捕获后 Chromium FromPoint 抖动 → 后续划词全部 reject
- **修复**:①`CapturedSelection.Failed(anchor, proc, hwnd)` 保留源窗口;②兜底前 `SwitchSourceToForeground`(GA_ROOT,用户点复制=明确意图);③gate 走 6 层 + LooksTextual 3 次重试(最后一次不 sleep);④**行重建 RebuildFromVisibleLines**:selection rects 与 GetVisibleRanges 行求交、按比例裁剪(1-2px caret 矩形过滤,防产垃圾单字)
- **验证**:本地 HTML 矩阵页(tools/qa/ft-matrix.html,新 Edge 窗口+TOPMOST)+ matrix-e2e.ps1:**A 中文段/B 英文段/D 代码混合全 PASS**(UIA 直读+复制),E2 textarea 拖选→翻译 453ms→卡片 replace/insert 全链 PASS,C(user-select:none)行为可接受,F(input)由 E2 等价覆盖,Bing 真实网页兜底 captured 22ch/212ch,千问页手动实验 Ctrl+C 成功(拿到页面文本)
- **E2E 测试方法论教训**(全部踩过):SendInput DOWN/UP 不带 MOVE 时点击发生在**当前光标位置**;多显示器绝对坐标必须扣 SM_X/YVIRTUALSCREEN;UIA InvokePattern.Invoke 会把前台抢给 no-activate 窗口;激活页签要点 TabItem;SetWindowPos flags 0x0002=SWP_NOMOVE 恰好禁掉移动;PS1 无 BOM 中文 GBK 误读会破坏后续行 token 化;测试脚本每轮启动 app 会抢前台——先挪主窗口再把目标窗口 TOPMOST
- **遗留**:千问页真实划词最终确认需用户 1 分钟人工(页面 JS 弹 Edge 剪贴板权限框、动态布局,自动化不可靠)

**任务 2:Bugbot 风格全面审查(general-purpose 子代理读全部 33 源文件)→ 发现 7 P1 + 15 P2/P3,P1 全修+高价值 P2 全修**
- P1 修复:①WriteBack Replace/Insert 剪贴板恢复改 finally(abort 路径曾吞用户剪贴板);②剪贴板备份从纯文本改 **IDataObject 全格式快照/恢复**(图片/文件不再丢);③UIA re-read 与剪贴板轮询移出 UI 线程(Task.Run + Win32 剪贴板 API,点击后 UI 冻结 ~2s 消除);④热键路径解析真实前台进程名(空名曾绕过终端保护,Ctrl+C=SIGINT 隐患);⑤热键注册失败自动恢复原热键;⑥兜底捕获 anchor 保留(结果卡不再弹屏幕左上角,anchor=0 时用光标位置)
- P2 修复:启动热键 fallback 真注册;TryParse 支持 F1-F24/数字键("f5"曾解析成 F 字母);LooksTextual 最后一次 sleep 去掉;ElementNotAvailable 落入重试(曾直接放弃);托盘 TaskbarCreated 重挂(Explorer 崩溃后托盘消失);翻译失败时"复制"按钮禁用(错误文本不再可复制);line-rebuild 诊断插值加 FT_DEBUG 门
- 审查确认 sound:LL 钩子线程模型、FtComboBox 零 Binding 模板(键盘可达性完好)、FT_UITEST/FT_DEBUG 全部环境变量门控且不记录选中文本/密钥、DPAPI、单实例、QwenMtProvider 取消/重试、TranslationService 缓存
- 回归 30/30;全管线重发布重装;便携包同步

## v23 工程化补齐:CI 可测性 / A 类缺口 / 交付物(2026-10-26)✅

**背景**:v22 结束时项目处于「可自用、不可公开」中间态 —— 有 MSI 但无版本库、无平台层测试、spec 承诺的设置项缺三项、README 只写一句 v1.0.0。本轮把这三块补齐。

- **版本库**:`git init` + `.gitignore`([Bb]in/、[Oo]bj/、publish/、assets/qa/、*.bin、settings.json、install.log、*.wixpdb),基线提交 `a10fd00`(133 文件)。提交前全仓扫密钥(正则 `sk-[a-z0-9]{16,}`、`api[_-]?key\s*=\s*"[^"]{8,}"`、`Bearer [A-Za-z0-9]{20,}`)→ 0 命中;唯一疑似 `tools/E2EKeySmoke/Program.cs:37` 的 `sk-invalid-key-for-negative-test` 是负向测试假 Key,真 Key 走环境变量 `QIANWEN_API_KEY`。旧交接文档「不自行 git」红线已被用户明确指令覆盖
- **flaky test 根治**:`QwenMtProviderTests.FakeQwenServer` 原 `var port = 18500 + new Random().Next(2000)` 单次绑定,撞端口即 `HttpListenerException: 另一个程序正在使用此文件`。改为 20 次重试(`Prefixes.Clear()` → `Add` → `Start`),全失败才抛 `InvalidOperationException`,并暴露 `ListenerException` 供诊断。连跑 4 次 6/6
- **新测试项目 `tests/FastTranslation.Platform.Windows.Tests`**(此前空目录且不在 slnx):net10.0-windows + UseWPF,xunit;**36 例**。为可测性抽出 4 个 `internal` 纯函数(行为不变,靠 `InternalsVisibleTo` 开放):`HotkeyService.TryParse`、`WriteBackService.RuntimeIdEquals`、`UiaSelectionReader.SliceLine`(行切片的 40% yOverlap 阈值与比例裁剪)、`TrayIcon.PickIcoEntry`(ICO 目录选择与越界拒绝)
- **抽出即抓到一个真 bug**:`ParseNamedKey` 的 `d1..d0` 分支原为 `part[1] == '0' ? '9' : (char)(part[1] - 1)`,把 `Ctrl+D1` 映射成数字 0 键。VK_D0..VK_D9 就是 ASCII 数字键,已改为直接返回 `part[1]`
- **A 类缺口补齐**:①排除应用(列表 + 填入前台应用 + 添加 + 移除选中,`WindowsSelectionCapture` 新增 `isExcluded` 钩子在 UIA gate **之前**就拒绝,省掉昂贵探测);②开机启动(`StartupRegistration`,`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`,无管理员、无服务、无可执行文件改动;失败时不假装勾选生效,关闭保存并提示);③关于(信息版本 + 文件写入日期 + 未签名提示)
- **设置窗口纵向溢出(真 bug,175% 缩放的 1440×960 屏才暴露)**:内容加到 1435px 高,`保存/取消` 掉出屏幕。`MaxHeight=820`、`Height=720`、`SizeToContent+MaxHeight` 全无效 —— **WPF 的 `SizeToContent` 完全不受 `MaxHeight` 约束**(测量阶段无界)。最终:去掉 `SizeToContent`,内容区包 `ScrollViewer`(Vertical Auto / Horizontal Disabled),构造函数按 `SystemParameters.WorkArea.Height - 80` 与 720 取小、下限 360。实测窗口 875×1260px @y=168,两按钮 inWindow=True
- **交付物重出**:补 `tools/qa/make-portable.ps1`(`ZipFile.CreateFromDirectory` + 3 次重试;旧的 `Compress-Archive` 在 `viceProcess`/本地化资源上抛 PermissionDenied);MSI 产品名由 `FastTranslation` 改为 `Fast Translation`(卸载列表里与快捷方式名一致);`publish/dist/README.txt` 重写(安装/首次使用/日常/卸载/隐私/已知限制/四段更新记录)
- **验证**:`dotnet build` 0 警告 0 错误;`dotnet test` 24 + 36 + 6 = **66/66**;MSI 装后 250 文件、exe SHA256 与构建产物一致、桌面与开始菜单快捷方式存在、卸载项为 `Fast Translation`;便携包 263 条目 61.6MB
- **内存 5 分钟浸泡**(新增 `tools/qa/mem-soak.ps1`,每 15s 采 `Win32_PerfRawData_PerfProc_Process`):装在 `%LOCALAPPDATA%` 的 MSI 版 PrivateWS 全程 **37.6–38.6MB**(t=16s 37.9 → t=307s 38.6,handle 556→586),WS 120.4–125.4MB,红线 50MB 有余量。注意 `Start-Process` 后立刻单点采样的 50.8MB 是 GC 未收敛的启动瞬时值,不是稳态 —— 判断红线必须看浸泡曲线
- **UIA 脚本化交互验证**(新增 `tools/qa/` 下四支):`settings-verify.ps1` 校验窗口内按钮/关于文本/截图;`settings-dump.ps1` 全后代元素转储;`settings-interact.ps1` **点击级**验证排除应用增删 + 开机启动开关(读写 `HKCU Run` 与 `settings.json` 双向核对,结束时备份还原);`verify-install.ps1` 校验安装态。脚本必须 ASCII-only(中文用 `[char]0xXXXX` 拼),否则无 BOM 的 PS1 在 GBK 下乱码

## v24 开源前补齐:主题跟随系统 / 许可证 / 根 README(2026-10-26)✅

**背景**:决定推 GitHub 私有仓库前先把「对外可见」的三件事补齐 —— LICENSE、根 README、主题跟随系统(spec §3.1 承诺但代码里 `Theme` 字段一直没人读)。

- **许可证**:新增 `LICENSE`(MIT,FastTranslation Project)与 `THIRD_PARTY_NOTICES.md`。后者声明 v1.0.0 不含任何第三方源代码;构建期依赖 .NET 10/WPF(MIT)、`ProtectedData`(MIT)、WiX v4(MSL,仅构建)、xunit 系(Apache-2.0,仅测试);参考过的 STranslate / Text-Grab(MIT)只借鉴思路未复制代码,Pot / Crow / CopyTranslator(GPL)同样只学思路 —— 对应 spec §19.3 的 license 红线
- **根 README**:替换掉原先只有一句 v1.0.0 的占位内容,写成 118 行完整文档 —— 功能介绍、环境要求、安装(MSI/便携/未签名提示/卸载)、首次使用、隐私(只出网到 dashscope.aliyuncs.com、零遥测、不落盘文本)、已知限制、从源码构建(含 WiX 打包命令链)、项目结构、架构硬约束(Core 零 Win32/WPF 引用)、许可指引。配图放 `assets/screenshots/`(浅/深 × 主窗/设置 四张),因为 `assets/qa/` 在 .gitignore 内
- **主题方案(选 A:启动时定调色板,不做实时换肤)**:spec 只要求「跟随系统深浅色」,启动时读一次注册表就能拿到完全一致的可见结果;实时切换要把 94 处 `StaticResource` 全改 `DynamicResource` + 调色板可变 + 窗口重建,代价是整轮 UI 回归。故 `ThemeMode`/`AppTheme` 一律不做运行时热切换,设置页明确标注「下次启动生效」
- **新增 `ThemePalette`**(src/FastTranslation.Platform.Windows/ThemePalette.cs):`AppTheme { Light, Dark }`;`ThemeColors` record 17 个 hex 令牌(Brand/BrandHover/BrandPressed/BrandFill/BrandFillHover/Accent/WindowBg/Surface/Hair/HairStrong/BorderStrong/Hover/TextPrimary/TextSecondary/TextMuted/Danger/Notice);`Resolve(setting, systemPrefersDark)` 认 `system`/`light`/`dark`(trim + 小写,null/未知回落系统偏好);`SystemPrefersDark()` 读 `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\AppsUseLightTheme`(缺失/异常按浅色,即最保守)。深色主色 #4A82FF、窗底 #16191F、Surface #1F242C、正文 #E8ECF3
- **新增 `ThemeBridge`**(src/FastTranslation.App/ThemeBridge.cs):`Apply(theme)` 先 `ThemePalette.Use` 再把 App.xaml 里 15 个 brush 的 `Color` **就地改**。**不能换字典条目** —— 隐式 Style/ControlTemplate 解析时已把 StaticResource 解析成同一个 brush 实例,替换 key 指向的对象对已解析模板无效
- **真根因教训(花了三轮截图才定位)**:只改 brush 颜色后,深色下**所有输入框/文本框底色仍然是 #FFFFFF**。因为 App.xaml 的模板里用的是 `{StaticResource …Brush}`,解析即固定。最终把 4 个 XAML 的 brush 引用批量改 `{DynamicResource …}`(App.xaml 52、MainWindow 10、SettingsWindow 11、OnboardingWindow 10,共 83 处),同时给 15 个 SolidColorBrush 加 `po:Freeze="False"`(冻结的 brush 不能改色,`po` 命名空间=`http://schemas.microsoft.com/winfx/2006/xaml/presentation/options`)。改完深色实测:窗底 #16191F / 输入框 #1F242C / 边框 #3A424F / 主按钮 #4A82FF / 文字 #E8ECF3;浅色 #F7F9FC / #FFFFFF / #CFD9EA / #2562FF
- **浮层窗口同步**:overlay 是纯代码构造,吃不到 XAML 资源。`OverlayWindows` 里的 `FtTheme` 由 16 个 `readonly SolidColorBrush` 改成属性 `=> Freeze(ThemePalette.Active.X)`,Freeze 用带锁的 `Dictionary<string, SolidColorBrush>` 缓存复用(每帧新建 brush 会拖慢浮层)
- **顺手清掉剩余硬编码色**:禁用按钮态 #A9C0F2/#E9EEFB → `HairStrongBrush`/`TextMutedBrush`;两处 `SelectionBrush` #BBD2FF → `BrandFillBrush`。仍保留硬编码的三处是有意的:#402562FF 焦点环(品牌色半透明)、#1B2536 DropShadowEffect、MainWindow 的 BoltRule 品牌渐变
- **设置页**:加「主题」`FtComboBox`(跟随系统/浅色/深色)+ 一行「下次启动生效」提示;读写 `AppSettings.Theme`
- **测试**:新增 `ThemePaletteTests`(12 例):9 组 `Resolve` 的 InlineData(含 `"  DARK  "`、null、`""`、乱字符串回落系统偏好)、`Active` 跟随 `Current`、两套调色板 17 项全非空且以 # 开头、深色必须在 WindowBg/Surface/TextPrimary 上与浅色不同。平台层测试 36 → **48**
- **截图验证陷阱(重要,曾误判成 bug)**:屏幕 1440×960,主窗 980×945 居中后 rect=(770,326),底部 311px 在屏幕外,`CopyFromScreen` 抓到的桌面空白 #FFFFFF 会被当成「深色没生效」。新增 `tools/qa/shot-main-theme.ps1` 先 `SetWindowPos(hwnd, 0, 0,0,0,0, SWP_NOSIZE|SWP_NOZORDER)` 把窗口挪到 (0,0) 再抓
- **新增 QA 脚本**(tools/qa/):`shot-theme.ps1`(注入 Theme → 重启 → UIA 抓主窗与设置窗 → finally 还原 settings.json)、`shot-main-theme.ps1`、`hist-all.ps1`(3px 网格全图直方图 top10)、`row-hist.ps1`/`row-probe.ps1`/`col-run.ps1`(按行/列的近白与深色像素统计,用来定位「哪一行为什么还是白的」)、`pixel-probe.ps1`/`pt-probe.ps1`、`dump-windows.ps1`、`screen-info.ps1`
- **验证**:`dotnet build` 0 警告 0 错误;`dotnet test` 24 + 48 + 6 = **78/78**

## v25 打包补卫星资源 + 推送 GitHub 私有仓库(2026-10-26)✅

**背景**:v24 之后重打 MSI 发现安装态与发布目录文件数不一致,顺带修掉一个从 v1 起就存在的打包缺陷;随后把仓库推到 GitHub 私有仓库。

- **真 bug:`gen-files-wxs.ps1` 漏子目录**:首次对比得 `inst count=250` vs `src count=262`,差 12 个全是 `zh-Hans\*.resources.dll`(PresentationCore/PresentationFramework/PresentationUI/ReachFramework/System.Windows.Controls.Ribbon/System.Windows.Input.Manipulations/System.Xaml/UIAutomationClient/UIAutomationClientSideProviders/UIAutomationProvider/UIAutomationTypes/WindowsBase)。根因是 `Get-ChildItem -LiteralPath $dir -File` **没带 `-Recurse`** —— 而 `Directory.Build.props` 的 `SatelliteResourceLanguages zh-Hans` 必然产出这个子目录,所以 v1–v24 所有 MSI 都缺这 12 个文件(程序仍能跑,因为缺的是非必要语言资源,但安装不完整)
- **两次失败修法**(值得记):① 想在 `<Fragment>` 下用 `<StandardDirectory Id="INSTALLFOLDER">` → `error WIX0021: The StandardDirectory/@Id attribute's value, 'INSTALLFOLDER', is not one of the legal options`(`StandardDirectory` 只认 TARGETDIR、LocalAppDataFolder 这类系统文件夹 Id);② `$id = 'c' + $i` 里的 TS 模板字符串把 `$i` 提前插值成 `ReferenceError: id is not defined`。最终用 `<DirectoryRef Id="INSTALLFOLDER">`(INSTALLFOLDER 由 `installer/Product.wxs:18-22` 的 LocalAppDataFolder → Programs → FastTranslation 定义)+ 递归函数 `Emit-Tree` 按相对路径深度生成 `<Directory Id="dir0" Name="zh-Hans">`,子目录下的文件用 `$subDirs[$rel]` 指到生成的 Id
- **结果**:`files.wxs generated: 262 files (250 root + 12 in 1 subdirs)`,`grep -c '<Component ' = 262`;MSI 52928512 → **53091453** 字节;新增 `tools/qa/diff-install.ps1` 对比安装目录与 `publish/ft-folder` 的相对文件集 + exe SHA256,得到 `inst count=262 src count=262`、两侧独有均为空、exe 哈希同为 `ABE61D9F1A94AC545F80AF766735712C16DBE5174999EB39C83D560CDBAEFA33`
- **纠正一个旧认知(ARP 位置)**:卸载项**不在 HKCU**。install.log 里 `ALLUSERS|MSIINSTALLPERUSER` 命中 0 条(Product.wxs 只写 `Scope="perUser"`),ARP 实际写在 `HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{ProductCode}`,DisplayName `Fast Translation`;`HKCU\Software\Microsoft\Installer\Products\5BE9AB6619DFC44419348E0327A6F728` 是 per-user MSI 注册。`verify-install.ps1` 之前只查 HKCU Uninstall 子键所以一直报 false,现已重写:installed files=262、exe hash matches build=True、启动菜单与桌面快捷方式均在、ARP entry present=True、satellite resources installed=12
- **内存复测(干净启动)**:`tools/qa/mem-soak.ps1 -Minutes 5` 全程 PrivateWS **35.9–39.6MB**(t=17s 38.1 → t=231s 39.6 → t=310s 37.0 回落),WS 120.8–127.2MB,handles 556→584。红线 50MB 有余量;首轮浸泡出现过 47→63→57MB 的爬升,是安装后第一次启动叠加托盘/浮层句柄预热,重启后再测即回到 38MB 区间 —— 结论是**浸泡必须跑在干净启动的实例上**,单点采样或复用旧实例都会误判
- **便携包重打**:`tools/qa/make-portable.ps1` → 263 条目(262 产物 + `dist/README.txt`)、61616504 字节。注:`Compress-Archive` 在本地化资源文件名上会抛 `PermissionDenied`,故脚本用 `ZipFile.CreateFromDirectory` + 3 次重试
- **推送 GitHub 私有仓库**(`github.com/LYW206817/FTranslation`):远端只有作者用网页初始化时留下的一个占位提交(Apache-2.0 LICENSE + 两行 README),与本地历史无共同祖先 —— 经确认**覆盖远端**,以本地 5 个提交为准(`git push -u origin master:main --force`),远端 170 个文件、README 为 118 行完整版、LICENSE 为 MIT
- **提交身份改 GitHub noreply**:原 `user.email` 是 Gitee 的 no-reply 地址,GitHub 不认(贡献图不归属)。用 `git filter-branch --env-filter` 把 5 个提交的 author/committer 统一改成 `LYW206817 <210836236+LYW206817@users.noreply.github.com>`(未推送前重写,无副作用);并新增 `.gitattributes`(`* text=auto` + png/jpg/ico/zip/msi/wixpdb 标 binary)消除批量 LF→CRLF 警告
- **连接方式踩坑**:本机 `https://github.com` **TCP 443 连不通**(api.github.com 可通、github.com 的 443 连接被 reset),但 SSH 22 通畅、`~/.ssh/id_rsa` 已授权该账号 ⇒ remote 用 `git@github.com:LYW206817/FTranslation.git`,并写入仓库级 `core.sshCommand`(指定 key 与 known_hosts 绝对路径,因为 MSYS2 的 `$HOME` 落在中文用户名下(如 `/c/Users/示例用户`),中文路径在 ssh 里会乱码)

## v26 设置页布局重做(2026-10-26)✅

**背景**:用户反馈「设置页上下滑动卡卡的,中间上下限隔断 UI 怪怪的」。截图实测(窗口 875×1260px @1.75 DPI)暴露五个具体缺陷:①顶部「跟随系统」下拉被视口上边缘切掉一半;②底部「开机时自动启动」复选框被切一半,保存/取消浮在它上面;③排除应用按钮行横向溢出,「移除选中」被窗口右边裁掉;④空的排除应用列表是个难看的空盒子;⑤原生 WPF 滚动条与自定义设计令牌格格不入。

- **布局重做**(src/FastTranslation.App/SettingsWindow.xaml 88 → ~215 行):改成三行网格 —— **固定页头**(17px 标题 + Hair 下边线)/ **ScrollViewer 正文** / **固定页脚**(Hair 上边线 + 取消/保存)。正文从「一长串 FieldLabel + 控件」改成 5 张卡片分组:翻译与外观 / 取词与写回 / 服务 / 排除应用 / 系统。卡片 = Surface 底 + Hair 边 + CornerRadius 10 + Padding 14,13,14,14 + Margin 0,0,0,14;分组标题是 11px SemiBold TextMuted 的 GroupLabel。**页头页脚的 hairline 就是「上下限隔断」** —— 控件再也不会被视口边缘切成一半,滚动时切的是卡片而不是输入框
- **两列网格**:目标语言|模型、主题|划词热键 各占半宽(列宽 `*` / 12 / `*`)。半宽放不下 `qwen-mt-flash(快速,默认)`,模型项文案缩成 `Flash(快速,默认)`/`Plus(更高质量)`(模型名仍是 qwen-mt-flash/qwen-mt-plus,代码里没动)
- **排除应用卡片**:外框 `Border`(WindowBg 底 + Hair 边 + Padding 3)里叠 `TextBlock x:Name="ExclusionEmpty"`(`IsHitTestVisible="False"`)与 `ListBox`,空表时显示「还没有排除任何应用」而不是空盒子;动作按钮由一行三个挤爆改成两行 —— `[输入框 *][添加]` + `[填入前台应用][移除选中]`,两者都稳稳在窗口内;列表项自制 `ExclusionItem` 模板(Hover=Hover、Selected=BrandFill+Brand)
- **嵌套滚动卡顿(真根因之一)**:鼠标移到排除应用列表上滚轮时,内层 `ListBox` 吃掉滚轮,页面纹丝不动 —— 用户感觉就是「卡」。新增 `ExcludedList_PreviewMouseWheel`:内层已到顶/到底或本就不能滚时 `e.Handled = true`(此方案只覆盖了列表,`KeyBox` 等输入框仍吞滚轮 —— 见 v28) 并把 `Body` 按 `e.Delta / 120.0 * 48` 步进,Clamp 到 `0..ScrollableHeight`(配 `FindScrollViewer` 递归 `VisualTreeHelper`)
## v28 设置页滚轮被输入框吃掉(v26 修复不完整)(2026-10-26)✅

**背景**:v26 之后用户仍反馈「往上滑还是会被阻断,得等一会儿才好」。v26 只给排除应用 `ListBox` 加了滚轮转发,没意识到页面上**每个 `TextBox`/`PasswordBox` 模板里都带一个内层 `ScrollViewer`(`PART_ContentHost`)**。

- **新探针 `tools/qa/wheel-probe.ps1`**:UIA 打开设置页 → 找 `ScrollPattern` → `SetScrollPercent` 定位 → 用 `SendInput`(`MOUSEEVENTF_WHEEL` 0x0800,mouseData=±120)逐格合成滚轮,每格采样 `VerticalScrollPercent`、进程 `TotalProcessorTime` 与 `AutomationElement.FromPoint`(光标下的元素)。这是区分「忙(CPU 高)」与「事件被吞(CPU ≈ 0)」的关键工具。
- **探针自身的坑(浪费一轮)**:`Add-Type` 里的 `INPUT` 结构必须正好 **40 字节**(x64:`uint type` + `MOUSEINPUT` 32 字节 + 4 字节对齐),多加两个 `int pad` 变成 48 字节 → `SendInput` 返回 0(error 87),**十格滚轮全部静默丢弃**,读数全是 d=0,极易误判成「真 bug 复现」。另外 `SetCursorPos` 与该类型里的同名 extern 方法会撞「成员名称不能相同」,一律用单块 `MemberDefinition` 的 static 方法集合。
- **实测定位(安装版 v26,光标固定在窗口 50%/45% 处)**:从 10% 向下滚,连续三格正常(10 → 23.3 → 36.6 → **49.9**),第 4 格起 `pct` 恒定 49.9、`cpu = 0ms`,连续 5 格不动,而同一行的 `under` 显示 `Edit:API Key#KeyBox`。结论:**滚轮事件被投递到 `KeyBox` 的内层 ScrollViewer,它把 `Handled` 置真而自身无处可滚,页面纹丝不动**;CPU 为 0 说明与软件渲染耗时无关,是纯事件吞噬。「得等一会儿才好」的真实机制不是等,而是**光标移开那个输入框**之后滚轮才又能到达外层。
- **修法(替换 v26 的局部方案)**:删掉 `ExcludedList_PreviewMouseWheel` 与递归版 `FindScrollViewer`,改成页级 `Body_PreviewMouseWheel`(挂在 `src/FastTranslation.App/SettingsWindow.xaml:88` 的 `ScrollViewer x:Name="Body"`;`PreviewMouseWheel` 是隧道事件,先于内层冒泡处理器执行)。逻辑:`FindInnerScrollViewer(e.OriginalSource)` 沿 `VisualTreeHelper.GetParent` 上溯到 Body 之前,取最近的 `ScrollViewer`;若它能朝该方向滚(`CanScroll`:`delta > 0` 要 `VerticalOffset > 0.5`,否则要 `VerticalOffset < ScrollableHeight - 0.5`)就放行返回;否则 `e.Handled = true` 并 `Body.ScrollToVerticalOffset(Clamp(VerticalOffset - Sign(delta) * step))`。`step = SystemParameters.WheelScrollLines * 16.0` DIP(`WheelScrollLines == -1` 时取 `0.9 * ViewportHeight`,`<= 0` 则不接管),与 WPF 原生一档 48 DIP 对齐(实测一格 = 13.29% ≈ 50 DIP)。
- **效果(同坐标、同探针、同格数)**:修复前 10 → 49.9 后卡死 5 格;修复后连续 10 → 23.3 → 36.6 → 49.9 → 63.2 → 76.5 → 89.7 → 100,向上方向 90 → 76.7 → 63.4 → 50.1 → 36.8 → 23.5 → 10.3 → 0 同样连续,每格 `cpu` 16–203ms(整窗重光栅化的固有成本,与 v26 的测量一致)。
- **副产品**:滚动链(内层到边后交给外层)现在对 `ListBox`/`TextBox`/`PasswordBox`/`ComboBox` 一律生效,不再只有排除列表。
- **验证**:`dotnet build` 0 警告 0 错误;`dotnet test` 24 + 48 + 6 = **78/78**。

## v29 「装不上,闪一下就消失」:Windows Installer 修复源陷阱 + 1.0.1(2026-10-26)✅

**背景**:用户把 `publish/dist` 里的 MSI 经微信转给自己后双击,现象是「装不上,闪了一下就消失了」。

- **证据链(先看日志再动手)**:`Get-WinEvent -ProviderName MsiInstaller` 显示 **15:13、15:19、15:20 三次安装全部 status 1603**,来源都是 `C:\Users\<user>\xwechat_files\wxid_*\msg\file\2026-10\FastTranslation-1.0.0-Setup(2).msi` 与 `(3).msi`。同时 ARP 里 `{666548C4-B2CB-4B0F-9743-2F158CA845F4}` 仍在,但 `%LOCALAPPDATA%\Programs\FastTranslation` **整个目录没了**,开始菜单快捷方式指向不存在的 exe —— 用户看到的「闪一下」就是 msiexec 起来又立刻回滚
- **排除「文件坏了」**:微信那份 MSI 与本地产物 **SHA256 完全一致**(都是 `CEE76CB6…C999250AAA`,53079165 字节),所以不是传输损坏
- **真根因(`/l*v` 日志逐行确认)**:同一 ProductCode 已装过、又拿**改了名的同一份包**去双击时,Windows Installer 走「从新源修复」路径:它按**原包名** `FastTranslation-1.0.0-Setup.msi` 去新目录里找文件 → `Note: 1: 2203 … FastTranslation-1.0.0-Setup.msi 3: -2147287038` + `Note: 1: 1316` → `SECREPAIR: Error determining package source type` → `SECUREREPAIR: SecureRepair Failed` → **1603**,且修复流程先删后拷,回滚后旧装的文件也没了(所以「连原来的也没了」)
- **反证两例**:①先 `msiexec /x` 卸载、再用**同一份微信文件**全新安装 → **exit 0,262 文件**;②装好之后**再点同一份、同一路径**的文件 → **exit 0**(源与记录一致时 SecureRepair 通过)。⇒ 陷阱只在「同版本 + 换了路径/文件名」时触发,朋友首次安装不受影响
- **加固(本版实际改动)**:`installer/Product.wxs` 增加 `<Property Id="MsiLogging" Value="voicewarmupx" />` —— 实测把改名副本再装一次(仍 1603)时,`%TEMP%\MSI85990.LOG` 自动落盘 214KB / 1074 行,失败原因一行就能读出来;版本升 **1.0.1**(`Product.wxs` 的 `Version` + `Directory.Build.props` 新增 `<Version>1.0.1</Version>`,About 显示 `1.0.1+1a218f5`),新版本号让微信转发的那份变成**正常升级**而不是同版本修复
- **脚本去硬编码版本**:`tools/qa/make-portable.ps1`、`install-msi.ps1` 改 1.0.1;`hash-dist.ps1` 过滤器改 `FastTranslation-*`;`portable-selftest.ps1`、`zip-inspect.ps1` 改成取 `dist` 里最新的 `*-portable.zip`
- **新增 QA**:`install-state.ps1`(安装目录/快捷方式/ARP/临时日志一览)、`msi-events.ps1`(MsiInstaller 事件)、`post-install-check.ps1`(ProductVersion + MsiLogging 日志 + 启动存活)、`integrity-check.ps1`(262 文件 + exe 哈希 + 日志摘录)、`trap-test.ps1`(改名副本复现 1603 并检查是否自动落日志)、`launch-check.ps1`
- **验证**:`dotnet build` 0 警告 0 错误;`dotnet test` 24 + 48 + 6 = **78/78**;1.0.1 MSI **53070995 字节** `sha256 378ED782…A2600D`;`msiexec /i` 覆盖升级 1.0.0(源为微信路径)→ **exit 0**;ARP 单条 `{E66C09F4-7E13-41E2-9EBB-E43327542FD1}` `Fast Translation 1.0.1`;installed files=262、exe hash matches build=True、satellite resources installed=12、`ProductVersion = 1.0.1+1a218f56ba6e38e4b0b0800276e8d28a114ca7d9`;便携包 **61621089 字节** `sha256 880597BA…CE67C4`(263 条目)
- **对外发放策略**:朋友首选 **portable zip**(解压即用,不经 Windows Installer,零安装风险);MSI 作备选,并在 `publish/dist/README.txt` 增加「安装窗口闪一下就不见了怎么办」三条(已装同版本 / 先卸载再装 / 把 `%TEMP%\MSI*.log` 发我)
- **发布**:Release id `423960082`,tag `v1.0.1` → commit `dcdbdb6`,https://github.com/LYW206817/FTranslation/releases/tag/v1.0.1;资产 `FastTranslation-1.0.1-Setup.msi`(digest `sha256:378ed782…a2600d`)、`FastTranslation-1.0.1-portable.zip`(digest `sha256:880597ba…ce67c4`),均 `state: uploaded` 且 digest 与本地 `Get-FileHash` 一致;上传用 `run_in_background` + `job_output`(zip 26s / msi 19s)

## v30 自动更新 + 换成 Inno Setup 安装包 + 1.1.0(2026-10-27)✅

**背景**:1.0.1 已经发给朋友用,再发新版就得挨个传安装包。需求是「程序自己发现新版本、一键装好并自动重开」。设计规格把自动更新划在 v1.1(spec:683、:989、:1012),本次落地。

- **通道结构(受私有仓库限制)**:主仓库 `LYW206817/FTranslation` 是私有的,匿名读 release 一律 404,而自动更新必须能匿名取清单 ⇒ 新建**公开**仓库 `LYW206817/FTranslation-Updates` 专放清单与安装包,主仓库继续私有。清单 `version.json` 走三顺位:GitHub Pages(`https://lyw206817.github.io/FTranslation-Updates/version.json`)→ jsDelivr(`https://fastly.jsdelivr.net/gh/LYW206817/FTranslation-Updates@main/version.json`)→ gh-proxy 包 raw.githubusercontent。实测三条都能 200。
- **Core(纯逻辑,可测)**:`ReleaseVersion`(readonly record struct,去 v 前缀、切 +build 与 -pre、最多 4 段、数字逐段比较 —— 预发布一律按正式版号比,单通道下 `1.1.0-beta` 就是 1.1.0);`UpdateManifest.TryParse`(version/url/sha256 必填,url 必须绝对 http(s) 且 `.exe` 结尾,sha256 必须 64 位十六进制并 tolower,size 必须正整数,notes 截断 2000,清单上限 64KB);`UpdateChecker` 按序试三个清单地址,非 2xx/异常/解析失败都记进 Detail 继续下一个,全失败才返回 Failed。
- **Platform**:`UpdateInstaller` 负责下载与落地 —— `MirrorCandidates` 生成候选(原 URL、`https://gh-proxy.com/`+URL、github.com 补 www 形式)、`VerifyDownload` 先比 size 再比 SHA256、先写 `.part` 再 `File.Move(overwrite:true)`、失败即删并换下一个候选。`LaunchInstaller` 用 `UseShellExecute=true` 起 `setup.exe /SILENT /SUPPRESSMSGBOXES /NORESTART /UPDATEPID={自身 pid}`,然后调用方立刻退出 —— 应用是托盘程序,`WM_CLOSE` 被取消,Restart Manager 关不掉它,只能把 PID 交给安装器自己等。
- **App**:`UpdateCoordinator`(启动后 30s 首次静默检查,之后每 6h;`CheckAsync(interactive)` 静默失败不吭声,手动失败给气泡;`IsInstalledCopy()` 判断是不是 `%LOCALAPPDATA%\Programs\FastTranslation` 那份);`UpdateWindow`(470×340,发现新版本/当前版本/更新说明/进度条/稍后/立即更新;便携版把按钮换成「打开下载页」,因为安装器会把程序装到默认位置而不是替换便携目录);`UpdateLog` 写 `%LOCALAPPDATA%\FastTranslation\update.log`(超 512KB 先删)。托盘新增 `1004 检查更新`(原生 `CreatePopupMenu` 那套,1001 设置/1002 暂停取词/1003 退出),设置页「系统」卡片新增「检查更新」按钮。
- **安装包从 WiX MSI 换成 Inno Setup**:用户要「点一下的 install.exe,还能选路径」,这正是 Inno 的默认形态。`installer/FastTranslation.iss`:`PrivilegesRequired=lowest`(per-user,自动更新永不弹 UAC)、默认目录 `{localappdata}\Programs\FastTranslation`、`Compression=lzma2/ultra64`、`ArchitecturesAllowed=x64compatible`、`MinVersion=10.0`。**`CloseApplications=no`/`RestartApplications=no`**:靠 Restart Manager 关不掉托盘程序,改成 `[Code]` 里 `ParseUpdatePid()` + `ProcessAlive()`(`OpenProcess(SYNCHRONIZE|0x1000)` + `WaitForSingleObject` 判 `WAIT_TIMEOUT`)轮询 250ms/上限 60s;`PrepareToInstall` 里 `UninstallLegacyMsi()` 遍历 HKCU/HKLM/HKLM_32 的 Uninstall 键,找 DisplayName='Fast Translation' 且 UninstallString 含 msiexec 的子键,静默 `msiexec /x` 掉 —— 朋友不用手动清理旧 MSI。`[Run]` 里 `Flags: nowait postinstall` **不能加 skipifsilent**,否则静默升级后程序不会被拉起来。
- **Inno Pascal 脚本的两个坑**:①没有内置 Win32 导入表,`OpenProcess` 直接报 `Unknown identifier`,必须显式 `external 'OpenProcess@kernel32.dll stdcall'` 声明,`PROCESS_QUERY_LIMITED_INFORMATION`/`SYNCHRONIZE` 也没有常量,写 `$1000`/`$00100000`;②装 6.7.3 的 Languages 目录里**没有 ChineseSimplified.isl**,所以安装向导只有英文,中文只出现在应用自身。
- **MSYS2 路径转换会毁掉静默安装(真事故)**:在 win_bash 里直接 `./Setup.exe /VERYSILENT` 时,`/VERYSILENT` 被 MSYS2 当成路径转成 `C:/Users/.../VERYSILENT`,Inno 于是把第一个非开关参数当目标目录,**装到了 `D:\SoftwareDownload\FastTranslation`**。正确姿势是用 `.ps1` + `Start-Process -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/LOG=...' -Wait -PassThru`。
- **下载慢的真因(本次最值的发现)**:E2E 演练点「立即更新」后进度 1% → 6% → 7% → 9% 就近乎停住。curl 实测:直连 `https://github.com/.../releases/download/...` 不带 `-L` 是 302 零字节,带 `-L` 30 秒只下 276737 字节(**约 9 KB/s**,来自 objects.githubusercontent.com),46MB 要一个半小时;同一个 URL 加 `https://gh-proxy.com/` 前缀后 `http=206`、速率 108–948 KB/s。修法是 `DownloadAsync` 里把候选**按代理优先重排**:`.OrderBy(c => c.StartsWith(ProxyPrefix, ...) ? 0 : 1)`,直连留作最后兜底。改完 E2E 里 46MB 约 100 秒下完。
- **E2E 演练(完整跑通)**:把 `version.json` 临时写成 `1.1.1`(url/sha/size 指向真实的 1.1.0 包)→ 安装 1.1.0(带更新器)→ 启动 → `update.log` 出现 `16:12:08 update available: 1.1.1 (running 1.1.0)` → UIA 抓到「发现新版本」对话框 → 点「立即更新」→ `16:32:58 downloaded 1.1.1 ...` → `16:33:01 installer launched` → 应用退出 → 安装器等 PID 结束 → 覆盖安装 → `16:33:46 update available: 1.1.1 (running 1.1.0)`(重启后再次检查,证明程序被自动拉起)。演练后已把清单还原成 1.1.0 并指向最终产物(sha `8bb256d0…`、46305182 字节)。
- **UIA 自动化坑**:`AutomationElement.RootElement.FindFirst(Children, NameProperty)` **找不到**这个 WPF 对话框,必须 `PropertyCondition(ProcessIdProperty, $pid)` + `FindAll(Descendants, …)` 再按 Name/ControlType 过滤;`.ps1` 必须 ASCII-only,中文字面量会让 PowerShell 报「字符串缺少终止符」,用 `[char]` 码点拼;点按钮走 `InvokePattern`。
- **Pages 缓存**:`Cache-Control: max-age=600`,改完清单最多要等 10 分钟才对外可见(jsDelivr 缓存更久);发布流程要留这个窗口,别以为提交就生效了。
- **验证**:`dotnet build` 0 警告 0 错误;`dotnet test` 62 + 57 + 6 = **125/125**;Setup.exe **46305182 字节** `sha256 8bb256d0…`;便携包 **61672161 字节**(263 条目);安装后 264 文件、ARP 单条 `{8B1F2C34-7A6D-4E58-9C21-5F3D8E7A1B60}_is1`、无 MSI 残留、旧 MSI 被自动卸载。
- **残留风险(已告知用户)**:已经装 1.0.1 的朋友身上没有更新器,**必须手动装一次 1.1.0**,从那以后才真正自动。

- **1.1.0 发布**:主仓库 Release id `425206694`,tag `v1.1.0` → commit `528d4f27ad5aa387fbfa68db1e5453645f7437f9`,https://github.com/LYW206817/FTranslation/releases/tag/v1.1.0 ;两个资产均 `state: uploaded`:`FastTranslation-1.1.0-Setup.exe`(id 680982868,**46305182 字节** `sha256 8bb256d0c07fdfa87b4617c4759a19589276abf4af6a31ae94a92c131d709a09`)、`FastTranslation-1.1.0-portable.zip`(id 680983576,**61672161 字节** `sha256 2567d8b3f86e3fc9550b6a09664139c778d7770baa68b72d0273309524cbd2b4`)。更新通道仓库 `FTranslation-Updates` 的 v1.1.0 资产 id `680940805`,清单 `version.json` 已指回 1.1.0。
- **发布 API 坑**:`POST /releases` 的 `target_commitish` 传短 sha(`528d4f2`)会被拒 `422 tag_name is not a valid tag`,必须传完整 40 位 sha;`body` 含中文时用 `curl --data-binary @file`(JSON 由脚本生成,别让 PowerShell 拼 —— 在 win_bash 里 `@{}` 的 `$` 会被吞掉)。

## v31 安装向导中文化与奶白泛蓝外观(2026-10-27)✅

**背景**:用户三条诉求 —— ①安装界面能说中文;②升级时能改安装路径(当前改不了);③按奶白泛蓝的品牌风格美化安装器并加一点动效。

- **路径改不了的根因**:Inno 的 `DisableDirPage` 默认值是 `auto`,同一 AppId 已注册时会**自动隐藏「选择目标位置」页**,于是覆盖升级时用户根本没机会改目录。改成 `DisableDirPage=no`(永远显示)+ `UsePreviousAppDir=yes`(静默升级仍沿用原目录),两者缺一不可:只想改路径就变成每次升级都要手点目录,只想静默就仍然看不到页。
- **中文语言包**:本机 Inno 6.7.3 的 `Languages` 目录里没有 `ChineseSimplified.isl`(Inno 7 的发行目录里有,417 行,UTF-8,`LanguageID=$0804`/`LanguageCodePage=936`)。把 Inno 7 那份复制到 `installer/Languages/ChineseSimplified.isl` 即可被 Inno 6 直接编译。`[Languages]` 里写相对路径 `Languages\ChineseSimplified.isl`(相对 .iss 所在目录);`ShowLanguageDialog=no`,只有一种语言就不用选。
- **外观指令**:`WizardStyle=modern polar hidebevels` —— 逐个值实测过「下一步」按钮底色:`modern light`/`classic` 全是 #FDFDFD,`windows11` #E1EDF6,`zircon` #A0D4DE,**只有 `polar` 给出蓝色系(#A2D0FE)**;`WizardBackColor=#F7F9FC`(= 应用 WindowBg);`WizardImageFile` 三个 DPI 尺寸(宽高比 164:314)、`WizardSmallImageFile` 三个方形;`DefaultDialogFontName=Microsoft YaHei UI`(与应用窗口同一族)。
- **刻意不要 `dynamic`**:`dynamic` 会跟随系统深浅色,但它的深色调色板**只重绘窗体和面板,不重绘 notebook 页面底面** —— 实测深色 Windows 下得到「深色边框 + 白色页 + 近白文字」的割裂画面。一份恒定的奶白向导在两种模式下都诚实可读,半吊子的深色化不可读。改后 `WizardBackColorDynamicDark` 一并删除。
- **inno 的 TColor 是 $BBGGRR**:写 `$FF6225` 才显示成 #2562FF,`$36251B` 才显示成 #1B2536。调色板常量(`LightBrand`/`LightPrimary`/`LightSecondary`/`LightHair`)在 `[Code]` 顶部集中定义。
- **改色必须先 `StyleElements := []`**:只赋值 `Font.Color` / `Panel.Color` 会被 VCL 主题整个覆盖,肉眼看不到任何变化。`Paint()` 的顺序是 **先 `StyleElements := []` → 再 `BevelOuter := bvNone` → 再 `Color` → 最后 `SetBounds`**。
- **副作用(踩过一次)**:清掉 `StyleElements` 也丢掉了主题给的透明背景,TNewStaticText 会给自己铺一块**白底方块**压在页面上(实测欢迎页出现 339234 个纯白像素)。所以 `ColorText()` 必须同时把 `Text.Color` 设成页面底色(`LightBack = $FCF9F7` = #F7F9FC)。改完白色像素归零。
- **动效**:Pascal Script 里**没有 TTimer、没有过渡动画 API**;`CreateCallback(@Proc)` + `SetTimer(0, 0, 20, CreateCallback(@AnimTick))` 可用(`CallbackAddr` 和 `TTimer` 都不存在)。做的是底部一条 3px 的步进进度条:`PageStep()` 把六页映射成 1..6,`AnimTick` 每次走剩余距离的 1/4(线性步进在 20ms 定时器上像卡顿),`HighContrastActive` 时直接跳过动画。实测四页进度条右端依次 196 / 382 / 568 / 753 px(1138 宽窗口的 1/6…4/6)。
- **按钮改不了**:`TNewButton` **没有 Color 属性**(CHM 里只有 Font/Enabled/ModalResult/Style 等);`StyleElements := []` + `Font.Color := $FF0000` 截图里搜不到一个红像素 —— polar 的按钮面与文字都由主题自绘。能动的只有 `Font.Size` 与 `Font.Style := [fsBold]`。
- **父级挂载**:TPanel 挂 `WizardForm.MainPanel` **完全不渲染**,挂具体 Page 只在该页渲染;常驻元素(页脚 hair、进度条)一律挂 `WizardForm`。布局坐标必须过 `ScaleX()/ScaleY()`(本机 1.75 DPI 下 `ScaleX(100)=187`)。
- **美术资源**:`tools/qa/make-installer-art.py`(PIL)生成 `assets/installer/` —— 横幅是奶白→淡蓝竖向渐变 + 底部品牌蓝柔光 + 图标 alpha 0.16 水印;页头小图不是透明图标(透明 PNG 在 #F7F9FC 上几乎看不见),而是品牌蓝渐变圆角方里挖出奶白水滴。
- **自动更新兼容性**:静默升级带着 `/UPDATEPID=<pid>` 进来,安装器先等那个进程退出再覆盖文件(60s 上限),`[Run]` 不带 `skipifsilent` 所以装完会把应用重新拉起来。实测:应用运行中启动 Setup → 等 PID → 覆盖 264 文件 → 拉起新进程,exit 0;若应用一直不退则 60s 后放弃并回滚(exit 5,日志 `DeleteFile 失败;错误代码 5`),这是预期行为。
- **仍未落地**:用户早前提的「GitHub 国内不友好是否迁 Gitee」还挂在待办 —— 需要先核实 Gitee 发行版附件的单文件上限、配额与实名要求(46MB 的 Setup.exe 能不能直接挂)。

## v32 安装向导中文化、奶白泛蓝外观与托盘占用修复(2026-10-27)✅

- **中文界面**:本机 Inno 6.7.3 的 `Languages` 目录没有简体中文,直接从 Inno 7 的 `ChineseSimplified.isl`(417 行,`LanguageID=$0804`/`LanguageCodePage=936`)拷到 `installer/Languages/`,`[Languages]` 只留这一条 + `ShowLanguageDialog=no`。
- **升级时能改路径**:Inno 的 `DisableDirPage` 默认值 `auto` 会在同一 AppId 已注册时**自动隐藏**「选择目标位置」页 —— 这正是用户升级时看不到改路径入口的原因。改为 `DisableDirPage=no` + `UsePreviousAppDir=yes`(静默升级仍沿用原目录)。
- **配色**:走 `WizardStyle=modern polar hidebevels`(逐值实测,见下)+`WizardBackColor=#F7F9FC`;文字与细线在 `[Code]` 里按应用色板绘制。**刻意不要 `dynamic`**。
- **Inno 的 TColor 是 `$BBGGRR`**:`$FF6225` 显示成 `#2562FF`,`$36251B` 显示成 `#1B2536`。
- **改色必须清 `StyleElements`**:只赋 `Font.Color`/`Panel.Color` 会被 VCL 主题覆盖;先 `Control.StyleElements := [];` 再设色才生效。副作用是**丢掉透明背景**,TNewStaticText 会铺一块白底方块(实测 339234 个纯白像素、bbox (410,82)-(1088,630)),所以 `ColorText` 必须同时把 `Text.Color` 设为页面底色 `#F7F9FC`。
- **按钮改不了颜色**:`TNewButton` 在 CHM 里没有 `Color` 成员(`Unknown identifier 'COLOR'`),`StyleElements := []` + `Font.Color` 也无效(polar 主题自绘)。能动的只有 `Font.Size`/`Font.Style`。所以按钮观感只能靠 WizardStyle 选型:实测「下一步」底色 polar=`#A2D0FE`、windows11=`#E1EDF6`、zircon=`#A0D4DE`、modern light/classic=`#FDFDFD` ⇒ 只有 polar 是蓝色系。
- **动效 = 底部进度条**:Inno Pascal 没有 TTimer,但有 `SetTimer(0,0,20,CreateCallback(@AnimTick))` + `KillTimer`。`CurPageChanged` 里按 `PageStep()/PageCount` 算目标宽度,`AnimTick` 每次走剩余距离的 1/4(线性步进在 20ms 定时器上看着卡)。`HighContrastActive` 时不动画。
- **父级挂载**:常驻装饰面板挂 `WizardForm`(挂 `MainPanel` 完全不渲染,挂具体 Page 只在该页出现)。
- **美术资源**:`tools/qa/make-installer-art.py` 生成横幅(奶白→淡蓝竖向渐变 + 底部品牌蓝柔光 + 图标水印)与右上角蓝底圆角徽标(纯透明 PNG 在 `#F7F9FC` 上几乎看不见,所以做成实心蓝底挖空水滴)。横幅与 `WizardBackImageFile` **不能同时用**,后者会把前者替代掉。
- **托盘占用修复**:`CloseRunningApp()` 在 `PrepareToInstall` 两条分支都调用,用 `taskkill /f /im` 关掉正在运行的应用。根因:托盘程序点 X 只是隐藏窗口,Restart Manager 关不掉它(`CloseApplications=no`),已加载的 DLL 全部拒绝替换,手动双击 Setup 会以 `DeleteFile 失败;错误代码 5` 回滚。自更新时 `/UPDATEPID` 非 0,函数直接让开,仍由 `WaitForUpdateProcess()` 等 PID 退出。
- **验证**:重编译 `Successful compile (64.593 sec)`;六页截图(1138x870)像素级确认 —— 每页 body `#F7F9FC`、WelcomeLabel 区纯白像素归零、页脚 hairline 固定在 y=774–775、进度条按 (15,196)/(15,382)/(15,568)/(15,753)/(15,939) 递进;应用在托盘里时静默安装 `exit=0`、安装后自动拉起、264 文件且与 `publish/ft-folder` 二进制一致;`dotnet test` 125/125。
- **版本 1.1.1**:`Directory.Build.props` 与 `.iss` 的 `AppVersion` 同步;`publish/dist/README.txt` 与根 `README.md` 的产物名跟着替换。

## v33 更新器多代理轮换(国内下载加速)与代理只作用于 github.com(2026-10-27)✅

**背景**:用户问「更新器在 GitHub,国内不挂梯子下载很慢,有没有方法」。先实测再动手 —— 直连 `github.com` 本轮根本连不上(本机 443 被 reset,`http=000`),上一版记录的「直连 9 KB/s」已是较好情况。

- **镜像实测(本机 curl,ranged 抽样 + 整包)**:`gh-proxy.com` 206、497945 B/s;整包 46626589 字节 82.16s 下完、均速 567532 B/s、落地 SHA256 与清单一致 ⇒ 现有策略在当前网络下已能在 1.5 分钟内装完。`ghfast.top` 206、349837 B/s(可作第二备选)。**全死**(http=000)的一大批:`ghproxy.net`、`gh-proxy.top`、`ghp.ci`、`github.moeyy.xyz`、`ghfile.geeknimo.com`、`gitproxy.click`、`gh.api.99988866.xyz`、`gh.llkk.cc`、`hub.whtrys.space`、`gh-proxy.ygxz.in`、`ghx.gh-proxy.com`、`ghproxy.cc`;`gh-proxy.net` 302 只 11 字节、`ghproxy.homeboyc.cn` 403、`gh.ddlc.top` 429。⇒ 能用的就这两个,候选表不追求长度,追求每个都能应答。
- **速率抖动大**:同一个 `gh-proxy.com` 连续两次 3MB ranged 得到 2234908 B/s 与 319394 B/s。所以第二代理是**真兜底**,不是装饰;哪个快由运行时决定,不写死单点。
- **两个真实缺陷(本次修的)**:①`ProxyPrefix` 只有一条,主代理挂了直接掉到直连(等于没有更新);②`DownloadAsync` 里的 `.OrderBy(c => c.StartsWith(ProxyPrefix) ? 0 : 1)` 是**对全部候选无条件重排**,而 `MirrorCandidates` 又给任意绝对 URL 都加代理前缀 —— 一个非 GitHub 的下载地址会被先送进只能取 GitHub 的代理,把唯一能work的候选浪费掉。
- **改法(Platform)**:`ProxyPrefixes` 改成 `private static readonly string[]` = { `https://gh-proxy.com/`, `https://ghfast.top/` };`MirrorCandidates` 改为**按 host 组装**——非 `github.com` 直接返回原 URL 单候选,github.com 则按「两个代理 → 原 URL → `https://www.github.com`+PathAndQuery」顺序建表(新增私有 `AddOnce` 去重);`DownloadAsync` 删掉那次排序,直接用 `MirrorCandidates` 的顺序。注释里写明「代理只讲 GitHub,给别人加前缀是把能下的变成必然失败」。
- **清单通道同样加一条**(Core):`UpdateChannel.ManifestUrls` 从三顺位变四顺位,在 `gh-proxy.com` 那条后面补 `https://ghfast.top/https://raw.githubusercontent.com/{Owner}/{Repository}/main/{ManifestFile}`。实测两条都 200、558 字节。理由:清单取不到,用户就永远不会知道有更新 —— 比安装包慢更致命。
- **测试**:原来 4 个 `MirrorCandidates` 断言里有两个**断言的正是要删掉的行为**(任意绝对 URL 加代理前缀)。改成 5 个:`LeavesGarbageAlone`、`DoesNotProxyANonGitHubUrl`(非 GitHub 只留原 URL)、`PutsEveryProxyBeforeTheDirectHost`(原 URL 不在首位、首位是 gh-proxy、含 ghfast、且原 URL 之前全带 `://gh`)、`AddsTheWwwHostLastForGitHub`(www 在末位,排在直连之后)、`DoesNotRewriteAnApiGitHubHost`。测试夹具用真实形态的 `v1.1.1` Setup URL。
- **验证**:`dotnet build` 0 警告 0 错误;`dotnet test` **62 + 58 + 6 = 126/126**(Platform.Windows.Tests 由 57 增至 58)。
- **Gitee 待办已查清,但仍不迁**:官方帮助页写明发行版**单附件 ≤100MB、单仓库附件总量 ≤1GB**(46.6MB 的 Setup.exe 完全挂得下,账号 `lyw206817` 也存在,id 17012524)。但 ①Gitee 不支持 GitHub 风格的 `/releases/download/{tag}/{file}`;②附件直链带**不稳定数字 id**(形如 `/attach_files/{id}/download/{name}`),无法模板化,每次发布只能手填;③匿名 API 拿不到附件列表(对 RuoYi/Sa-Token/JeecgBoot 等公开仓库 `grep attach_files` 计数一律 0),页面又是前端渲染;④实测大文件吞吐极不稳定(同一条 archive 一次 2.8 MB/s、重测 73 KB/s)。⇒ 走 Gitee 每次发布都要手工上传 46MB 并手填清单,运维成本换来的是并不保证的速度,本轮不做;真要更进一步,应评估国内的 OSS/R2 而不是第 4 次搬仓库。


## v34 安装向导排版修复 + 徽标换真图标,1.1.2 发布(2026-10-27)✅

**背景**:1.1.1 发出后用户看图指出两处缺陷 ——「字体排版重叠」和「右上角那个蓝环不应该是我们的 logo 吗?」。

- **缺陷 1 根因(实测,不是猜)**:`InitializeWizard` 把 WelcomeLabel1 放大到 22pt bold、PageNameLabel 15pt、FinishedHeadingLabel 20pt,但 `TNewStaticText` 的矩形**不会跟着变**。探针 probe-t4/x2–x6 实测原始几何:WelcomeLabel1 `top=30 h=91`、WelcomeLabel2 `top=121`(30+91=121,零间距);PageNameLabel `top=15 h=29`、PageDescriptionLabel `top=48`。字号一放大,两行标题就压到下一行说明文字上。
- **修法**:新增 `procedure FitStackedLabels()`,用 `TNewStaticText.AdjustHeight`(函数式,调用后立即改写 Height 并返回新值)重排后续 label 的 `Top`:`AdjustHeight` 实测 W1 91→137、PN 29→47、PD 58→36、FH 91→125、FL 99→36、W2 458→141;``WelcomeLabel2.Top := WelcomeLabel1.Top + WelcomeLabel1.Height + ScaleY(8)``,`PageDescriptionLabel` 用 `ScaleY(2)`,`FinishedLabel` 用 `ScaleY(6)`。**只在 `if not HighContrastActive then` 分支里调用** —— 高对比度模式没改字号,不能重排。FinishedLabel 的 Caption 是进页时才填的,所以 `CurPageChanged` 里补一句 `if CurPageID = wpFinished then WizardForm.FinishedLabel.AdjustHeight;`。
- **缺陷 2 根因**:`tools/qa/make-installer-art.py` 的旧 `badge()` 拿图标自身的 alpha 当遮罩去铺奶白,而图标 alpha 恰好是那个圆角方块 ⇒ 得到一块白圆角方块套在蓝底里。**修法**:直接把 `assets/icon/icon-256.png` 的 artwork 合成上去(4 倍超采样后 LANCZOS 缩回),再描 1px `#D6DDE8` 圆角描边,让它在 `#F7F9FC` 页面上有边界。`wizard_strip()` 的水印分支未动。
- **中文断行**:`installer/Languages/ChineseSimplified.isl:125` 的 `WelcomeLabel1` 改为 `欢迎使用 [name]%n安装向导`;22pt 下否则会断成「欢迎使用 Fast Translation 安 / 装向导」。
- **截图取证工具换了**:`publish/shot-pages2.ps1`(PostMessage ENTER 翻页)在旧进程残留/焦点漂移时会**跳过页面甚至跨页**,旧输出只有「欢迎→目标位置→附加任务」的部分。新写 `publish/shot-pages4.ps1`:每页先读第一个 `TNewStaticText` 的标题、再 GetWindowRect+CopyFromScreen,翻页用 `[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')` + `SetForegroundWindow` 重试。实测一次拿到五页且标题正确:欢迎 / 选择目标位置 / 选择附加任务 / 准备安装 / 正在安装。`shot-pages2.ps1` 的 `$w + 'x' + $h` 会抛 `Cannot convert value "x" to type "System.Int32"`(字符串+变量被当成算术),已改成 `[string]$w`。
- **截图的坑**:本机把显示缩放调过之后,`GetWindowRect` 返回的矩形比实际绘制区域大,截出来的 PNG 四周会带一圈桌面。**验证版面以内容为准,不要以像素坐标为准**;要坐标就用 `geo-dump.ps1` / `wiz-dump2.ps1` 读 UIA `BoundingRectangle`。
- **安装残留**:截图脚本在取消确认框上退出会留下 `is-XXXX.tmp`(本次 6 个,合计约 40MB)。它们不是安装器 bug,`Get-ChildItem <installdir> -File -Filter 'is-*.tmp' | Remove-Item -Force` 即可。**装好的标准文件数是 262 + unins000.exe + unins000.dat = 264**。
- **1.1.2 产物**:`FastTranslation-1.1.2-Setup.exe` **46696599 字节** `sha256 258045ca45e55f2545ecf159ac138b65a9e013e3bc30c8a0cf509d1c866e9afd`;`FastTranslation-1.1.2-portable.zip` **61672258 字节** `sha256 076a5464eb9759be8716fc872edbf846de03b381fbe4618e67cfa68f2f9f211d`(263 条目)。`dotnet build` 0 警告 0 错误;`dotnet test` **62 + 58 + 6 = 126/126**。
- **静默升级验证**:应用在托盘里时 `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART` → exit 0,264 文件,`ProductVersion 1.1.2+2565d299…`,三个二进制与 `publish/ft-folder` SHA256 全一致,`[Run]` 把应用重新拉起。
- **发布**:主仓库 commit `4b8727c262f3270995fa388048a20b084bab076e`;Release id **425380700**,tag `v1.1.2`,资产 Setup `681423290` / portable `681423826`,两者 `state: uploaded` 且 digest 与本地一致。更新通道 `FTranslation-Updates` Release id **425381763**,资产 `681425525`;manifest 由 blob `42bcf59b…` 更新为 commit `031cd4cea4d79c191b21e2ba0718b25c0f1b2c74`,内容 version=1.1.2 / size 46696599 / sha256 `258045ca…`。
- **本轮通道可达性实测(与 v33 不同,务必按次测)**:`github.com` 直连 `http=000`(拉 0 字节);`gh-proxy.com` **206 / 1.32 MB/s**;`ghfast.top` 206 / 141 KB/s;**`lyw206817.github.io`(Pages)与 `fastly.jsdelivr.net` 本轮都是 `http=000`**。清单三顺位里前两条都断,实际由第 3、4 顺位的 raw 代理供上 —— 这正是 v33 把清单通道补到四顺位的原因。

## v35 并行分段下载 + 清单源竞速 + 手动检查可见反馈(2026-10-27)✅

**背景**:v33 提交后用户指出「刚才我的环境就是带着节点的,现在才是国内真实环境,根本连不了 github,检查更新也没反应」。v33 的全部测速数据因此作废,必须在国内裸网重测再改。

- **裸网实测推翻了「连不上」的判断**:`github.com/LYW206817/FTranslation-Updates` 网页 **200**(0.07–0.10 s 连接、0.40–1.02 s 完成、240749 字节),`objects.githubusercontent.com` 可达;真正不可达的是 `raw.githubusercontent.com`(http=000)。⇒ 问题不是「连不上」,是**单连接被限速**。
- **update.log 证明「检查更新没反应」其实是成功**:日志末尾两条 `up to date (1.1.2)`,而线上清单也是 1.1.2 —— 结果正确,只是 `UpdateCoordinator` 对 UpToDate **完全静默**,用户看不到任何反馈。
- **24 个 GitHub 代理裸网 bench(ranged 1MB / 8 s)**:只有 `https://gh-proxy.com/` 应答(206 / 208896 字节 / **26069 B/s**);其余 23 个全 http=000(含 v33 刚加进去的 `ghfast.top`)⇒ **ghfast.top 在真实网络里是死的,v33 那个改动现在有害(白等一次超时),已删除**。
- **核心发现:单连接 ~30 KB/s,但总带宽不限**。同一个 46696599 字节资产单连接 22796–38576 B/s;并行 ranged 8 路 271648 B/s、16 路 459922 B/s、32 路 1087952 B/s、64 路 1648043 B/s。48 段并行完整下完 **elapsed=103s**,`certutil -hashfile` SHA256 与清单一致 ⇒ **分段并行 + 顺序拼接可行,且不需要任何代理**。
- **改法(Platform,`UpdateInstaller.cs`)**:`DownloadAsync` 先走 `TrySegmentedAsync`,失败才回退原有 `MirrorCandidates` 顺序循环(候选语义不变)。新常量 `:38` `SegmentThresholdBytes = 8 MiB`、`:42` `MaxSegments = 32`、`:46` `SegmentSizeBytes = 1 MiB`、`:50` `SegmentAttempts = 4`、`:54-55` `StallWindow = 10s` / `StallFloorBytes = 60 KiB`。`PlanSegments`(`:98-118`)=`IReadOnlyList<(long Start,long Length)>`,按 1 MiB 算段数并用 `Math.Min(MaxSegments, wanted)` 截断,前 `total % count` 段各多 1 字节保证不丢字节。`TrySegmentedAsync`(`:247-327`)先用 `Range: bytes=0-0` 探针,**要求 206 且 `ContentRange?.Length == manifest.SizeBytes`**,否则删 .part 放弃;`File.OpenHandle` 预分配(`RandomAccess.WriteAsync(handle, new byte[1], SizeBytes-1)`)后 32 个 `Task.Run` 并发写同一 `SafeFileHandle` 的不同偏移;`VerifyDownload` 与 `File.Move` 在 handle 释放**之后**才做。
- **踩坑 1**:写成 `using var handle = ...` 时 handle 活到方法末尾,`VerifyDownload` 的 `File.OpenRead` 撞共享冲突,**静默回退到顺序路径**(看上去像「分段没生效」)。必须换成 `using (...) { }` 块。
- **踩坑 2**:最初 2 MiB/段 = 23 连接,在 97% 处塌陷(t=145 s 仍 98%),个别 socket 衰减到几 KB/s。改成 **1 MiB/段(打满 32 连接)+ 每段 10 s/60 KiB 滑窗速率看门狗**,低于阈值即放弃该连接并从断点续传。
- **实测效果**:46.7 MB 从 **约 26 分钟 → 1~2 分钟**(我自建 console E2E:48 段 curl 103 s;分段引擎 120.4 s,sha256 与清单一致、无残留 .part;子代理同路径报 64 s)。口径按「1~2 分钟」说,不按最快的 64 s 说。
- **遗留缺口(未做,已记录)**:分段只对清单里的原始 `github.com` 地址做。若某天 github.com 整体被封,探针 0.1 s 内失败 → 回退到 gh-proxy **单连接** ≈26 KB/s,又回到 26 分钟。gh-proxy 对 ranged 同样回 206,所以「分段走代理」是明确的下一步。
- **改法(Core,`UpdateChecker.cs`)**:`ManifestUrls`(:54-58)由四顺位收紧为**两条**:Pages 源 + `https://gh-proxy.com/https://github.com/{Owner}/{Repository}/raw/main/{ManifestFile}`。剔除理由都是实测的:jsDelivr 的 `@main` 缓存卡在 **1.1.0**,会让用户**永远「已是最新」**;末尾两个 raw 通道本轮 http=000(`gh-proxy + raw.githubusercontent` 12 s 超时、裸 raw 连接被重置、`github.com/.../raw/main` 302 到死域名);而 `gh-proxy + github.com/.../raw/main` 200 / 585 字节 / 2.47 s,是活的。
- **并发竞速 + 1.5 s 防过期**:`CheckAsync`(:87-145)对所有源立即并发请求,谁先返回「比当前新」就 `race.Cancel()` 立刻返回;若先到的是「不新于当前」,只记最高版本并开一个 `StaleConfirmWindow = 1500 ms`(:74)宽限窗,期间允许另一源推翻,到点才判 UpToDate。宽限计时器**故意不绑 token**(`NextAsync` :148-171),否则被丢弃的可取消计时器会留下未观察异常。落败源记 `Cancelled`、不进失败原因;全失败仍按 `_manifestUrls` 原顺序串接每条原因(`JoinFailures` :173-175)。
- **改法(App,可见反馈)**:`UpdateCheckResult` 新增尾部可选 `SourceUrl`(:15-22,旧构造点全部仍合法);新类型 `UpdateCheckNotice`(readonly record struct :29-38,文案放 Core 里以便单测断言)=`UpToDate`「已经是最新版本 {current}。」/`Failed`「连不上更新服务器。可以到下载页手动获取最新版本:{ReleasesPageUrl} …失败原因已记录到 update.log。」。`UpdateCoordinator.CheckAsync` 注入 `_show`(:90-125),**只有 interactive 才说话**;静默定时检查保持全静默。`App.xaml.cs:191` 用 `MessageBox.Show(...)` 接,不再用托盘气泡 —— Win11 通知设置会把气泡吞掉,手动点一下必须有可见结果。update.log 另记 `check ok via {SourceUrl}`,便于事后判断走的是哪条源。
- **测试**:`UpdateInstallerTests` 58→**68**(+10:`PlanSegments` 边界、非 GitHub 不代理、探针失败回退等);新增 `tests/FastTranslation.Core.Tests/UpdateCheckerTests.cs` Core 62→**75**(+13:竞速快路径、宽限窗口、过期源被推翻、全失败串接、取消语义)。`dotnet build` 0 警告 0 错误;`dotnet test` **75 + 68 + 40 全绿**。
- **真实 E2E(裸网)**:running=1.1.2 → `elapsed=1.97s / UpToDate / source=Pages`(1.97 s 即宽限窗代价,<5 s 判定线);running=1.1.1 → `elapsed=0.10s / Available / detail=1.1.1 -> 1.1.2`(命中即返回,不等宽限窗)。

- **发布 1.1.3**:主仓库 commit `a8dcf5b`(10 文件,+846/−68;`publish/` 被 `.gitignore:9` 忽略,故 `publish/dist/README.txt` 不进库,只随便携包分发),Release id **425679276**,tag `v1.1.3`,资产 Setup `682086321` **46720027 字节**、portable `682087337` **61697636 字节**(263 条目),两者 `state: uploaded`;`ISCC` 编译 **69.203 s**;`publish/ft-folder` **262** 文件。更新通道 `FTranslation-Updates` Release id **425680600**,资产 `FastTranslation-1.1.3-Setup.exe` 46720027 字节;manifest 由 blob `30aac5e7…` 更新为 commit `2b602a44…`,version 1.1.3 / size 46720027 / sha256 `9f509f83…`。
- **发布后真实 E2E(裸网,跑的是已上线的资产)**:`CheckAsync` 以 running=1.1.2 → **1.57 s** `Available`、`detail=1.1.2 -> 1.1.3`、source=Pages;`DownloadAsync` 对新资产完整跑 **89.8 s**、detail 带 `(segmented)`、`VerifyDownload=True`、`leftover_parts=0`。
- **传播延迟(下次发布要记住)**:发布后约 2 分钟 Pages 已返回 1.1.3,而 `gh-proxy + github.com/raw/main` 这条**仍在返回 1.1.2**(GitHub raw 的 CDN 缓存,5 分钟量级)。1.5 s 宽限窗在「慢的好源推翻快的旧源」这个方向上是对的,但极端情形「旧源 0.3 s 到、新源 5 s 才到」仍可能被判成 UpToDate —— 真要根治得给清单加 cache-busting,或在宽限窗内额外比对已知最高版本。
- **提交隔离的坑**:工作树里的 `App.xaml.cs` 同时带着更新弹窗和未完成的截图翻译接线,原样提交会让 HEAD 编译失败(`error CS0246: 未能找到类型或命名空间名 ImageResultWindow`)。1.1.3 的提交里该文件是「e9ef59a 版本 + 仅 MessageBox 一行」,用 `git update-index --cacheinfo` + `commit --amend` 做的,**没有覆盖主工作树文件**;因此 `git status` 里 `App.xaml.cs` 仍显示 modified,那是残留的截图翻译在制品,属预期。
- **注意(与本轮无关但在工作树里)**:仓库里另有一批未提交的图像翻译在制品(`ImageTranslationPolicy.cs`、`ImageTranslationTypes.cs`、`RegionCapture.cs`、`QwenMtImageTranslator.cs` 及其测试),它们让 `FastTranslation.Providers.QwenMt.Tests` 从 6 个测试涨到 40 个,且**打真实 Qwen API**,偶发超时甚至把测试宿主挂到 5 分钟。本轮未触碰这些文件。

## v36 截图翻译:拖框选区 + 阿里 qwen-mt-image-2.0(2026-10-27)✅

**背景**:用户 m00928「只快速翻译还是不够,加一个截图翻译,工具条变成三个,复制/翻译/截图翻译,截图模型就用阿里的 qwen-mt-image-2.0,我想先看看这个模型翻译图片的效果」。

- **先验模型再写代码**。官方文档(help.aliyun.com/zh/model-studio/qwen-mt-image-api)给的是同步端点 `POST https://dashscope.aliyuncs.com/api/v1/services/aigc/image2image/image-synthesis`,请求 `{"model","input":{"image_url","source_lang","target_lang"}}`,响应 `output.image_url` 指向 **JPG / 与原图同尺寸 / 24h 过期** 的译后图。文档里那个 `{WorkspaceId}.cn-beijing.maas.aliyuncs.com` 前缀**不需要**,普通域名实测 200。
- **决定性实测:`image_url` 直接吃 base64 data URI**(`data:image/png;base64,…`)→ 200。桌面端因此**不需要上传、不需要公网托管**,截图不出本机。反面:先走 `getPolicy` + OSS POST 上传成 `oss://` 对象再引用,**一定失败** —— 私有对象过不了审核,`InvalidParameter.DataInspection`。
- **计价与配额**:0.004 元/张(1 元 250 张);文档标 RPM=1,背靠背两次调用实测均 200(未硬卡),产品里仍按「一次截图一次 POST」节流。
- **质量边界(已如实告知用户)**:英文网页(980x900 MDN)→ 中文,版面 1:1 保留、正文/导航/面包屑全译、`Request`/`Response`/`fetch()` 等标识符保持英文;**密集小字多栏界面会错位** —— 875x1260 的设置窗中文→英文时,左侧窄导航被压碎成 `g`/`F`/`C`/`e` 碎片。官方文档也承认复杂排版建议切块。
- **Core 契约**(新 3 文件):`ImageTranslationTypes.cs` —— `record ImageTranslationRequest(byte[] PngBytes, string SourceLanguage, string TargetLanguage, string Model)`、`record ImageTranslationResult(byte[] JpegBytes, string SourceLanguage, string TargetLanguage, string Model, long ElapsedMs, string RemoteUrl)`(带 `Error`/`Succeeded`)、`interface IImageTranslationProvider`;`ImageTranslationPolicy.cs` —— 目标语言跟随设置,`auto`/空 → `Chinese`,与源同语言时退回 `auto`。
- **Provider**(`QwenMtImageTranslator.cs` 256 行):`:163` `image_url = "data:image/png;base64," + Convert.ToBase64String(request.PngBytes)`;**只有合成 POST 参与 429/网络重试一次,译后图 GET 不重试** —— 一次 POST 就是 0.004 元,重试要付两次钱;响应里的 `error.code:message` 截断 200 字符带进 `TranslationError`。
- **拖框截屏**(`RegionCapture.cs` 357 行):`CapturedImage(byte[] PngBytes, int Left, int Top, int Width, int Height)`、`PickAsync(CancellationToken)`;覆盖整个虚拟屏、黑 0.35 遮罩、品牌蓝选区框、Esc/右键取消、**小于 8 物理像素视为取消**。`NativeMethods.cs` 只追加 BitBlt 一族,`SRCCOPY = 0x00CC0020`。
- **踩坑(遮罩混进截图)**:必须先 `overlay.Hide()` → `Dispatcher.Yield(Render)` → 等 60 ms 再 BitBlt,否则半透明遮罩被烤进画面。已知近似:mixed-DPI 多屏按 overlay 自身的单一 DPI 换算 DIP→物理像素。
- **三个入口**:划词工具条第三颗 pill「截图翻译」(取景框图标,children 顺序 `{copy, divider, translate, screenshot}`)、全局热键 **Ctrl+Alt+S**(`AppSettings.ImageHotkey`)、托盘原生菜单 **id 1005**。三者都汇到 `App.xaml.cs` 的 `RunScreenshotTranslation()`。
- **`ImageTranslationCoordinator`** 用 `Interlocked.CompareExchange(ref _busy, 1, 0)` 防重入 —— 两个拖框 overlay 同时开着会把彼此的灰罩截进图;开新一次前先 `_imageWindow?.Hide()`,否则 Topmost 浮窗会被拍进下一张;没配 Key 时走托盘气泡 + 打开设置页。
- **浮窗 `ImageResultWindow`**:独立无边框窗(不改原位置覆盖,用户决策),`SizeToContent.WidthAndHeight` 居中、图片上限取工作区 70%、小图不放大、Esc 关闭、三个动作 **复制图片 / 另存为… / 关闭**。`:107-108` `_image.Width = bitmap.Width * scale` 用的是位图 DIP 尺寸(不是像素),aspect 才和图片一致。
- **一次误判留档**:验收截图里译图下方看着像「白带」,实测是**源页面自己的空白**被框进了选区(拖框到 1000 而正文只到 940),不是布局缺陷;UIA 量到的窗口 1817x812 与 `header + image*scale + padding` 完全吻合。`.ps1` 里比对中文控件名**必须存 UTF-8 带 BOM**,否则 PowerShell 按 GBK 解码,字面量变乱码、`FindFirst(Name='复制图片')` 恒为 null。
- **验证**:`dotnet build` 0 警告 0 错误;`dotnet test` **75 + 68 + 40 = 183 全绿**;真机 E2E(脚本 `publish/e2e-shot.ps1`,DPI-aware)拿到工具条三键与译图浮窗(截图 `assets/qa/ui-shot/e2e-toolbar.png`、`e2e-result.png`,`2609 ms` 出图);UIA 点击 `复制图片` → 剪贴板得到 **1580x620** 图,`关闭` → 只剩主窗口。
- **订正 v35 末条**:图像翻译这批单测**不打真实 API** —— `QwenMtImageTranslatorTests` 与 `QwenMtProviderTests` 一样走本地 `HttpListener` 假上游(`FakeQwenServer`),无网络、无真实 Key,所以 183 个测试可以离线跑。PowerShell 探针里 DPI-unaware 进程的 `CopyFromScreen` 只抓物理屏左上角,`[Dpi]::SetProcessDPIAware()` 后坐标才与截图一致。


### 1.2.0 发布记录(2026-10-27 深夜)

- **主仓库 Release** id `425707722`,tag `v1.2.0` → commit `0e2c29581bb560e6309c076fddf9c0ff7eac55c6`。资产:`FastTranslation-1.2.0-Setup.exe` id 682156700 /46752995 B / `sha256:7b033f18…57b403`;`FastTranslation-1.2.0-portable.zip` 最终 id 682177881 /61744153 B / `sha256:a3ae5d59…af4e63`(第一版 zip 里的 README 还写着 1.1.3,修好后删资产 682157279 重传)。
- **更新通道** `LYW206817/FTranslation-Updates` Release id `425708667`(tag v1.2.0)、资产 682159085;清单 `version.json` commit `07654029`(version 1.2.0 / size 46752995 / sha256 7b033f18…)。实测两条源都返回 1.2.0(Pages 与 `gh-proxy + github.com/.../raw/main`),资产走 gh-proxy `http=206`。
- **打包顺序坑(代价是白打了一次 81 秒的包)**:`installer/FastTranslation.iss` 的 `[Files]` 读的是 `publish/ft-folder\*` 这个**发布快照**,只改 `Directory.Build.props` 的 `<Version>` 再编译,会得到一个「ARP 写着 1.2.0、实际文件还是 1.1.2」的包里外不一的安装包(实测已装 exe 仍是 `1.1.2+2565d29`)。正确顺序:改版本 → `dotnet publish -o publish/ft-folder` → ISCC → `make-portable.ps1`。
- **便携包 README 的源文件搬到 `docs/portable-readme.txt`**(受版本控制):之前只有 `publish/dist/README.txt`,而 `publish/` 在 .gitignore 里,全新 clone 的仓库根本生不出便携包。`make-portable.ps1` 现在从 docs 读、并同步写回 dist。
- **安装验证**:`publish/inno-install.ps1` 静默安装 exit 0,ARP `{8B1F2C34-…}_is1` DisplayVersion **1.2.0**、安装目录 exe `1.2.0+fcf15d6`(264 文件),拉起后 `update.log` 记 `up to date (1.2.0)`。
- **探针假超时**:`Start-Process -Wait` 装完后 `[Run]` 拉起的 GUI 进程继承了 stdout 管道,bash 侧 `| tail` 一直等 EOF ⇒ 命令看起来挂住 240 s,其实安装早已 `Installation process succeeded`。以后装完要么重定向输出到文件,要么别接管道。

## v37 结果卡:提示回到划词处 + 去掉被裁切的阴影

两个用户可见缺陷(m01334 原话:「当前插入,保存失败的时候弹窗固定出现在左上角,应该是出现在划词的地方,还有卡片边框的阴影没有消除」):

**1. 插入失败的提示固定在屏幕左上角。** 根因在调用顺序:`App.xaml.cs:457` 的 `WriteBackAsync` 先 `_card?.Hide()` 再写回,失败后 `ShowNotice` 走到 `if (!_window.IsVisible) ShowAt(new ScreenPoint(80, 80), null)` —— 卡片已被收起,于是按硬编码的 (80,80) 重新显示。修法:`ResultCardWindow` 在 `ShowAt` 里记住 `_lastAnchor / _lastRect / _hasPosition`,`ShowNotice` 复用这份位置;从未显示过时才退回 `WindowsSelectionCapture.CurrentCursorPoint()`。实测提示卡由左上角 (117,112) 挪到划词处 (1202,724)。

**2. 卡片阴影其实一直在被窗口边界裁切。** `FtTheme.CardShadow()`(BlurRadius 32 / ShadowDepth 8)挂在撑满窗口的根 `Border` 上,而卡片窗口是 `AllowsTransparency + SizeToContent=Height`、没有给投影留任何透明边距,于是投影被裁成一圈 1~2px 深的硬边(实测边框外像素 `(217,227,241)`,再往外一格直接是背景)—— 看着就像「阴影没擦干净」。这与 v2x 里「工具条去阴影」是同一条决定,于是照做:删掉 `CardShadow()`(结果卡 + 截图译图卡),只留 1px 发丝边框;顺手删掉早已没有引用的 `PillShadow()`。

**3. 提示卡顺手压扁。** 提示模式下 `_sourceBlock` 折叠、header/footer 外边距归零(正常卡片由 `RestoreFullChrome()` 还原),卡片高度 112 DIP → 88 DIP,不再是一行字顶着一大片空白。

**验证方式(publish/CardProbe,gitignore 内)**:PowerShell 5.1 加载不了 net10 程序集(`GetTypes` 报 `Unable to load one of the requested types`),所以改用引用 `FastTranslation.Platform.Windows` 的 WPF 探针,按 结果卡 → 收起 → 提示 的顺序驱动 `ResultCardWindow` 并用 `Graphics.CopyFromScreen` 逐帧截图(`assets/qa/probe-card-1-result.png` … `-4-after-timer.png`)。同时确认收起后屏幕无残留:提示自动消失后原位置上没有任何边框/白块像素。

`dotnet build` 0 警告 0 错误;`dotnet test` 75 + 68 + 40 = **183 全绿**。

## v38 结果卡:检查更新弹框与窗口标题栏统一到 Fluent 主题

用户 m01303 原话:「当前检查更新弹框色调样式和主流统一一下」。深色下系统 `MessageBox` 是一块惨白的原生窗,和整窗配色割裂;确认范围时用户选了「弹框 + 所有窗口的深色标题栏」——只换弹框的话,深色下原生白色标题栏仍然破坏整体观感。

- **新窗口 `src/FastTranslation.App/NoticeWindow.xaml` / `.xaml.cs`(替代 MessageBox)**:430 宽、`SizeToContent="Height" ResizeMode="NoResize"`、底 `{DynamicResource WindowBgBrush}`,`StackPanel Margin="24,20,24,18"` 内是 20x20 应用图标 + `Headline`(15 SemiBold `TextPrimaryBrush`)+`BodyText`(13 / LineHeight 21 / `TextSecondaryBrush`)+`LinkRow`(默认 Collapsed)+`CancelAction`+`PrimaryAction`(`Style="{StaticResource PrimaryButton}"`,即 App.xaml 里的品牌蓝主按钮)。两个静态入口:`Show(Window?, string, string, string? linkUrl = null, string? linkLabel = null)` 与 `Confirm(Window?, string, string)`(返回 bool);私有构造第五参 `confirm` 才把「取消」和「继续」露出来。
- **ownerless 场景**(托盘里跑检查更新,没有可见窗口):`ShowFor` 只在 `owner is { IsVisible: true }` 时 `Owner = owner`,否则改 `WindowStartupLocation.CenterScreen` —— 否则 `CenterOwner` 会落到任意位置。Esc 只在非 confirm 时关窗(`OnKeyDown`),confirm 型必须点按钮,避免把「按 Esc」读成一种答案。
- **链接外开**:`Link_Click` 用 `Process.Start(new ProcessStartInfo { FileName = _linkUrl, UseShellExecute = true })`,catch 里退化为 `Clipboard.SetText(_linkUrl)`。
- **契约变更 `src/FastTranslation.Core/UpdateChecker.cs:29`**:`UpdateCheckNotice` 由 `(string Title, string Body)` 变为 `(string Title, string Body, string? LinkUrl = null)` —— URL 不再内嵌进正文(弹框里裸链接不可点、还把正文撑长),改由窗口渲染成一个「打开下载页」按钮。文案同时改成用户话术:`UpToDate` 标题 `"已经是最新版本"`、正文 `"当前版本 {current} 已经是最新的,不需要更新。"`;`Failed` 正文去掉裸 URL,只留「连不上更新服务器…」+ 空行 +「失败原因已记录到 update.log。」,`LinkUrl` 传 `releasesPageUrl`。
- **两处 MessageBox 全部替换**:`src/FastTranslation.App/App.xaml.cs:221` `notice => NoticeWindow.Show(_mainWindow, notice.Title, notice.Body, notice.LinkUrl)`(原 `MessageBox.Show(..., MessageBoxImage.Information)`);`src/FastTranslation.App/UpdateWindow.xaml.cs:60` `installable = NoticeWindow.Confirm(this, "安装位置会变", "当前程序不在默认安装位置。安装新版本会把它装到默认位置,并保留现有设置。继续吗?")`(原 `MessageBoxButton.OKCancel` + `MessageBoxImage.Question`,取消后照旧 `OpenDownloadPage()` 并返回)。`git grep MessageBox -- src tests` 现在只剩注释。
- **深色标题栏 `src/FastTranslation.Platform.Windows/WindowChrome.cs`(新建)**:`Apply(Window)` 挂 `SourceInitialized`(HWND 出现之前 DWM 不接受属性),回调里 `SetDarkTitleBar(window, ThemePalette.Current == AppTheme.Dark)`;两值都写(强制浅色时要清掉系统深色框)。属性先试 `UseImmersiveDarkMode = 20`(Win10 2004+ 文档化),`DwmSetWindowAttribute` 非 0 再退 `UseImmersiveDarkModePre2004 = 19`(1809–1909 的未文档化号),不用做版本判断;整体 try/catch 吞掉 —— 纯外观,不值得为标题栏丢窗口。
- **五个窗口都挂上**:`MainWindow.xaml.cs:32` / `SettingsWindow.xaml.cs:27` / `OnboardingWindow.xaml.cs:17` / `UpdateWindow.xaml.cs:27` / `NoticeWindow.xaml.cs:21`,均紧跟 `InitializeComponent();`。
- **QA 钩子与截图脚本**:`App.xaml.cs:339` `FT_UITEST=notice-confirm` 走两按钮形状,`:351` `notice` / `notice-link` 用 `UpdateCheckNotice.UpToDate(new ReleaseVersion(1, 2, 0, 0))` / `Failed(UpdateChannel.ReleasesPageUrl)` 造数据,1800ms 后用 `Dispatcher.BeginInvoke` 再弹(直接调用会抢在主窗首次布局前)。新脚本 `tools/qa/shot-notice.ps1`:参数 `-ExePath -Theme -Kind(notice|notice-link|notice-confirm) -Out`,备份并注入 `settings.json` 的 Theme → 杀进程 → 设 `$env:FT_UITEST` 启动 → **每 300 ms 轮询**找弹框 → `PrintWindow` 存图 → finally 还原设置。
- **截图链路上踩的三个坑(都会让人误判成「样式没生效」)**:① UIA 的 `RootElement.FindAll(Children, ProcessId)` 在本机只列主窗一个,必须 `EnumWindows` + `GetWindowThreadProcessId` + `IsWindowVisible` + `GetWindowTextW` 非空且 ≠ 'Fast Translation'(弹框类名 `HwndWrapper[FastTranslation;;<guid>]`);② PowerShell 是 DPI-unaware,`GetWindowRect` 报 430x278 而真实窗口 753x487,`CopyFromScreen` 就只抓到左上角一块 —— 先 `SetProcessDPIAware()`;③ 主窗冷启动到钩子触发约 10 秒,固定 `Start-Sleep` 必然抢跑;且 `CopyFromScreen` 仍会把别的东西拍进来,最终改用 `PrintWindow(hwnd, hdc, 2)` 渲染窗口自身。另外 C# 片段里用 `Bitmap`/`Graphics` 必须 `Add-Type -ReferencedAssemblies System.Drawing`。
- **颜色证据(PrintWindow,普通 753x487 / 确认型 753x347)**:深色正文底 `#16191F`(WindowBg)、按钮行 `#202020`、链接与主按钮 `#4A82FF`、次文本 `#A3AEBF`、主文本 `#E8ECF3`;浅色 `#F7F9FC` / `#2562FF` / `#5C6B82` / `#1B2536`。行级直方图显示 y=5..44 整条是 `#202020`(深)/`#F3F3F3`(浅)、正文区 y≥60 才是 `#16191F`/`#F7F9FC` —— 标题栏与客户区是两种颜色,即 DWM 深色标题栏生效而窗口背景走 `DynamicResource`。(浅色 `#F3F3F3` 是系统原生浅色标题栏,不是设计令牌。)
- **测试**:`Notice_UpToDate_NamesTheRunningVersion` 断言标题 `"已经是最新版本"` 且 `Assert.Null(notice.LinkUrl)`;`Notice_Failed_OffersTheDownloadPageAsALink`(原 `Notice_Failed_NamesTheDownloadPageAndTheLog`)断言 `Assert.Equal(UpdateChannel.ReleasesPageUrl, notice.LinkUrl)` 且 `Assert.DoesNotContain(UpdateChannel.ReleasesPageUrl, notice.Body)`。构建时若被运行中的 `FastTranslation.exe` 锁 DLL(MSB3021/MSB3027),先 `Stop-Process -Force` 再编。

`dotnet build FastTranslation.slnx -c Release` 0 警告 0 错误;`dotnet test` 75 + 68 + 40 = **183 全绿**。


## v39 结果卡:更新弹窗跟随发起它的窗口

用户 m02559 原话:「不对啊,在设置页面里检查更新,出现的弹窗应该也是在设置页面的上层,现在是在使用页面的上层」——从设置页点「检查更新」,提示框却盖在主窗口上。

- **根因**:`src/FastTranslation.App/App.xaml.cs:221` 把 owner 写死成主窗:`notice => NoticeWindow.Show(_mainWindow, notice.Title, notice.Body, notice.LinkUrl)`。`UpdateCoordinator` 的回调本身不带「谁发起的」这一信息,于是无论托盘、主窗还是设置页发起检查,弹框一律认主窗为 owner —— 在设置页里点时,`CenterOwner` 自然把它摆到主窗中央。同类问题也在 `PresentUpdate`(发现新版本时开的 `UpdateWindow`)。
- **修法(不改回调签名)**:在**显示那一刻**解析 owner,而不是在订阅时绑定。新增 `App.xaml.cs` `private Window? DialogOwner()`,遍历 `Windows.OfType<Window>()` 取 `IsVisible && IsActive` 的那个;都没有(托盘发起、整个应用没焦点)就退 `_mainWindow is { IsVisible: true }`,再没有就返回 null,让 `NoticeWindow.ShowFor` 落到 `CenterScreen`。设置窗是主窗之上的模态窗,检查期间它一直是活动窗口,所以它天然成为 owner;设置窗先关了也不会有悬空 owner。
- **回调收口**:原来内联给 `UpdateCoordinator` 的 lambda 提成 `private void ShowNotice(UpdateCheckNotice notice) => NoticeWindow.Show(DialogOwner(), notice.Title, notice.Body, notice.LinkUrl);`,构造函数处直接传方法组 `ShowNotice`;`PresentUpdate` 的 owner 判断也换成 `if (DialogOwner() is { } owner)`,ownerless 分支保留原来的 `CenterScreen`。
- **设置窗本身也锚到主窗**:`SettingsWindow` 原本 `WindowStartupLocation="CenterScreen"` 且没有 Owner,是一个和主窗无关的顶层窗口(被主窗拖开后两者并列,弹框就更像「盖错窗」)。`ShowSettings()` 里改成:`_mainWindow is { IsVisible: true, WindowState: not WindowState.Minimized }` 时 `window.Owner = owner; window.WindowStartupLocation = WindowStartupLocation.CenterOwner;`,否则保持 `CenterScreen`(托盘进设置、主窗已收起的情况)。
- **QA 钩子 `FT_UITEST=notice-settings`**:先 `Dispatcher.BeginInvoke(ShowSettings)` 打开设置页,2600ms 后走**生产同一条** `ShowNotice`(`UpdateCheckNotice.UpToDate(new Core.ReleaseVersion(1, 2, 0, 0))`)。设置页是 `ShowDialog()`,但嵌套消息循环里 DispatcherTimer 照常 tick,不需要非模态旁路。
- **取证判据用 `GW_OWNER`,不是像素**:新脚本 `tools/qa/check-notice-owner.ps1`(参数 `-ExePath -Kind -Expect -Out [-TimeoutSec 40]`)用 `EnumWindows` 收本进程全部可见顶层窗口 → 标题:句柄,再对弹框调 `GetWindow(hwnd, GW_OWNER /*4*/)` 反查 owner 标题,并打印三个窗口的 rect 与中心点。不靠截图的原因:主窗、设置窗、弹框都居中,三者中心点**完全重合**(实测都是 1260,798),截图分不出 owner 是谁 —— 句柄关系才是唯一确定的证据。
- **证据**:`-Kind notice-settings` → `GW_OWNER -> '设置' expected '设置' => PASS`;回归 `-Kind notice`(主窗/托盘路径)→ `GW_OWNER -> 'Fast Translation' => PASS`。截图 `publish/qa-notice-over-settings.png`(875x1260,设置窗 rect 整块)留档,`publish/` 在 gitignore 内。
- **踩坑**:含中文的 `.ps1` 必须 UTF-8 **带 BOM**(PowerShell 5.1 按 GBK 解码会把 `param()` 第一行读坏,报「赋值表达式无效」);用脚本补 BOM 时若先按 utf8 读回再拼 BOM 字节会写出双 BOM,表现为行首多一个 `?` —— 写之前先剥掉开头的 U+FEFF。

`dotnet build FastTranslation.slnx -c Release` 0 警告 0 错误;`dotnet test` 75 + 68 + 40 = **183 全绿**。

## v40 发布 1.2.1:提示位置、卡片阴影、Fluent 提示框

先是用户报的两条缺陷(v37),随后用户自己提交了 v38/v39 两轮界面改动,合起来作为 1.2.1 发布。

- **v37 提示落在左上角**:`WriteBackAsync`(`src/FastTranslation.App/App.xaml.cs:457`)先 `_card?.Hide()` 再写回,失败时卡片已不可见,`ResultCardWindow.ShowNotice` 走兜底 `ShowAt(new ScreenPoint(80, 80), null)`。修法是在 `src/FastTranslation.Platform.Windows/OverlayWindows.cs` 的 `ResultCardWindow` 里记住 `_lastAnchor/_lastRect/_hasPosition`(`ShowAt` 赋值),提示复用该位置,只有从未显示过才退回鼠标位置。实测提示从 (117,112) 移到 (1202,724)。
- **v37 卡片阴影**:`FtTheme.CardShadow()` 挂在撑满窗口的根 Border 上,而卡片窗是 `AllowsTransparency` + `SizeToContent`,没有留透明边距,投影被窗口边界裁掉,只剩边框外 1–2px 深灰硬边(边框外实测 `(217,227,241)`,再外一格即背景)。与 docs/IMPL.md:290「工具条去阴影」同一决定,删掉 `CardShadow()`(结果卡 + `ImageResultWindow.cs:60`),只留发丝边框,并清掉无引用的 `PillShadow()`。提示卡同时加了 `RestoreFullChrome()`,折叠原文块后高度 112 → 88 DIP。
- **v38/v39(用户提交)**:`src/FastTranslation.App/NoticeWindow.xaml(.cs)` 取代 `MessageBox`,`UpdateCheckNotice` 增加 `LinkUrl`;`src/FastTranslation.Platform.Windows/WindowChrome.cs` 统一深色标题栏;更新提示按 `GW_OWNER` 归属到发起它的窗口。

**发布数据(2026-10-28)**

- 版本:1.2.1(`Directory.Build.props` `<Version>` 与 `installer/FastTranslation.iss:7` `AppVersion`),提交 `687fa5e94409cbc94258b9df48a1c928761b0fdc`。
- 产物:`FastTranslation-1.2.1-Setup.exe` **46764884 字节** `sha256 fcc38d7ee68f93a12994fe90293572a6cec6d8f1a977c0abd7aff5988a36a0d0`;`FastTranslation-1.2.1-portable.zip` **61754362 字节** `sha256 9217d1f4cb18d4b34a268a188634876295cbee76d7503e979fed27d8f3f92b63`(263 条目)。
- 主仓库 Release `426239503`:Setup 资产 `683724425`、portable 资产 `683724413`,均 `state: uploaded` 且 digest 与本地一致。
- 更新通道 Release `426239654`:资产 `683724944`(46764884 / 同 digest);清单 `version.json` 更新为 1.2.1,commit `1d29e1fbf95d04128e1bdc327de6a35ba13170f0`。
- 清单四顺位实测:Pages 仍是 1.2.0(`Cache-Control: max-age=600`),jsdelivr / gh-proxy / ghfast.top 已经 1.2.1;gh-proxy 取通道资产 `206`。因为 `UpdateChecker` 取「首个有效清单」,Pages 追上之前手动检查会显示「已是最新」,这是 b22 记录的已知残留风险。

**踩坑**

- `tools/qa/make-portable.ps1` 无 BOM 且含中文注释,PowerShell 5.1 按 GBK 解码导致 `$readme` 变成 null,报 `ParameterArgumentValidationErrorNullNotAllowed`;补 UTF-8 BOM 后正常(与 v39 记录的同一条规则)。
- GCM 里的 GitHub 令牌已失效/不存在(`CredEnumerate('git:*')` 只有一条无关的 `git.istudy.xin`),`git credential fill` 会挂起等待交互;本次由用户 Regenerate 后临时写入 `publish/.tok` 用完即删。
- 本机原先装在 `D:\SoftwareDownload\FastTranslation`(MSYS2 路径转换的历史遗留),而 `UpdateCoordinator.IsInstalledCopy()` 只认 `%LOCALAPPDATA%\Programs\FastTranslation`,不在默认目录就只能降级到下载页。为了让用户能真的体验一键更新,先静默卸载旧副本,再把 1.2.0 装到默认目录(设置与 Key 在 `%LOCALAPPDATA%\FastTranslation`,不受影响)。
- `dotnet build` 0 警告 0 错误;`dotnet test` 75 + 68 + 40 = **183 全绿**。
