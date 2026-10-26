# Fast Translation 实施日志(IMPL)

> 每任务:变更文件 / 命令 / 构建与测试输出 / 未验证项。按 PLAN.md 执行。

## 夜间执行总记录(2026-10-25 00:50 — 02:40)

### M0 最终批次(完成)
- 结论 **PASS_WITH_SCOPE**;全部数据、9 条产品级发现、蓝图 7 处缺陷:见 `tools/Probe/RESULTS.md`
- 关键新增发现(夜间批次):
  - Chromium 拖选进行中光标句柄变体 + Word 恒用自定义 I-beam 句柄 → 触发门控必须语义化(UIA FromPoint Document/Edit 兜底)→ 已实现于产品 `WindowsSelectionCapture.HandleGesture`
  - Chromium UIA 树懒激活 + 重建(元素矩形二次读取变空)→ 产品 `UiaSelectionReader` 单次快照 + 预热重试
  - SendInput DOWN/UP 不带 MOVE 时坐标被忽略 → 产品写回/兜底的按键序列无需坐标,不受影响;测量脚本已修
  - Word 模态对话框静默禁用一切交互 → 产品写回三重校验的实证依据
  - 写回生产算法直测:Notepad 3/3、Edge textarea 3/3、Word 2/3(8/9=89%,插入未测)

### 文档
- 新建 `docs/PLAN.md`(执行计划,M0 结论内建为 D1–D9 决策)
- 新建本文件 `docs/IMPL.md`(实施日志)

---

## T1 解决方案与基元 ✅
- 文件:`FastTranslation.slnx`(.NET 10 新格式)、`Directory.Build.props`(Nullable/TreatWarningsAsErrors)、四个 src 项目 + 两个 tests 项目、`app.manifest`(PerMonitorV2)
- Core 基元:`Primitives.cs`(ScreenPoint/ScreenRect/CapturedSelection/TriggerEvent/TriggerKind)、`TranslationTypes.cs`(Request/Result/Error/ITranslationProvider)、`Interfaces.cs`(ISelectionCapture/IResultPresenter/IWriteBack)
- 验证:`dotnet build` 通过

## T2 文本规范化与语言策略 ✅
- `TextNormalization.cs`:Cf/Ctrl/Pua 丢弃、空白折叠、IsMeaningful;`LanguagePolicy`:CJK≥50% → 英,否则 → 中;Resolve 产出 (source,target)
- 测试:TextNormalizationTests(8 用例)+ LanguagePolicyTests(4)全绿

## T2b 语言目录 ✅(待核)
- `LanguageCatalog.cs`:76 语言(code/ApiName 英文名/中文显示),ToApiName 兜底
- **未验证项**:与官方 92 语言清单 diff(beta 前必须,需官方页或真实 Key)

## T3 设置与密钥 ✅
- `Settings.cs`:AppSettings + SettingsService(JSON 往返、损坏回退默认)+ ISecretStore/内存实现
- `DpapiSecretStore.cs`:DPAPI CurrentUser(entropy=name);**.NET 10 中 ProtectedData 已内置,无需 NuGet(NU1510 实证)**——红线"唯一 NuGet"升级为"零 NuGet"
- 测试:SettingsServiceTests 往返/损坏 2 用例绿

## T4 Qwen-Mt Provider ✅
- `QwenMtProvider.cs`:单 user 消息、顶层 translation_options、Bearer、429 重试 1 次(400ms 退避)、超时归网络、错误分类;BuildUri 支持自定义 BaseUrl
- 测试:FakeQwenServer(HttpListener)6 用例:成功+协议形状(translation_options/target_lang/Bearer)、401、429 重试、坏响应、缺 Key、超长——**全绿**

## T5 翻译服务 ✅
- `TranslationService.cs`:LRU 500、单飞取消(新取消旧)、空输入/超长短路
- 修复两处真实缺陷(测试驱动):①作废请求的过期结果不得呈现(token 复查)②CTS Cancel-then-Dispose 竞态(只 Cancel,归属任务 Dispose)
- 测试:24 用例全绿(含取消时序)

## T6 选词协调器 ✅
- `SelectionCoordinator.cs`:状态互锁(Interlocked)、空捕获静默、终端进程不做兜底、排除名单、错误卡片
- 测试:4 场景(静默/兜底/终端/排除)绿

## T7+T8 鼠标监视器 ✅
- `NativeMethods.cs` + `LowLevelMouseHook.cs`:专用 STA 线程;回调零异常逃逸(M0 c000041d);系统阈值 SM_CXDRAG/双击;DOWN 时 I-beam 快照;点击链(1/2/3 击)判定;GA_ROOT 来源窗口
- 未验证项:自动化单测未做(需注入,人工冒烟)

## T9 剪贴板兜底 ✅
- `ClipboardService.cs`:序列号轮询(默认 300ms,50–5000 可配)、修饰键先行释放、备份/恢复、CLIPBRD_E_CANT_OPEN 重试
- 验证:构建绿;端到端兜底在真实应用中待 Key 联调时复核

## T10 UIA 读取器 ✅
- `UiaSelectionReader.cs`:FromPoint→6 层父链→TextPattern 快照(文本+矩形一次成型)、RuntimeId、IsWritable 判定(Edit/Document+IsEnabled)、400ms 预算、预热重试、LooksTextual 语义门控
- 未验证项:真实应用取词链路待明早人工冒烟(M0 探针已证明路径可行)

## T11 浮窗 ✅
- `OverlayWindows.cs`:TranslatePill(no-activate/1.5s 自动隐藏/DPI 缓存于 SourceInitialized)与 ResultCardWindow(深浅色基础样式、复制/替换按钮、Esc/失焦关闭、屏幕边界防溢出)

## T12 热键与托盘 ✅
- `HotkeyService.cs`:message-only 窗口 RegisterHotKey,字符串解析(Ctrl/Alt/Shift/Win+键)
- `TrayIconService.cs`:原生 Shell_NotifyIcon,左键开关取词、右键菜单(设置/暂停/退出);图标为软件渲染 GDI 位

## T13 写回服务 ✅
- `WriteBackService.cs`:三重校验(前台 HWND / RuntimeId / 归一化选区文本)、剪贴板暂存+恢复、Ctrl+V(无坐标按键序列)、Unicode 逐字兜底、失败改复制;终端/只读在协调器层拒绝
- 未验证项:真实应用替换实测(算法 8/9 已在 M0 验证,集成后需复测)

## T14 App 壳 ✅
- `SoftwareRender.cs`(ModuleInitializer,D1)、`App.xaml/.cs`(单实例 Mutex、组合根、托盘/热键/浮窗/协调器接线、UI 异常不崩)、`SettingsWindow`(语言/模型/热键/开关/Key/BaseUrl)、`OnboardingWindow`(输 Key→测试连接→完成)
- 冒烟(2026-10-25 02:30):publish/win-x64/FastTranslation.exe 启动 ✓ 进程存活 ✓ 引导窗显示 ✓ **空载私有工作集 35.2MB(<50MB)** ✓ 软件渲染生效 ✓

## T15 发布 ✅
- `dotnet publish -c Release -r win-x64 --self-contained` → `publish/win-x64/`(133MB,self-contained 非裁剪,含运行时)
- Inno Setup 脚本:未做(夜间无 Inno Setup;便携版可用)

## T16 验证 ✅(部分顺延)
- `dotnet build FastTranslation.slnx -c Release`:**0 警告 0 错误**(TreatWarningsAsErrors 开启)
- `dotnet test`:**30/30 绿**(Core 24 + Provider 6)
- 内存门槛:空载 35.2MB(带引导窗)✓;100 次捕获复测:顺延(需真实取词会话)
- 顺延项:真实 Key 联调、92 语言 diff、插入写回实测、8 小时稳定性、Inno Setup、THIRD_PARTY_NOTICES(零第三方代码复制,暂无需要)

## 真实 Key 联调(2026-10-25 上午,QIANWEN_API_KEY)✅

**Provider 级**(`tools/E2EKeySmoke`,走产品 QwenMtProvider):
- en→zh:OK 609ms("那只敏捷的棕色狐狸跳过了那只懒狗。")
- zh→en:OK 235ms
- 1870 字符长文本:OK 2250ms
- 坏 Key:正确归类 Authentication
- Key 经 DPAPI 存入本机(名称 qwenmt-api-key),往返校验 ✓;FirstRunCompleted=true(跳过引导)

**应用级端到端**(`tools/Probe/app-e2e.ps1`):
- publish 产物启动 → 托盘模式 ✓
- Notepad 真实拖选注入 → 触发 → UIA 捕获 → **qwen-mt-flash 真实翻译 688ms** → 结果卡显示译文+状态行+复制/替换按钮 ✓
- 焦点保持 Notepad ✓;**运行内存 25.7MB** ✓
- 卡片"复制"按钮(UIA Invoke)→ 剪贴板获得完整译文 ✓
- 遗留观察:复制后卡片隐藏的 UIA 可见性判定不敏感(窗口枚举仍见 1 个矩形),视觉行为待人工确认

**协议验证**:translation_options 顶层字段 + 单 user 消息 + Bearer,与文档一致;响应正常解析。

## 环境变更清单(夜间)
- 安装:.NET 10 SDK 10.0.402(winget,已授权)
- 临时电源 standby-ac 0 → 已恢复 1500s
- 进程清理:全部测试进程已关;用户文档零污染(UIA 复核);git 仍未初始化、零提交(遵嘱)

## UX 改造(2026-10-25 中午,用户反馈驱动)✅

用户方向:取消"抬起即译"(用户不可控),改为豆包式两段交互;补 ✕ 关闭(原卡片无法关闭)。

**新交互流**:
1. 拖选/双击/三击 → **工具条**(复制 / 翻译,no-activate,8s 自动隐藏,点外即隐)
2. 点"翻译" → 加载卡("正在翻译…")→ **结果卡**(译文 + 复制/替换原文/插入译文 + **✕**;点外关闭)
3. 工具条"复制" → 读取选区 → 复制原文(含剪贴板兜底路径,终端除外)
4. 热键 Ctrl+Alt+T 仍为直达翻译(用户显式意图)

**变更**:
- `SelectionToolbar`(替代 TranslatePill)+ `ResultCardWindow` 重构(加载态/✕/插入按钮/点外命中判定)
- `WindowsSelectionCapture`:选区触发一律只弹工具条;新增 `RawMouseDown` 事件(点外关闭的数据源);诊断日志以 `FT_DEBUG=1` 环境变量门控(默认零输出,隐私红线)
- `WriteBackService.InsertAsync`:三重校验(前台/文本匹配/折叠后退化)→ VK_RIGHT 折叠选区 → 粘贴 → 剪贴板恢复
- 设置窗移除"仅点击取词"(交互已固定两段式)

**实测修复的 4 个真实缺陷**:
1. UIA `NativeWindowHandle` 是子元素句柄 → 写回前台校验必失败;归一化到 **GA_ROOT**
2. 写回路径 `ConfigureAwait(false)` 切到 MTA 线程池 → WPF 剪贴板(需 STA)**静默挂起**;写回全链路保持调用方(UI/STA)上下文
3. 无 ✕ + no-activate 永不失焦 → 卡片无法关闭(用户实证);✕ + LL-hook 点外关闭双路修复
4. 测试脚本的 UIA Invoke 会提升自身前台 → 写回校验拒绝(测试伪影,真实点击无此问题;E2E 改用 SendInput 真实点击)

**端到端验证(SendInput 真实点击全程)**:
- 拖选 → 工具条 ✓;真实点击"翻译" → qwen-mt 真实译文卡片 ✓;✕ 关闭 ✓
- 真实点击"插入译文" → 三重校验 → 折叠 → 粘贴,译文出现在选区之后 ✓(焦点保持)
- 真实点击"替换原文" → 选中文字被译文替换 ✓
- 回归:30/30 测试绿;构建 0 警告 0 错误

## 手感修复 v3(2026-10-25 下午,用户反馈:翻不出来/按钮缺失/手感差)✅

**根因与修复**:
1. **有时翻不出来** —— ①协调器互锁:上一会话未结束就静默丢弃新请求(快速操作必踩)→ 改为**取消-替换**语义(新请求取消旧会话,永不丢弃);②点击时才读选区,选区可能已失效 → 改为 **mouse-up 后台预读+缓存**(M0 结论:抬起瞬间读取成功率最高),点击"翻译"直接用缓存,缓存空才重读→剪贴板兜底
2. **插入/替换按钮缺失** —— 可写判定只认控件类型;浏览器输入框/textarea 走"祖先链 Edit + ValuePattern.IsReadOnly"启发式,原生编辑器(Word/记事本等)的 Document 视为可写,浏览器页面 Document 不可写
3. **手感** —— 工具条定位改为**选区末行下方**(空间不足翻到上方,向上拖不再盖字)、屏幕工作区钳制、**鼠标悬停暂停自动隐藏**

**实测**(SendInput 真实点击):快速连续两轮"拖选→翻译"(第二轮在第一轮 API 在途时触发)→ 最终卡片为最新选区译文,无丢弃;浏览器 textarea 按钮出现(insert=True replace=True)、网页纯文本按钮正确隐藏;30/30 测试绿。
**E2E 脚本**:tools/Probe/rapid-e2e.ps1、browser-e2e.ps1(注意:测试窗口必须先置顶,否则拖选点到的是控制台,门控正确拒绝——这本身是门控有效的证据)。

## VSCode/编辑器按钮缺失修复 v4(2026-10-25 下午,用户截图反馈)✅

用户在类 VSCode 编辑器(Qoder/Electron)里翻译,卡片只有"复制"。三层叠加根因,逐层实测修复:

1. **剪贴板兜底捕获被一刀切标为不可写** → 兜底捕获(Ctrl+C 成功 = 编辑器)改为 `IsWritable=true`
2. **Electron/Monaco 的 UIA 形态特殊**:TextPattern 落点在 Document 或 Edit 不定;Edit 的 `ValuePattern.IsReadOnly` 撒谎(Monaco 自管编辑);**Chromium 元素无窗口(NativeWindowHandle=0)**导致进程名和来源句柄解析为空 → 修复:触发时的来源进程名与 GA_ROOT 句柄作为 hint 传入读取器;Electron 编辑器(code/qoder/cursor/trae)Document 视为可写、Edit 忽略 IsReadOnly;浏览器页面 Document 仍不可写
3. **UIA 读不到选区时写回直接拒绝** → 降级为"仅前台校验"(用户显式点击 + 选区仍在源窗口,粘贴依然安全;STranslate 同策略)

**VSCode 实测**(SendInput 真实点击全程):拖选 → 工具条 → 翻译(qwen 真实译文)→ **插入/替换按钮出现** → 点击"替换原文" → 选中文本被译文替换 ✓(REPLACED=True)。30/30 测试绿。
**注**:单词级低质翻译(如 "Button"→"阿顿")是 qwen-mt-flash 对无上下文短词的幻觉,非流程缺陷。

## 模态弹窗致死修复 v5(2026-10-25 下午,用户反馈:启动后什么都没有)✅

**根因**:写回校验失败路径用了 `MessageBox.Show`(模态)。托盘常驻应用只有一条 UI 线程——模态框一弹(常被压在其他窗口下不可见),**整条 UI 消息循环被堵死**:工具条、卡片、托盘菜单全部无响应,表现即"啥都没了"。用户杀进程前该实例一直是活的(pid 在,UI 死)。

**修复**:
1. 写回失败提示改为**非模态提示条**(结果卡复用,2.5s 自动消失,不抢线程)
2. 启动时钩子安装失败改为**托盘气泡通知**(Shell_NotifyIcon NIF_INFO)
3. 原则固化:常驻托盘应用的事件路径上**禁止一切模态框**(设置窗口内的提示除外,那里模态符合预期)

**验证**:快速双触流程正常;故意触发写回失败(UIA-invoke 前台伪影)→ 无模态窗出现、应用存活;失败后新选词 → 工具条 → 翻译 → 插入全部正常;30/30 测试绿。

## 启动死区与托盘关闭修复 v6(2026-10-25 下午,用户反馈)✅

**一、启动"宕机期"**——三个来源全部消除:
1. **UIA 客户端冷初始化**:首次划词的 FromPoint 要加载整套 UIA COM(1-2s,门控卡住)→ 启动时后台线程 `Warmup()` 预初始化
2. **浮窗 HWND 懒创建**:工具条/卡片首次显示才建窗口+首帧 → 启动空闲时 `EnsureHandle()` 预创建(样式+DPI 缓存同时就位)
3. **JIT**:publish 开启 `PublishReadyToRun`(预编译)
实测:首个真实划词从"卡 1-2 秒"→ **门控 6ms + 捕获 24ms + 工具条显示**(FT_DEBUG 时间戳)

**二、无法手动关闭**——托盘右键菜单原用 WPF ContextMenu 挂在 message-only 窗口上(不可靠,经常根本弹不出)→ 改为**原生 TrackPopupMenu**(CreatePopupMenu + KB135788 前台处理,托盘标准做法);设置/暂停取词/退出 全部走原生命令。左键切换暂停不变。

## ZCode 兼容 + 可写名单可配置 v7(2026-10-25)✅

用户在 ZCode 编辑器里无插入/替换——白名单漏了 "zcode"。修复:内置名单集中为 `UiaSelectionReader.EditableDocumentProcesses`(winword/notepad/wordpad/notepad++/devenv/code/qoder/qoder cn/cursor/trae/**zcode**/vscodium/windsurf),并新增设置项 **EditableProcesses**(逗号分隔进程名,追加到内置名单,设置窗可编辑)——不再靠硬编码猜测用户的编辑器。

## 白名单废除,全面乐观策略 v8(2026-10-25,用户:谁有闲工夫加白名单)✅

**新契约**:凡能取到文本 → 显示 复制/替换/插入(唯一硬拒绝:密码框、终端)。真实可写性在**点击时事后验证**:
1. 粘贴后按**来源窗口句柄**(非屏幕坐标,免疫遮挡)重读该窗口文档(Document 的 TextPattern + Edit 的 Value)
2. 译文真的落笔 → Replaced/Inserted
3. 没落笔(网页正文等只读面)→ 自动改为**复制译文 + 提示条**,绝不假装成功
4. UIA 读不到的编辑器(Monaco 类,剪贴板兜底捕获)→ 免验证乐观执行(与 STranslate 同策略)
- 移除 EditableProcesses 设置项与内置进程白名单;UIA 文本刷新滞后用 4×250ms 重试吸收
- E2E:浏览器页面/输入框按钮全出现(True/True);只读页面点替换 → 安全降级、应用存活;可编辑目标真实落笔(地址栏实测中文粘贴成功,证明粘贴管线);记事本/Word 此前已验

**遗留**:个别应用的首拖边界/焦点细节按真实使用反馈微调;自动化测试受 Win11 记事本标签会话恢复污染,产品行为以人工验证为准。

## 失败提示改版 v9(2026-10-25,用户指定文案)✅

写回失败(校验中止/粘贴未落笔/发送失败)一律**不碰剪贴板**,按用户文案弹非模态提示:
- 替换失败:「当前内容不能替换诶,换一个试试」
- 插入失败:「当前内容不能插入诶,换一个试试」
剪贴板唯一写入点 = 粘贴暂存(成功路径)与用户原剪贴板恢复;`AbortedOrCopy` 改名 `Aborted`,`CopiedInstead` 不再产生。

## 主窗口 v10(2026-10-25,用户:不能只当个小程序)✅

- **主窗口**(启动即显示):手动输入翻译(输入框+翻译按钮+Ctrl+Enter)、目标语言选择(持久化)、结果区+复制译文、取词开关(同步托盘提示)、设置入口、API Key 状态(未配置时显示配置按钮)、热键/模型状态栏
- **生命周期**:点 ✕ = 隐藏到托盘(应用继续划词服务);托盘菜单"退出"= 真退出;**托盘左键 = 呼出主窗口**(原左键切取词改到菜单/主窗口开关)
- **二次启动**:不再静默退出——通过命名 EventWaitHandle 唤起已运行实例的主窗口
- 修复:XAML `IsChecked=True` 在 InitializeComponent 期间触发 Checked → 构造未完成空引用 → 启动中断只剩托盘(事件处理器加初始化护栏)
- E2E:窗口显示 ✓ 手动翻译真实译文 ✓(今天,那只敏捷的棕色狐狸…) 关闭到托盘(进程活+窗口隐)✓ 二次启动唤起 ✓

## 应用图标 v11(2026-10-25,用户提供原图)✅

- **图标源**:`assets/icon/icon-master.jpg`(用户提供,1280×1280,蓝斜体 FT + 闪电切痕,奶白泛蓝底,自带 ~20.8% 圆角、角外白底)
- **处理管线** `assets/icon/process-icon.ps1`:JPG → 32 位 ARGB 画布 → 252px 抗锯齿圆角透明蒙版(实测原图圆角 ~266px,蒙版略小保证白边零残留)→ HighQualityBicubic 缩出 256/64/48/32/24/16 → PNG-in-ICO 打包 `FastTranslation.ico`(~109KB)
  - 坑:JPG 是 24 位图,直接 LockBits 写 alpha 会在 UnlockBits 时被丢回 24 位——必须先画到 32 位画布再写
- **接线**:
  - csproj:`ApplicationIcon`(exe/资源管理器)+ `Resource`(WPF pack URI `/FastTranslation.ico`)
  - MainWindow/SettingsWindow/OnboardingWindow XAML:`Icon="/FastTranslation.ico"`
  - 托盘:`TrayIconService(byte[]? icoBytes)` 注入 ICO 字节,`CreateIconFromResourceEx` 按 SM_CXSMICON 取小号,失败回退原手绘"译"字标;App 装配根 `LoadIconBytes()` 从资源流读
- **验证**:Release 构建 0 警告 0 错误;`ExtractAssociatedIcon` 确认 exe 嵌的是自定义图标(非通用 exe 标);启动冒烟 6s 存活、主窗口正常创建(XAML Icon 资源解析通过);回归 30/30(Core 24 + Provider 6)

## UI 设计系统重构 v12(2026-10-25,ui-ux-pro-max)+ 打包发布 ✅

**设计系统**(App.xaml 令牌 + 模板,ui-ux-pro-max 查询:Flat Design / Fluent 主题 / 隐式样式):
- 色板与图标同源:品牌蓝 #2562FF(主)/#40A9FF(强调),奶白底 #F7F9FC,白卡面,文字三级 #1B2536/#5C6B82/#8B97A9,发丝线 #E4EAF3
- 控件模板全覆盖:Button(幽灵/品牌主/圆✕/胶囊)、TextBox/PasswordBox(聚焦光环)、CheckBox(品牌对勾)、开关 ToggleSwitch、ComboBoxItem(去 Fluent 勾选符号,品牌高亮)
- WPF `ThemeMode="Light"`(.NET 10 Fluent);字体 Segoe UI + Microsoft YaHei UI;150-200ms 过渡

**界面重构**:
- 主窗口:头部(小图标+开关式取词切换+设置)/输入卡片(占位提示、语言/模型徽章/复制/翻译按钮内嵌)/结果卡片/状态栏
- 设置窗:字段标签体系、热键注册失败改窗内红字(去 MessageBox)、按钮右下主次分明
- 引导窗:三步编号(品牌蓝圆点),已配置 Key 则永不弹(App.xaml.cs 启动时检测并置 FirstRunCompleted)
- 取词工具条(代码级):白色悬浮胶囊(复制 | 分隔线 | 翻译品牌蓝),PillShadow
- 结果卡(代码级):头部原文摘要+✕(次级灰)、加载 spinner 旋转动画、底部状态+按钮组(插入/替换幽灵、复制主键),错误红字,提示橙字,CardShadow+HairStrong 边框
- **约束**:全部 UIA Name(复制/翻译/替换原文/插入译文/✕)与 x:Name 原样保留,E2E 脚本零改动兼容

**视觉 QA**(tools/qa/shot-*.ps1,截图+视觉模型评审迭代):主窗 9/10(修:下拉勾选符号与文字重叠、提示对比度)、设置窗通过、工具条 9/10(修:悬浮阴影 Performance→Quality 偏置、内边距)、结果卡 9/10(修:按钮热区加高、✕/原文对比度、边框)。坑:无 BOM PS1 中文字面量乱码 → QA 脚本全部 ASCII+码点构造中文;脚本误把主窗口当工具条 → 匹配排除含"设置"按钮的窗口。

**验证**:回归 30/30;私有工作集 31MB(PerformanceCounter 'Working Set - Private',与 M0 同口径;注意 PrivateMemorySize64=commit 口径是 70MB,勿混);划词→翻译→卡片全流程实测(531ms 真实译文)。

**打包发布**(publish/dist/):
- FastTranslation.exe 单文件自包含 64MB(PublishSingleFile+压缩,内置 .NET 10,零依赖)
- FastTranslation-1.0.0-Setup.msi 60MB(WiX v6;**v7 需接受 OSMF 收费协议,勿升**):per-user 免管理员,%LOCALAPPDATA%\Programs\FastTranslation(VS Code 同款;与数据目录 %LOCALAPPDATA%\FastTranslation 分离),桌面+开始菜单快捷方式,MajorUpgrade,卸载清目录留数据(RemoveFolder)
- 闭环实测:静默装(注意 Git Bash 会改写 msiexec 开关,必须用 PowerShell Start-Process)→ 装后启动 ✓ → 卸载 ✓ 目录清空数据保留 ✓ → 装回正式版
- README.txt 分发说明

## 托盘图标修复 v13(2026-10-25 晚,用户:托盘没显示 logo)✅

- **根因**:`IconFromIcoBytes` 把整个 ICO 容器喂给 `CreateIconFromResourceEx` —— 该 API 只吃单张图标图像(且 `LookupIconIdFromDirectoryEx` 要 RES 节不是文件字节),返回 0 → 静默回退手绘"译"字标(用户截图中的蓝底"译")
- **修复**:手动解析 ICO 目录,选最接近 SM_CXSMICON 的条目,取其 PNG payload 过 `CreateIconFromResourceEx`(PowerShell 实证 entry0(16×16) → HICON 有效;PNG payload 该 API 支持,旧回退路径本就如此)
- 捷径方案 `new System.Drawing.Icon(stream)` 不可用:平台项目零 WinForms/System.Drawing 依赖
- 重发布 + 重装 MSI,托盘飞出面板实拍确认 FT logo 正确显示
- 同包已含桌面快捷方式修复:广告快捷方式 → 普通 .lnk(Target=[!File]),图标取 exe 内嵌资源;MSI 层加 Icon/ARPPRODUCTICON(设置→应用列表也有图标)

## 简洁艺术风改版 v14(2026-10-25 深夜,frontend-design skill;用户三项反馈)✅

1. **logo 清晰度**:应用内不再用 ICO 的 32px 小帧(被 175% DPI 拉糊)——csproj 加 `icon-master.png`(1280 透明底母版)Resource,头部/引导窗直接高倍缩放,标题栏仍走 ICO(256 帧);另出 `assets/icon/FastTranslation-4K.png`(4096px,6.3MB,展示用;注明:源图 1280,上采样平滑不增细节)
2. **布局"桌面与批注"**:输入区=安静白纸(卡内只有文本框,占位提示),工具行(语言下拉+模型徽章+复制/翻译)浮在桌面上不再被输入框压迫,译文=引文式排版(左侧 3px 闪电渐变竖条 #40A9FF→#2562FF 全页唯一强调元素,贴文本高度,ScrollViewer 内 VerticalAlignment=Top)
3. **下拉框点不开根因**:ThemeMode=Light 的 Fluent ComboBox 模板与隐式 Style 打架(截图可见 Aero2 老灰色混入)→ **完全自绘模板**(ToggleButton 绑 IsDropDownOpen + PART_Popup + 圆角 8 + 品牌高亮 + 箭头旋转 + FocusVisualStyle=null 去虚线框),行为不再依赖主题
- **QA 方法论升级**(用户在用电脑,禁止 SendInput 注入):`FT_UITEST=dropdown` 环境变量门控的应用自展开钩子 + UIA 直读 ExpandCollapseState(**Expanded** = 交互修复实锤)+ **PrintWindow 离屏截图**(不抢前台不覆盖用户屏幕);CopyFromScreen 会截到用户正开的前台应用,GetForeground 抢前台在 Win11 受限
- 回归 30/30;重发布重装完成

## v15 三连修(2026-10-26 凌晨,用户实测三项反馈)✅

1. **下拉框"点了没反应"真根因**:`Directory.Build.props` 的 `InvariantGlobalization=true`(M0 体积优化)让 WPF 模板内 Binding 的 culture 解析抛 `XamlParseException`(被全局异常处理器静默吞掉)→ **任何** ComboBox 模板(Fluent/Aero2/自绘)的 Popup IsOpen 绑定全灭 → 弹窗从未打开过(此 bug 从项目第一天就存在)。修复:①去掉 InvariantGlobalization;②ComboBox 自绘模板彻底零 Binding(Popup 用 `IsDropDownOpen` Trigger 驱动、点击用 `FtComboBox` 子类代码驱动)双保险。验证:popup hwnd 真实出现 + 异常日志清零(此前每次启动 126 条)。**注意:WPF 项目永远别开 InvariantGlobalization**
2. **取词工具条豆包化**:白色不透明全圆角胶囊(Ø17)+ 线性图标(1.5px Lucide 风复制/闪电)+ 文字、紧凑内边距、柔和投影;图标+文字内容需显式 `AutomationProperties.SetName` 保 UIA 契约(平台项目要 `global::` 前缀防命名空间遮蔽)。视觉评审 8.5/10 与豆包高度一致
3. **内存 125MB → 38-51MB**:单文件自包含的 bundle 程序集按私有内存映射(125MB)→ **改文件夹发布**(磁盘映射,不占私有页)+ `System.GC.HeapHardLimitPercent=20`;38/51 两态由系统工作集修剪决定(同二进制先后测得两值,非配置差异)。MSI 改 250 文件夹安装(一文件一组件自动 GUID);便携版改 zip(58.7MB)。回归 30/30

## v16 下拉框样式回退修复(2026-10-26)✅

- **根因**:WPF 隐式样式**精确类型匹配**——`TargetType="ComboBox"` 的样式不会作用于子类 `FtComboBox`(v15 为绕开 Binding culture 问题引入),控件回退到系统默认模板 → "UI 退回去了"
- **修复**:样式改 `TargetType="{x:Type local:FtComboBox}"`(App.xaml 加 xmlns:local);ControlTemplate 的 TargetType 保持基类即可
- **QA 教训**:popup-shot.ps1 枚举窗口必须按 pid 过滤且用"最小宽度候选"选弹窗,否则会截到用户正在用的窗口或主窗口;验证截图前先确认尺寸符合预期(弹窗 ~315×569 物理)
- 全链路重验证:bin ✓ → 干净重发布(ft-folder 250 文件)→ MSI 重建 → 重装 → 安装版弹窗实拍 = 品牌样式完全生效(圆角/边框/投影/淡蓝选中高亮);回归 30/30

## v17 下拉框点击失效修复(2026-10-26 凌晨)✅

- **根因**:模板里 PART_Toggle ToggleButton 被 Fluent 主题的默认 ToggleButton 样式影响,**尺寸坍缩为 0×0**——点击全部漏到底层背景 Border,Click 永远不触发(v15 模板重写时引入,此前被 invariant 大 bug 掩盖,样式修复后第一次暴露)
- **修复**:ToggleButton 显式 `HorizontalAlignment/VerticalAlignment="Stretch"`(本地值胜过主题样式 Setter)
- **验证方法论**:①`InputHitTest`(WPF 命中测试权威 API)探针:修复前命中链 `Border←Grid`(漏过 ToggleButton),修复后 `Border←ToggleButton←Grid` ✓ + toggle 尺寸 180×33 ✓;②日志同时捕获到用户真实点击事件到达 combo(证实事件路由正常,只是 0×0 接不住);③SendInput 注入测试不可信:多显示器虚拟桌面坐标公式要扣 SM_X/YVIRTUALSCREEN 原点,且用户正在用电脑(前台锁定 + 真实鼠标并发移动污染 GetCursorPos 读数)
- **插桩保留**(FT_UITEST 门控,生产无影响):FtComboBox.OnToggleClick/OnApplyTemplate 日志、hittest/dropdown/overlays/click 自测钩子
- 全链路:重发布→MSI→重装→安装版探针验证通过;回归 30/30

## v18 细节打磨(2026-10-26)✅

1. **输入区光标与占位提示对齐**:InputHint 的 Margin 从 (17,15) 改为与 InputBox Padding 完全一致 (14,12),字号 13→14.5 与输入文字相同——闪烁光标与提示文字精确同位
2. **工具条去阴影**:删除 PillShadow(阴影被窗口边界裁切出现硬边角);纯白底铺满全圆角胶囊 + 发丝线,豆包同款素净;实拍验证零阴影残留
- 全管线:重发布→MSI→重装;回归 30/30;便携 zip 同步

## v19 对齐与工具条真修(2026-10-26 凌晨,用户截图打脸)✅

1. **光标错位真根因(v18 修错方向)**:WPF TextBoxBase **自带**把 Padding 应用给内部文本视图,模板里 `PART_ContentHost Margin=Padding` 变成**双倍 padding** → 光标 x=29.7(应为 ~15)、下移一行感。实验二分(Margin=BorderThickness → 数值不动;Margin=0 → 光标 16,12)锁定。hint Margin 补 border 1 → (15,13)。**CopyFromScreen 实拍光标紧贴"输"字左侧同行**(PrintWindow 抓不到系统 caret,之前"验证通过"是假阴性)
2. **工具条按钮灰底**:PillGhost 等幽灵样式**漏设 Background** → Fluent 主题默认灰底透过 TemplateBinding 显形。修复:TemplateStyle 永远显式设 Background(transparent counts)。实拍:纯白胶囊+无边框图标按钮,豆包一致
- 教训: PrintWindow 截图验证 UI 时,凡是系统级 overlay(caret/拖拽指示)都不可见——必须 CopyFromScreen + 前台;视觉"通过"结论必须基于能看清问题的截图

## v20 工具条形状(2026-10-26)✅

- 取词工具条外形由全圆角胶囊(Ø17)改为豆包同款**方框圆角型**(CornerRadius 10),白底+发丝线不变;实拍验证
- 全管线:重发布→MSI→重装→回归 30/30→便携 zip 同步

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
- **验证**:`dotnet build` 0 警告 0 错误;`dotnet test` 24 + 36 + 6 = **66/66**;空载 PrivateWS 稳态 35.4MB(启动瞬间 50.8MB,仍在 50MB 红线内);MSI 装后 250 文件、exe SHA256 与构建产物一致、桌面与开始菜单快捷方式存在、卸载项为 `Fast Translation`;便携包 263 条目 61.6MB
- **UIA 脚本化交互验证**(新增 `tools/qa/` 下四支):`settings-verify.ps1` 校验窗口内按钮/关于文本/截图;`settings-dump.ps1` 全后代元素转储;`settings-interact.ps1` **点击级**验证排除应用增删 + 开机启动开关(读写 `HKCU Run` 与 `settings.json` 双向核对,结束时备份还原);`verify-install.ps1` 校验安装态。脚本必须 ASCII-only(中文用 `[char]0xXXXX` 拼),否则无 BOM 的 PS1 在 GBK 下乱码
