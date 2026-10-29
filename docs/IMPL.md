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
- **提交身份改 GitHub noreply**:原 `user.email` 是 gitee 的 `（已移除）`,GitHub 不认(贡献图不归属)。用 `git filter-branch --env-filter` 把 5 个提交的 author/committer 统一改成 `LYW206817 <210836236+LYW206817@users.noreply.github.com>`(未推送前重写,无副作用);并新增 `.gitattributes`(`* text=auto` + png/jpg/ico/zip/msi/wixpdb 标 binary)消除批量 LF→CRLF 警告
- **连接方式踩坑**:本机 `https://github.com` **TCP 443 连不通**(api.github.com 可通、github.com 的 443 连接被 reset),但 SSH 22 通畅、`~/.ssh/id_rsa` 已授权该账号 ⇒ remote 用 `git@github.com:LYW206817/FTranslation.git`,并写入仓库级 `core.sshCommand`(指定 key 与 known_hosts 绝对路径,因为 MSYS2 的 `$HOME` 是 `/c/Users/示例用户`,中文路径在 ssh 里会乱码导致 `Could not create directory '/c/Users/\301\365\323\300\315\376/.ssh'`)

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

## v41 自动更新:自定义安装目录不再被挪走,也不再被误判成便携版

用户 m01641 原话:「这个自动更新不允许我们选择安装路径,是全自动的,自动没关系,不能更改原先安装路径」。

**先测事实,再看代码。** 本机把 1.2.0 装进 `D:\FT-Custom-Test`,再用更新器原样的参数(`/SILENT /SUPPRESSMSGBOXES /NORESTART`,不带 `/DIR`)跑 1.2.1:

- `publish/custom-upgrade.log` 里 `Dest filename: D:\FT-Custom-Test\FastTranslation.exe`,`[Run]` 启动的也是这个目录;
- 升级后 ARP 变成 `v1.2.1 | D:\FT-Custom-Test\`,默认目录始终没有出现。

⇒ `UsePreviousAppDir=yes` 确实沿用了用户选的目录,**静默安装本身不会挪位置**。真正的缺陷在应用侧的判定:

- `src/FastTranslation.App/UpdateCoordinator.cs` 的 `IsInstalledCopy()` 把「默认目录 `%LOCALAPPDATA%\Programs\FastTranslation`」写死,别的目录一律视为便携版;
- `src/FastTranslation.App/UpdateWindow.xaml.cs` 于是在点「立即更新」时弹「安装位置会变 / 会把它装到默认位置」的确认 —— 这个说法与上面的实测相反,而且把自定义目录的用户直接推到下载页。

**修法**

- 新增 `src/FastTranslation.Platform.Windows/InstallLayout.cs`:`UninstallKey = "{8B1F2C34-7A6D-4E58-9C21-5F3D8E7A1B60}_is1"`、`ReadRegisteredDirectory()`(HKCU / HKLM × 64/32 视图读 `InstallLocation`)、`CanSelfUpdate(exeDir, registeredDir, protectedRoots)`、纯函数 `IsSameDirectory` / `IsUnder`。判据从「是不是默认目录」改成「是不是安装记录里的那个目录」;便携包没有安装记录 ⇒ 仍然降级到下载页。
- `ProgramFiles` / `ProgramFilesX86` / `System` / `Windows` 四个根视为不可自我替换 —— `PrivilegesRequired=lowest` 没有提权通道,在那里静默安装会在应用已经退出之后失败。
- `UpdateInstaller.LaunchInstaller(setupPath, pid, installDirectory, out error)` 增加 `/DIR`:静默模式看不到文件夹页,显式重复当前目录才是「不改路径」的硬保证;`BuildInstallerArguments` 独立成公开方法以便测试,含空格的路径会加引号。
- `UpdateWindow` 删掉那段与事实不符的确认;便携版的降级文案仍由 `_selfInstallable` 负责。`NoticeWindow.Confirm` 保留,只由 UI 探针分支使用(`FT_UITEST=notice-confirm` 的文案改为中性说法)。

**验证**

- 单元测试 `tests/FastTranslation.Platform.Windows.Tests/InstallLayoutTests.cs`(9 例:去尾分隔符/大小写、reject 不同目录、便携无记录、Program Files 及其兄弟目录的边界)与 `InstallerArgumentsTests.cs`(3 例);`dotnet test` 75 + **83** + 40 = **198 全绿**,`dotnet build` 0 警告 0 错误。
- 真机探针 `publish/InstallProbe`(net10.0-windows 控制台,ProjectReference 到 Platform.Windows)对着真实注册表输出:`registered = D:\FT-Custom-Test\`、`canSelfUpdate(D:\FT-Custom-Test) = True`、`canSelfUpdate(默认目录) = False`、`canSelfUpdate(publish\ft-folder) = False`、`canSelfUpdate(C:\Program Files\FastTranslation) = False`。
- 安装级验证 `publish/dir-args-test.ps1`:T1 在 `D:\FT-Custom-Test` 已装 1.2.1 的基础上用 `/DIR=D:\FT-Custom-Test` 静默升级 → 目录内变成 `1.2.2+cfe2fc6`,默认目录仍 absent;T2 全新静默安装 `/DIR="D:\FT Test Dir"`(带空格、带引号)→ 落在该目录。测试目录随后卸载删除,本机恢复为默认目录的 1.2.2。
- 顺带发现:静默安装的 `[Run]` 会把应用拉起来,脚本里 `Start-Process -Wait` 会一直等到应用退出 —— 判完成要轮询 `ProductVersion`,不能用 `-Wait`。

**顺带修掉的**`tools/qa/make-portable.ps1` 缺 BOM(`$readme` 变 null)已在 1.2.1 那次提交里补上。

## v42 发布记录:1.2.2

- 主仓库 Release `426263015`,tag `v1.2.2` → commit `8601dddbd56b718fdb3bab44a9d709194dd8e3e3`;资产 `FastTranslation-1.2.2-Setup.exe` id **683817104**(46766839 字节 / `sha256:fc29736278a635e71945e29d8766a4a40c95e9f2a97803c521f68d8e90540d3e`)、`FastTranslation-1.2.2-portable.zip` id **683817237**(61757746 字节 / `sha256:279df77f0931311d71d0deb416188ce421e8044034000f21a8d445a2b6317f7b`)。
- 更新通道 `LYW206817/FTranslation-Updates` Release `426263182`,资产 id **683817635**(同一份 Setup.exe),三个资产均为 `state: uploaded` 且 digest 与本地一致。
- `version.json` 由 blob `bf2f85dea1aab1ea1e6164665c006f1d9b9f6ffb` 更新为 commit `6ef0b2f7e8a92c519772cdb612e44b29a9e3aedf`(version 1.2.2 / size 46766839 / sha256 fc297362… / url 指向通道的 v1.2.2 资产)。
- 清单四顺位实测:gh-proxy 与 ghfast.top 的 raw 立即 1.2.2;GitHub Pages 在 600 秒缓存过期后于 12:33 变 1.2.2;**fastly.jsdelivr 到 12:40 仍是 1.2.1**("@main" 的 CDN 缓存明显更久)。因为 `UpdateChannel.ManifestUrls` 里 Pages 排第一、这两个 raw 代理排在后两位,不影响正常检查;只有「Pages 挂掉且 jsdelivr 还在缓存旧清单」这种叠加情况下才会短暂读不到新版本,属于已知限窗,遇到再考虑改成「多源取最高版本」。
- 通道资产可用性:直连 `github.com` 返回 302 后不可达;`https://gh-proxy.com/https://github.com/…/v1.2.2/FastTranslation-1.2.2-Setup.exe` 返回 206、约 138 KB/s ⇒ 下载仍然只能走代理。

## v43 账号基座:自建后端 + 客户端登录链路(阶段0)✅

**为什么这样改**

v1.0 的设计(spec:12 / spec:29 / spec:45)刻意把「账号体系、代理后端、计费」排除在外,只做 BYO Key。用户已经推给朋友用之后要「知道有多少注册用户、免费版与商用版分档、开发者与用户都能看到消耗」,这套前提就作废了 —— 决策与取舍记在 `docs/superpowers/specs/2026-10-28-account-and-billing-design.md`,其中 D5 明确把 spec:743 那条「必须先取得阿里云对代理/转售/代付的书面确认」作废(该行已加删除线)。本版只做**阶段 0 账号基座**:能注册、能登录、能看到自己的额度与用量;翻译仍走用户自己的 Key,**没有任何文本经过本机以外的服务器**,所以这一版没有合规风险,也不影响现有用户。

**后端 `server/FastTranslation.Server`(.NET 10 + SQLite,零第三方业务框架)**

- 只有两个直接依赖:`Microsoft.NET.Sdk.Web` 框架本身 + `Microsoft.Data.Sqlite` 10.0.0。不引 EF、不引 ORM、不引 Identity、不引日志框架 —— 与客户端「最小实现 + 单测」的约定保持一致。
- `Domain/Plan.cs` 是配额的单一事实源:`Free = 10_000`、`Light = 50_000`、`Standard = 200_000` 字符/月,免费档额外 `FreeDailyChars = 2_000` 字符/天,单账号 `MaxDevicesPerAccount = 3`。`Domain/QuotaPolicy.Evaluate()` 是纯函数(先查月再查日,负数直接抛),所以额度语义可以脱离数据库测边界 —— 这是后面阶段 2 网关复用的同一份判定。
- 认证不引 JWT 库:`Auth/TokenService.cs` 手写 HS256(iss/aud/sub/email/plan/iat/exp/jti),签名与 base64url 解码**全部包在 try/catch(FormatException)** 里 —— 早期版本漏了这层,畸形 token 会变成 500 而不是 401。刷新令牌是 32 字节随机值的 SHA256,**一次性消费**,重放直接 401。
- 密码 `Auth/PasswordHasher.cs` 用 PBKDF2-SHA256 / 210,000 轮,格式 `pbkdf2-sha256$iterations$salt$hash`,校验用 `CryptographicOperations.FixedTimeEquals`。
- 注册可要求邮箱验证码(`Auth/VerificationCodeService.cs`:6 位数字、`VerificationCodeMinutes = 10`、`VerificationResendSeconds = 60`、`VerificationMaxAttempts = 5`)。开发环境 `RequireEmailVerification = false`,于是 `POST /api/v1/auth/code` 回 `verification_disabled`,客户端据此把「验证码」输入行整行隐藏 —— 不要把它当成错误。
- 登录失败一律 `401 credentials_invalid`,不区分「邮箱不存在」与「密码错」,避免账号枚举。
- 限流只作用在 `/api/v1/auth` 组上且按 **RemoteIpAddress**(`Api/RateLimiting.cs`)。**配额绝不能按 IP 限** —— 校园网与公司网 NAT 会把核心用户整片误杀(设计 §7.4)。
- `Storage/AccountStore.cs` 集中全部 SQL;三张用量表:`usage_monthly(user_id, month)`、`usage_daily(device_id, day)`、`usage_events`(逐条明细,供后续账单核对)。SQLite 开 WAL + `busy_timeout=5000` + `foreign_keys=ON`。
- `Program.cs` 里 `ServerOptions` 必须用 `AddSingleton(sp => sp.GetRequiredService<IConfiguration>().GetSection(...).Get<ServerOptions>())` 取,**不能**在 `Build()` 之前从 `builder.Configuration` 直接读 —— 否则 `WebApplicationFactory` 的内存配置覆盖不到,集成测试会连到真实数据库。
- 生产环境 `Server:SigningKey` 少于 32 字节直接抛异常拒绝启动(而不是悄悄用弱 key);Development 下才回退成随机 32 字节。

**客户端**

- 全部网络代码在 `src/FastTranslation.Core/Account/`(无 WPF 依赖,可单测):`AccountApiClient` / `AccountSessionStore` / `DeviceIdentity` / `AccountTypes`。`AccountApiClient` 承诺**永不抛异常**,所有失败都映射成 `AccountErrorCode`;传输层异常(DNS/拒绝/TLS/超时)那一层是靠 `SendAsync` 返回 `null` 实现的,不是靠调用方 catch。
- **wire 必须是 camelCase**:服务器 DTO 是 `record RegisterRequest(string Email, ...)`,System.Text.Json 服务端默认**大小写敏感**,所以客户端的 `JsonOptions` 必须带 `PropertyNamingPolicy = JsonNamingPolicy.CamelCase`。漏了这一条的后果是注册/登录稳定 400,而单元测试如果只断言路径不查请求体是发现不了的 —— 所以测试里显式断言了 `"code":"123456"`(注意这个字段的 wire 名就是 `code`,不是 `verificationCode`)。
- 会话(access + refresh)存 `ISecretStore`(`AccountSessionStore.SecretName = "account-session"`,Windows 下走 DPAPI),**不进 settings.json**:refresh 令牌是 90 天凭证,不该以明文躺在设置文件里。access 令牌提前 60 秒视为过期,避免边界上刚好失效。
- `AccountCoordinator` 用 `SemaphoreSlim` 保证并发请求只会刷新一次令牌;只有 `Unauthorized`/`InvalidResponse` 才清会话登出,网络类错误保留会话等下次启动重试 —— 否则用户坐一趟地铁就被登出。
- 入口:托盘菜单「账号」(id 1006,`TrayIcon.AccountRequested`)与主窗 footer 的「账号」按钮,都打开同一个非模态 `AccountWindow`(已开则前置复用)。设置页「服务」卡片新增**账号服务地址**输入框,指向自建或测试服务器;留空走内置默认。
- 会话恢复在启动时后台跑(`RestoreAccountAsync`),失败只 `Trace` 不弹窗。

**验证**

- `dotnet build FastTranslation.slnx -c Release` → **0 警告 0 错误**;**`dotnet test` → 112 + 83 + 92 + 40 = 327 全绿**(阶段 0 之前基线 290,新增 37 个)。
- 新增测试项目 `tests/FastTranslation.Server.Tests`(92 例):`QuotaPolicyTests`(月尽/日尽/两者皆尽报月/付费无日限/刚好用尽/负数抛)、`PasswordHasherTests`(畸形串 5 种)、`TokenServiceTests`(篡改 payload / 他钥签名 / 过期 / `alg=none` / 畸形输入 6 种)、`AccountStoreTests`(真 SQLite 临时库,含设备复用、refresh 一次性与过期、验证码计数、`ReadAccountMetrics`)、`AuthEndpointTests`(17 个 HTTP 端到端,用 `WebApplicationFactory` 覆盖注册/重复邮箱/统一登录失败/伪造令牌/refresh 轮换与重放/设备上限/admin 越权/升级套餐后 20 万字符且无日限)。
- 新增 `tests/FastTranslation.Core.Tests/AccountClientTests.cs`(34 例):`StubHandler : HttpMessageHandler` 断言请求 URI、方法、body 与 `Authorization: Bearer`,并覆盖 429/401/5xx 兜底、`HttpRequestException`→Network、`<html>gateway</html>`→InvalidResponse、死服务器下的 logout 不抛。
- 真服务器 E2E(Production 环境、独立 SQLite 文件、curl 与一个临时控制台探针):`/health` ok → 注册 `plan=free quota=10000 daily=2000` → me / usage → 重复注册 `EmailTaken` → 错密码 `InvalidCredentials` → refresh 轮换成功且新 access 不同 → **重放 Unauthorized** → 垃圾令牌 Unauthorized → logout → 死主机 `Network`。探针与临时库已删除。
- UI 证据(`assets/qa/account-main.png` 980x945、`assets/qa/account-dialog.png` 770x702 / 2366 种颜色,该目录不入库):UIA 控件树含邮箱/密码输入框、去注册/登录/关闭按钮,设置页 `账号服务地址` 输入框存在。

**残留风险**

- `AccountApiClient.DefaultBaseUrl` 目前是 `http://127.0.0.1:5199`(开发默认)。**正式对外前必须换成备案后的域名并走 HTTPS**,否则用户装上去默认连不上账号服务器(翻译不受影响,仍用本地 Key)。
- 本机没有任何人登录过的情况下,托盘「账号」能打开但提交会连不上 —— 属于预期行为,文案已说明是「自定义服务地址」。
- 邮件验证码的 SMTP 通道未接(未配置时只把验证码打进日志),所以**目前不能开 `RequireEmailVerification`**;阶段 2 做免费额度前必须先接上,否则刷号无门槛(设计 §7.4 三道闸的第一道)。
- 服务器尚无 TLS 终止、无反向代理、无备份策略;数据库是单文件 WAL,适合百人级,千人级要重新评估。
- 分档与网关(阶段 2)未做:现在所有人都是免费档,额度只被**展示**,没有任何地方真正扣减 —— `usage_events` 表与 `RecordUsage` 已就位但暂时没有调用方。

## v44 学习功能:保存结果 + 单词本三视图(阶段1)✅

**为什么这样改**

阶段 0 只解决了「能注册、能看到额度」。真正让付费有理由的是「保存」:免费版给 1 万字符网关额度但**完全不能保存**(决策 D8,连本地也不存),要保存就升级 —— 保存是这一版唯一的付费闸门,所以它必须先在客户端成立,订阅校验留到阶段 2 一次加。产品也据此从「划词翻译工具」变成「英语教学工具」:划词出译文顺手存进单词本/短语本/语句本,以后再复习。设计见 `docs/superpowers/specs/2026-10-28-account-and-billing-design.md`(阶段 1 一节)。

**服务端 `/api/v1/notes`**

- 数据模型是**一张 `notes` 表 + `kind` 字段**,不是三张表:`id / user_id / kind / source_text / target_text / source_lang / target_lang / model / origin_app / note / created_at / updated_at / deleted_at`。三个「本」只是同一张表按 `kind` 过滤;索引 `ix_notes_user_kind(user_id, kind, created_at DESC)` 直接服务列表页,`ix_notes_user_live(user_id, deleted_at)` 服务计数与去重。`deleted_at` 是软删除,留给阶段 3 的跨设备同步用 —— 硬删除会让另一台设备永远不知道这条被删了。
- `Domain/NoteClassifier.cs` 的类型判定**顺序有讲究**:先判句末标点(`. ! ?`)/长度 > 80 → 语句;再判「单个 CJK 字」或「不含空白且长度 ≤ 24」→ 单词;再判词数 2~6 → 短语;其余 → 语句。最初的写法是先判单词再判语句,`Really?`(无空白、11 字符)会被存进单词本,`她安静地离开了房间。` 同理 —— 这两条已经写进单元测试当回归锚点。
- `POST /api/v1/notes/` 是**幂等**的:`FindLive(userId, kind, sourceText)` 命中就直接回既有行,不新建。滑动选词反复点保存是常态,前端不该出现重复行。单账号上限 `MaxNotesPerAccount = 20_000`,超了回 409 `notes_limit_reached`(而不是静默失败)。
- `GET` 的 `q` 走 LIKE,且**必须转义**:把 `\` → `\\`、`%` → `\%`、`_` → `\_` 再配 `ESCAPE '\\'`;否则用户搜「100%」会把整库拉出来(下划线同理,单词里很常见)。
- 所有文本入库前统一截断(源/目标 5000、备注 2000、model 64、originApp 128、语言 16),避免一条异常长文本把整行撑爆。
- 端点文件 `Api/NotesEndpoints.cs` 挂在 `RequireAuthorization()` 组下;DTO 在 `Api/Contracts.cs`(`SaveNoteRequest` / `UpdateNoteRequest` / `NoteDto` / `NotePageResponse`)。

**客户端**

- `src/FastTranslation.Core/Notes/` 与 UI 无关、可单测:`NoteTypes.cs`(`NoteKind` / `Note` / `NotePage` / `NoteDeleted`)、`NoteClassifier.cs`、`NotesApiClient.cs`。类型判定在客户端**再实现一遍**是有意的 —— 卡片点保存要立刻显示「已保存到单词本」,不能等服务器回话;服务器仍是权威(不带 `kind` 时由它分类)。
- `Note` 的备注属性叫 **`Annotation`** 而不是 `Note`:叫 `Note` 会撞 C# 的 CS0542「成员名不能与其封闭类型同名」。
- `NoteDeleted` 这个只有 `bool Deleted` 的单字段 record 是被泛型逼出来的:`WithSessionAsync<T>` 有 `where T : class` 约束,`AccountApiResult<bool>` 编译不过。
- `AccountCoordinator` 新增四个 notes 包装方法,全部走既有的 `WithSessionAsync`:access 令牌过期时**自动刷新并重试一次**,词本里点删除不会因为半小时没动就报「未登录」。构造参数加了 `string? deviceFingerprint = null, NotesApiClient? notes = null`,不传 notes 时四个方法统一回 `NotSignedIn`,不给调用方留 null 分支。
- 结果卡(`src/FastTranslation.Platform.Windows/OverlayWindows.cs`)加第 4 个按钮「保存」,排在「插入译文」之前。它**始终可见**(不像替换/插入要看选区是否可写),因为「不能保存」本身就是要说给免费用户听的话:未登录点它 → 卡片切一行提示「先在账号里登录,保存的单词才能跨设备同步」并打开账号窗;已登录 → `SaveNoteAsync` 成功后 `MarkSaved()` 把按钮改成「已保存」并禁用(防连点),同时 `ShowNotice("已保存到语句本")`。
- `origin_app` 取 `ForegroundInfo.ProcessName()`(进程名,不记窗口标题) —— 窗口标题常常就是文档名甚至聊天内容,记下来等于偷偷采集用户数据。
- `NotesWindow`(新建)是 820x560 的非模态窗:顶部搜索框(320ms 防抖)+刷新,左侧三列「单词/短语/语句」书架,中间列表,右侧详情(原文/译文/改类型/备注/保存备注/删除/来源行)。改类型会换架,所以改完清空选中再重新拉列表。
- 入口两处:托盘菜单「单词本」(id 1007,`TrayIcon.NotesRequested`)与主窗 footer 的「单词本」按钮,已开则前置复用。

**验证**

- `dotnet build FastTranslation.slnx -c Release` → **0 警告 0 错误**;`dotnet test` → **133 + 83 + 40 + 109 = 365 全绿**(阶段 0 基线 327,新增 38 个)。新增:`NoteClassifierTests`(14)、`NotesApiClientTests`(7,用 `StubHandler` 断言 method/URI/body)、`NotesEndpointTests`(17,含跨账号隔离与幂等)。
- 真服务器 E2E(`ASPNETCORE_ENVIRONMENT=Development`、`http://127.0.0.1:5199`、账号 `e2e-stage1@example.com`):三条文本分别被自动判定为 word(`meticulous`)、sentence(`She left the room quietly.`)、phrase(`in the long run`);`GET /api/v1/notes/?kind=word` 只回 word 且 `total:3`;重复保存返回**同一个 id**,幂等成立。
- UI 证据(`assets/qa/`,不入库):`notes-card-result.png` 735x266 显示结果卡按钮为「保存 / 复制」;`notes-card-saved.png` 735x159 显示点击后卡片变成 **「已保存到语句本」**,证明「卡片 → OnSaveRequested → 服务器 → MarkSaved」整条链路通;`notes-window-word/-phrase/-sentence.png` 1435x980 三视图 + `notes-window-detail.png` 详情面板(含类型下拉、备注框、删除);`notes-main-with-button.png` 主窗 footer 出现「单词本」。

**残留风险**

- **免费版现在也能保存** —— 阶段 1 明确不做订阅闸门,所有登录用户都能存。阶段 2 加闸门时必须把已有数据算成「存量」(老数据只读或给宽限),否则会一夜之间让已存了几百条的用户点不开自己的本子。
- 列表一次拉 200 条、无分页游标;`total` 是账号总条数,翻页要等阶段 3 做同步时一起补。
- 单词本没有离线缓存,断网就是空列表;用户会以为是「数据丢了」。要等阶段 3 的本地镜像。
- 保存失败只提示「保存没成功,稍后再试一次」,没有区分网络与服务器错误 —— 现在够用,但用户量上来后需要能自查的文案。
- `origin_app` 依赖前台进程名,`ForegroundInfo.ProcessName()` 失败时为空串,数据里会出现空来源。
## v45 账号变现:开发者网关 + 额度闸门 + 订阅闸门(阶段2)✅

把「免费额度」从每个用户自带 Key 改成服务端用开发者自己的 Key 转发并记账,同时让「保存到单词本」成为第一个付费闸门。用户原话:*「给的免费额度直接走我的 API key 吧,给他们额度限制就行」* —— 不办执照、不接支付、不做手机号,付费靠激活码手动开通。

**为什么这样改**

- 以前免费用户想用就得自己申请百炼 Key,这是最大的流失点;开发者 Key 只有几十块成本,直接把门槛换成额度更划算。
- 记账必须落在服务端,客户端不可能诚实上报用量。所以网关是唯一入口:`POST /v1/chat/completions` 与 `POST /v1/image`。
- 额度耗尽时**不能报错卡住用户**:客户端要静默回落到用户自己的 Key,并在结果卡上说明一句。所以服务端返回 **429(不是 403)**,响应体 `{"error":"monthly_quota_exhausted"}` 加响应头 `X-FT-Quota-Reason` —— 403 会被客户端读成「账号被停用」。
- 闸门放在保存接口的**最开头**:若放到幂等查询之后,免费账号就能通过「这条是否已存在」的返回值探知自己的数据,闸门等于没关。

**服务端**

- `GatewayOptions.cs`(项目根,不在 `Gateway/` 下)绑定 `Server:Gateway`:`Enabled`、`ApiKey`、`BaseUrl`、`ImageBaseUrl`、`Model`、`TimeoutSeconds=60`、`DailyBudgetMicros=5_000_000`(全站日预算 5 元)、`RequestsPerMinute=40`、`MaxRequestChars=5_000`、`ImageQuotaChars=1_000`;配置项全部在 `appsettings.json` 补了默认值。
- `Gateway/GatewayEndpoints.cs`:`/v1` 组 `RequireAuthorization()`。聊天链路:503 `gateway_disabled` → 400 `text_required` → 413 `text_too_long` → 四道闸 → 转发 → 记账 → 原样返回上游体。**上游非 2xx 原样转发状态码且不记账**。四道闸顺序是 `rate_limited`(429) → `budget_exhausted`(429) → `account_disabled`(403) → 额度(429 + `X-FT-Quota-Reason`)。
- `Gateway/DashScopeGateway.cs`:把客户端令牌**换成**开发者 Key 再上行,客户端令牌永远不出现在上游请求里(有测试断言这一点)。
- token 记账:上游报了 `usage` 就用真实值,没报就按 `chars + 45` 估算输入(45 是 Qwen-MT 每次的固定前言),输出按 0。图片接口不报 usage,按 `ImageQuotaChars` 折成等效字符额度记账。
- `Domain/Pricing.cs` 把 token 换成微元成本存进 `usage_events.cost_micros`,全站日预算是对这些成本求和。
- `Domain/Entitlement.cs`:`plan_expires_at IS NULL` = 永久授权(手工开通不会因为没填到期时间就被锁死)。`EffectiveTier` 让**过期只降额度、不断服务**。
- 订阅闸门:`NotesEndpoints.Save` 一开始就判 `Entitlement.AllowsNotebook`,拒绝时 403 `subscription_required`。读接口永远不加闸。
- 管理面:激活码还没做,当前用 `POST /api/v1/admin/accounts/{email}/plan` 手动开通(`days` 是相对天数,0 表示永久)。

**客户端**

- `Core/TranslationTypes.cs` 新增 `TranslationErrorCode.QuotaExhausted`(刻意与 `RateLimited` 分开,组合 provider 靠它决定要不要回落)、`TokenUsage`、`TranslationResult.Usage` 与 `Notice`。
- `Providers.QwenMt/QwenMtProvider.cs`:`QwenMtOptions.ExtraHeaders` 让同一个类同时服务 DashScope 直连与网关(网关要 `X-FT-Device`,DashScope 忽略未知头);失败分支先读 body 再看 `QuotaRefusal.Describe`,命中即 `QuotaExhausted` 且**不重试**(额度用完重试没意义,还会白白多一次请求)。
- `GatewayTranslationProvider` / `GatewayImageTranslationProvider`:先走网关,成功即返回;网关返回 null(未登录/开关关)就只用自有 Key;网关拒绝时回落自有 Key,并在结果上写一句 `Notice` —— 用户看到的是「本月额度已用完,这次用了你自己的 Key」,而不是一条报错。
- `AccountCoordinator` 暴露 `DeviceFingerprint` 与 `GetAccessTokenAsync`(距过期 1 分钟内先刷新),网关请求因此永远带新鲜令牌与**和登录时同一个**设备指纹。
- 结果卡状态行(`OverlayWindows.StatusLine`)从「模型 · 耗时」扩成「模型 · 耗时 · 缓存 · N tokens · 一句说明」,并给 `_statusBlock` 加 `TextWrapping`,否则 420 宽度会把说明截掉。
- 保存被闸门拒绝时,卡片先说明原因再打开账号窗,不再只弹一行文案。

**验证**

- `dotnet build FastTranslation.slnx -c Release` → **0 警告 0 错误**;`dotnet test` → **138 + 87 + 58 + 125 = 408 全绿**(阶段 1 基线 365,本阶段新增 43)。
- 真服务器 E2E(`ASPNETCORE_ENVIRONMENT=Development`、`http://127.0.0.1:5199`、上游用本地桩,7 项全通过):
  1. 免费账号经网关翻译成功,且**上游收到的是开发者 Key 而不是客户端令牌**;
  2. 用量按 100 字符记到月/日两张表;
  3. 免费账号保存 → 403 `subscription_required`;
  4. 管理接口开 30 天轻享后,同一条保存 → 200 且能 round-trip;
  5. 升级后账号的 `quotaDaily` 变 null(不再有日限),继续翻译正常;
  6. 另一个免费账号花完 2,000 日额度 → **429 `daily_quota_exhausted` + `X-FT-Quota-Reason`**;
  7. `usage_events` 保留上游报的 60/8 token,成本 **58 微元/次 ≈ 0.0097 元/人/月**(设计预估 0.035,实测更低)。
- E2E 抓到一个真实缺陷并已修:网关把日额度记在**设备**上,但 `GET /api/v1/account/usage` 只认 `deviceId`,客户端只有指纹 → 账号窗「今日用量」恒为 0,而用户其实已被限流。现在该接口也接受 `fingerprint`(解析成 deviceId),`AccountApiClient.GetUsageAsync` 带上指纹,补了两侧测试。
- UI 证据(`assets/qa/`,不入库):`card-fallback.png` 783x276,UIA 文本为 `qwen-mt-flash · 486ms · 65 tokens · 本月额度已用完,这次用了你自己的 Key`,证明用量与回落说明都能在 420 宽度下换行显示;取图脚本 `tools/qa/shot-card-fallback.ps1`(新增 `FT_UITEST=overlays-fallback` 钩子)。

**残留风险**

- **存量免费用户现在能保存,明天就不能了**:阶段 1 没有闸门,已经存了数据的账号升级后会看到「需要付费」。上线前必须决定老数据是只读还是给宽限,否则是数据层面的破坏性变更。
- 全站日预算 5 元是**止损开关,不是防刷**:被刷爆的后果是所有人一起回落到自带 Key(还算体面),但当天免费体验就没了。没有 IP/设备级限速,只有每账号 40 请求/分钟。
- 激活码还没做,`plan_expires_at` 只能靠管理接口手填;真开卖前必须补 `POST /api/v1/notes` 之外的兑换链路,否则每来一个用户都要手工改库。
- 图片翻译按 `ImageQuotaChars=1000` 折字符记账,与真实成本(约 0.004 元/张)只是粗略对应;截图翻译量大时会低估消耗。
- 网关让**所有**登录用户的翻译都经过服务器,香港国际型线路对内地用户有延迟与丢包(设计 §2.4 已记);单机 ASP.NET 没有水平扩展方案,用户量上来要先解决这个。
- `GatewayOptions` 在项目根而不是 `Gateway/` 目录下,是历史遗留位置;找它时别只在子目录里搜。
- 设计 §5 原本要求「保存按钮灰掉并给订阅入口」,实际做的是**不置灰**:订阅/激活码页面还不存在,置灰只会让免费用户点了没反应又不知道为什么。等激活码链路做完再改成置灰 + 引导按钮。
## v46 官网:产品介绍静态单页(`site/`)✅

给 Fast Translation 做一个能对外说明产品的网页。用户原话:*「对了,我们肯定是要做个网站介绍产品的」*。选静态单页而不是前端工程:全站只有一个页面、没有任何数据交互,引入构建链只会增加发布步骤。

**为什么这样写**

- 首屏必须直接展示产品本体,不是讲道理。所以头图不是截图,而是用 CSS 1:1 重建的「浮动工具条 + 结果卡」:选中段落里一段蓝底文字、下面压着三个按钮、再下面是结果卡。好处是不依赖截图分辨率、深色模式自动跟着翻,也不需要维护两套图。
- 全站只有一个强调色(选区蓝 #2562FF),底色用纸白 #FDFCF8、正文用衬线,标题用无衬线 —— 因为产品做的事就是「在别人的文字上叠一层译文」,页面本身就该像一份双语评注本。
- 套餐只在页面上写价格与额度,不写「点击购买」。现在没有支付通道,也没有兑换入口,写上去就是假承诺。三张卡的按钮一律指向「下载」。

**页面结构**

- `site/index.html`(286 行)→ `site/assets/site.css`(14 KB)→ `site/assets/img/`(6 张 PNG,含 `og.png` 分享图)。章节:首屏 / 01 交互三按钮 / 02 界面四张截图 / 03 实测三个数字 / 04 能做与不做 / 05 套餐 / 06 下载 / 页脚。
- 02 的截图全部是真实运行截图(`assets/qa/` 里的同源文件),不是设计稿。
- 03 的三个数字来自 v45 的真机实测:531 毫秒、65 token、3 台设备上限。

**踩到的坑**

- **Headless Chrome 有最小窗口宽度**:`--window-size=390` 实际渲染出 clientWidth=518,所有「移动端」截图其实是被裁切的桌面视图。要测真实窄宽,得写一个外层页面用 `<iframe style="width:390px">` 包住站点再用 `--window-size=420,…` 截图。临时文件用完即删。
- **深色模式下卡片白底**:早先把卡片背景硬编码成 `#fff`,深色变量只翻文字颜色,于是浅字压白底几乎看不清。修法是引入 `--surface`(浅色 #FFFFFF / 深色 #15181D),所有卡片背景改用它;蓝底按钮保持白字。
- 结果卡按钮在 390px 下被逐字折行(「保存」被拆成两行),给按钮与状态行加 `white-space: nowrap`、给按钮行加 `flex-wrap: wrap`,窄宽下状态行独占一行。
- 套餐卡要「按钮压到卡底对齐」,得让 `.plan` 成为纵向 flex 并把 `ul` 设 `flex: 1 1 auto`;否则三张卡按钮高度错位。顺带 `.plan .tag` 必须 `align-self: flex-start`,不然「推荐」标签会被拉成整条色带。

**验证**

- 浅色 / 深色 / 1440 宽 / 390 宽四套截图逐段看过,无溢出、无错位:首屏演示、01 三卡、02 四图、03 数字、04 双栏、05 三卡 + 按钮、06 两卡 + 页脚。
- 深色预览的做法:复制到 `publish/sitedark/` 把 `@media (prefers-color-scheme: dark)` 换成 `@media all` 再截图(不污染源文件)。
- 标签平衡核对:`<div>` 55/55、`<section>` 6/6、`<a>` 12/12;五个锚点 `#how/#look/#numbers/#plans/#download` 都存在。
- 页面高度 1440 宽下 9184 px,深色与浅色一致。

**残留风险**

- **站点在备案通过前不能对外提供访问**,现在只能本地开 `index.html` 预览。要上线得先有域名、内地服务器、备案号,再把备案号写进页脚(现在页脚没有备案号占位)。
- `og:image` 用的是相对路径 `assets/img/og.png`;微信、Twitter 等抓取器普遍要求绝对 URL,拿到域名后要改成 `https://<域名>/assets/img/og.png`。
- 下载按钮直接指向 GitHub Releases 的 `latest`。国内访问 GitHub 不稳定,真要推广时得换成自己服务器上的文件或对象存储。
- 页面上的安装包名写死成 `FastTranslation-1.2.2-Setup.exe`,发版时必须跟着改(两处:安装卡与便携卡)。

## v47 部署:一台 ECS 上的 systemd + Caddy + SQLite ✅

**为什么这样改**

阶段 2 的网关和账号服务已经能跑,但它只在 `dotnet run` 里活过。要真上线还缺三件事:进程怎么常驻、HTTPS 证书从哪来、数据库文件放哪。这三件事写成一个可重复执行的 `deploy/` 目录,而不是一份手抄命令清单 —— 因为部署一定会做第二遍(换机器、换域名、升级)。

选型上有几个刻意的决定:

- **不引 Docker**。2G 内存的机器上再养一层容器运行时,runtime 镜像本身就要 200MB+,而且 SQLite 文件挂载出去比直接放 `/var/lib` 更容易出权限问题。systemd 单元 + 一个自包含二进制已经够了。
- **Kestrel 只监听 `127.0.0.1:5080`**。证书与 80/443 全部交给 Caddy,应用进程不碰端口,也就不需要 `CAP_NET_BIND_SERVICE`。
- **Caddy 而不是 nginx**。Caddy 自己申请并续期 Let's Encrypt 证书,nginx 还要配 certbot 的 timer 和 reload hook。项目里已经有一个「能少一个组件就少一个」的取向。
- **占位符没替换就不启用 Caddy**。`__DOMAIN__` 还在的时候强行 `systemctl start caddy` 只会每 30 秒重试一次 ACME 然后失败,日志里全是噪音。所以 `install.sh` 用 `grep` 检查占位符,还在就 `systemctl disable --now caddy` 并打印提示。

**服务端**

没有改服务端代码。部署文件依赖的既有事实:

- `Server:SigningKey` 在非 Development 环境下缺失或不足 32 字节会直接启动失败(`server/FastTranslation.Server/Program.cs:89-100`)。这是好事 —— 一个没配密钥的实例不应该安静地起来。
- `/health` 返回 `{"status":"ok"}`(`Program.cs:57`),`install.sh` 装完就用它做健康检查。
- 数据库路径支持绝对路径(`Program.cs:29-31`),所以单元里直接用 `Server__DatabasePath=/var/lib/fasttranslation/fasttranslation.db` 覆盖,不用改 `appsettings.json`。

`deploy/` 七个文件:

| 文件 | 作用 |
| --- | --- |
| `build-server.sh` | 本机执行,`dotnet publish -r linux-x64 --self-contained` 到 `publish/server-linux` |
| `bootstrap.sh` | 服务器上跑一次:2G swap、`ftserver` 系统账号、四个目录、apt 依赖、Caddy 官方源、libicu 检查 |
| `install.sh` | 幂等主安装:二进制、systemd 单元、静态站点、备份任务、Caddyfile |
| `fasttranslation.service` | `ProtectSystem=strict` + `ReadWritePaths=/var/lib/fasttranslation`,除数据库目录外整机只读 |
| `Caddyfile` | 静态站点 + `/api/*`、`/v1/*`、`/health` 反代,两处占位符 |
| `appsettings.Production.json.example` | 只列必须改的三项(SigningKey / AdminToken / Gateway:ApiKey) |
| `backup.sh` + `fasttranslation-backup.cron` | 每天 04:17 `sqlite3 .backup` + gzip,保留 14 天 |

备份脚本用 `sqlite3 $"$DB" ".backup ..."` 而不是 `cp`:数据库开着 WAL,直接拷主文件会漏掉 `-wal` 里还没 checkpoint 的事务,恢复出来是旧的。

**验证**

- `bash deploy/build-server.sh` → `publish/server-linux/` 341 个文件,二进制 78192 字节。产物里**没有** `appsettings.Production.json` —— 这是刻意的,生产配置不进发布产物。
- `bash -n` 四个脚本全部通过;`grep -c '\r'` 为 0,纯 LF(在 Windows 上写 shell 脚本最容易出的问题就是 CRLF,`bash` 会报 `$'\r': command not found`)。
- `caddy fmt --overwrite` 之后 `caddy adapt` 退出码 0,展开出的 JSON 确认了四件事:`admin` 已禁用、`/assets/*` 拿到一年期 immutable、`/api/*` 与 `/v1/*` 与 `/health` 三条都落到 `127.0.0.1:5080`、兜底是 `file_server` + `try_files {path} /index.html`。
- 服务器侧尚未实测:公网 IP `YOUR_SERVER_IP` 的 22 端口已通,80/443 仍关闭(安全组未开、Caddy 未装),与「备案通过前不对外」的预期一致。

**残留风险**

- **整套流程没有在真机上跑过一遍**。`bootstrap.sh` 里的 cloudsmith 源、`libicu` 包名、`systemd` 的加固指令都只做过语法检查;第一次跑大概率要修一两处。
- 3 Mbps 固定带宽意味着单个客户端下载安装包最快约 375 KB/s。安装包 60MB 左右,即 2~3 分钟。真要放安装包得另外接对象存储。
- 没有配置日志轮转。`journalctl` 会自己滚,但 `/var/log/ft-backup.log` 会一直长(一天一行,可忽略)。
- 备份只有本机一份。云盘挂了备份跟着挂,真要防灾得 `scp` 到别处。
- `install.sh` 的静态站点同步是 `rm -rf` 再拷 —— 如果 `WEB_DIR` 被误设成 `/var/www`,会连别的站点一起删。脚本里用了 ${WEB_DIR:?},变量为空时直接退出,但这个保护只在变量未设置时生效。
## v48 仓库瘦身:清掉过程文件与构建缓存(2026-10-29)✅

**为什么这样改**

仓库从 v1 长到 v47,攒下两类东西:每个历史版本的发布产物、几百个一次性探针脚本。它们都不在版本控制里(`.gitignore` 早就覆盖了 `publish/`、`assets/qa/`、`[Bb]in/`、`[Oo]bj/`),但会持续放大磁盘占用与备份体积,也让全仓 grep 变慢。这次做一次性盘点,顺便把「哪些东西其实没人引用」这件事查实。

三个刻意的决定:

- **只删在远端有备份的历史包**。9 个 `portable.zip` 与 7 个 `Setup.exe` 在 GitHub Release 里都有同名资产,本地副本零信息增量。反过来,1.2.2 的两个包必须留 —— 它们是对外分发的最新版本。
- **探针工程从仓库里删,而不是继续留在库里**。`tools/Probe`、`ProbeBare`、`ProbeBare8`、`ProbeWf`、`E2EKeySmoke` 自 v22 基线入库后再未改动、不在 `FastTranslation.slnx` 里、`docs/IMPL.md` 零引用。留着只会让后来者以为它们还在用。
- **`assets/qa` 只留被 IMPL.md 引用的 10 张**。整个目录被忽略,不设边界的话「截图即证据」这个说法就没有落点。删前逐个比对 `docs/IMPL.md` 里出现的文件名,保留的 10 个在 IMPL.md 中各命中 1 次。

**删了什么**

| 范围 | 删除前 | 删除后 |
| --- | --- | --- |
| `publish/` 全部 | 4157 文件 / 2591 MB | 992 文件 / 240.32 MiB |
| ├ `publish/dist` 历史包 | 30 文件 / 947.4 MB(9 个 portable.zip、7 个 Setup.exe、2 个旧 MSI + wixpdb、9 个安装日志) | 3 文件 / 103.5 MB(仅 1.2.2 的 Setup.exe + portable.zip + README.txt) |
| ├ 旧便携目录 | 8 个 × 263 文件,各约 134 MB | 0(只留 `ft-folder`,262 文件) |
| ├ 一次性探针产物 | `probe/` 41 文件 91.4 MB、`caddy/` 79.1 MB、`server-linux/` 106.4 MB、`tmp/`、`probeout/`、`inno7.exe` 13.6 MB、`innosetup.exe`、`speed.bin` 3.8 MB | 全删 |
| ├ 散装脚本与日志 | 根目录 257 个文件 19.85 MB(78 个 .ps1 探针、60 个 .py、56 个 .iss、20 个 .log、19 个发布载荷 json、9 个已并入 IMPL 的 md 分片、7 张 qa png) | 17 个文件(9 个按名保留的脚本 + `chm6.chm` + 7 张 `qa-notice-*.png`) |
| ├ `img/` + `art/` | 37 文件 / 1,357,737 B(MDN 抓取素材、诊断脚本、badge png) | 全删(全仓 0 引用) |
| 全部 `bin/obj` | 22 个目录 / 519,622,530 B(495.55 MiB) | 0 / 0 |
| `assets/qa` | 190 png / 26,752,819 B(25.51 MiB) | 10 png / 2,166,795 B(2.07 MiB) |
| `tools/` 探针工程 | 45 个被跟踪文件 | 0(`tools/` 下只剩 `qa/`) |
| 陈旧 worktree `D:/ft-rel-113` | 537 MB(detached HEAD `a8dcf5b`) | 已 `git worktree remove --force` |
| `.git` 对象库 | 404 文件 / 10.2 MB,`refs/original/` 残留 filter-branch 备份 | 34 文件 / 8.82 MiB,`size-pack: 8.75 MiB` |

合计:仓库根 **约 3.13 GB / 约 7970 文件 → 262.60 MiB / 1317 文件**,释放约 2.87 GB;另有仓库外的 537 MB 陈旧 worktree 一并移除。

**保留清单(逐项 EXISTS 校验)**

`publish/` 下按名被引用的都留住了:`chmread.py` + `chm6/`(AGENTS.md 指定用它读 Inno 帮助)、`poll-manifest.sh` + `poll-manifest.ps1`、`shot-pages4.ps1`、`e2e-shot.ps1`、`inno-install.ps1`、`dir-args-test.ps1`、`dl-inno.sh`、`final-check.sh`、7 张 `qa-notice-*.png`、`CardProbe/`、`InstallProbe/`。`ft-folder/` **262 个文件**原样保留 —— 它是 `installer/FastTranslation.iss:81` 的 `Source: "..\\publish\\ft-folder\\*"` 输入,删了就打不出安装包。

**验证**

- 删净 `bin/obj` 后 `dotnet build FastTranslation.slnx -c Release`:**已成功生成 / 0 个警告 / 0 个错误**,10.59 秒 —— 证明 `obj` 里没有任何不可重建的状态。
- `dotnet test`:4 个项目 **408 全绿**,失败 0 跳过 0(Core 138 / QwenMt 58 / Platform.Windows 87 / Server 125)。
- `git fsck --no-progress` 退出码 0,无 corrupt / missing,只剩 11 个 dangling blob。
- `git worktree list` 只剩 `D:/FTranslation  cb22e9c [master]`。
- `git status --porcelain` 只剩 `.agents/design` 下他人进行中的 4 条,没有任何 `publish/` 残留。

**残留风险**

- `publish/` 仍有 240.32 MiB,主体是 `ft-folder`(262 文件)与 dist 的 1.2.2 两个包。这是打包流水线的输入与输出,**不是可回收空间**;真要再瘦只能改流水线成「按需 publish」。
- 1.0.x–1.2.1 的二进制现在只存在于 GitHub Release。哪天误删 Release,这些版本的产物就找不回来了。
- `assets/qa` 里被删的 180 张属于不存在于任何文档的调试截图,不可恢复;但 IMPL.md 引用的 10 张都在,证据链完整。
- `bin/obj` 会在下次 `dotnet build` 时重新长出来(实测重建 18 个目录 / 97.25 MB),这是正常构建产物,不是垃圾。


## v49 商业闭环:激活码兑换(`POST /api/v1/redeem`)✅

**为什么这样改**

阶段 2 把「免费额度 + 开发者网关 + 订阅闸门」做完之后,服务端的付费能力是齐的——但没有任何一条路径能让用户**自己**变成付费用户:只能靠管理员拿 `X-Admin-Token` 调 `POST /api/v1/admin/accounts/{email}/plan` 手动改套餐。官网(阶段 v46)已经写着「付费套餐用激活码开通」,而 `/api/v1/redeem` 在服务端是零实现,这句话是空的。

三个刻意的决定:

- **先发码、后绑定支付,而不是等接支付再开闸**。码本身就是「已付款」的凭证,从哪卖出去(面包多、淘宝、微信收款)是运营问题,不是代码问题。把码的生成、兑换、幂等、降级保护做扎实,支付渠道以后接什么都不会改这一层。
- **码用 Crockford base32,不是 GUID 或 UUID 的变形**。16 个字符 × 5 bit = 80 bit 熵,猜不出来;字母表去掉 I/L/O/U 且兑换时把 O→0、I/L→1、U→V 折叠,所以用户抄错、看错、手打错的概率被压到很低——`Redeem_AcceptsWhatAHumanRetypes` 测的就是「把横线去掉、前缀截掉、全部小写」还能兑。
- **降级保护放在 claim 之前**。花 99 元买了标准版的人,手里剩一张轻享码,如果兑换流程先 `UPDATE redeem_codes` 再判套餐,码就废了、用户还降级。现在的顺序是:先判「已经是更贵的有效订阅」→ 直接 400 `redeem_plan_downgrade`,码一个字节都不动,可以送给别人。

**做了什么**

服务端(全部落在 `server/FastTranslation.Server/`):

| 文件 | 内容 |
| --- | --- |
| `Domain/RedeemCode.cs`(新) | `Generate()` 用 `RandomNumberGenerator.Fill` 取 16 字节,每字节 `& 31` 索引 32 字符字母表(256 是 32 的整数倍,所以分布均匀);`Format()` 产出 `FT-XXXX-XXXX-XXXX-XXXX`(22 字符);`Normalize()` 折叠歧义字母并校验长度 |
| `Storage/RedeemStore.cs`(新) | `CreateCodes` 事务批量插入;`Find`;`TryClaim` = `UPDATE … WHERE code=$code AND redeemed_at IS NULL`,靠 WHERE 实现「只能用一次」而不需要显式锁;`ReadStats` |
| `Storage/ServerDatabase.cs` | Schema 加 `redeem_codes` 表(`code` 主键、`plan`、`days`、`note`、`created_at`、`redeemed_by` 外键 `ON DELETE SET NULL`、`redeemed_at`)与 `ix_redeem_codes_redeemed` |
| `Api/RedeemEndpoints.cs`(新) | `POST /api/v1/redeem`(需登录,与 auth 共用按 IP 的限流):归一化 → 账号健康 → 码存在 → 未兑换 → 不降级 → `TryClaim` → 算到期 → `SetPlan` → 返回新 `AccountDto`+`UsageDto` |
| `Api/AccountEndpoints.cs` | 管理端 `POST /api/v1/admin/codes`(plan/days/count 三重校验,一次最多 200 张) |
| `Api/Contracts.cs` | `RedeemRequest/RedeemResponse/CreateCodesRequest/CreateCodesResponse` |

到期时间的两条规则:`stacks = 同档位 && 现有到期时间还没过`,同档则往后**续**,不同档从**现在**起算。这样「标准版还有 10 天,再买一张标准版码」= 40 天,而「轻享升标准」不会把那 10 天送出去。

客户端:

- `src/FastTranslation.Core/Account/AccountTypes.cs`:三个错误码 `CodeInvalid`/`CodeUsed`/`PlanDowngrade` 与中文文案;`AccountProfile` 增加 `PlanExpiresAtUnix`(末位带默认值,旧调用点无需改)与派生属性 `ExpiryLine`;新增 `AccountRedeem`
- `src/FastTranslation.Core/Account/AccountApiClient.cs`:`RedeemAsync`;`MapFailure` 认三个 `redeem_*` 错误码。**注意错误码刻意带 `redeem_` 前缀**——`code_invalid` 这个名字已经被邮箱验证码占用了,不加前缀会在用户眼里变成「验证码不对」
- `src/FastTranslation.App/AccountCoordinator.cs`:`RedeemAsync` 走既有的 `WithSessionAsync`(自带 401 自动刷新),成功后就地替换 `Profile` 并写回 `AccountSessionStore`,不重启窗口也能看到新套餐
- `src/FastTranslation.App/AccountWindow.xaml(.cs)`:账号窗新增「激活码」卡片(输入框 + 兑换按钮 + 一行提示),套餐行末尾追加 `· 有效期至 2026-11-28`

**验证**

- `dotnet build FastTranslation.slnx -c Release`:**0 警告 0 错误**
- `dotnet test`:**439 全绿**(Core 143 / QwenMt 58 / Platform.Windows 87 / Server 151),本次 +31:服务端 8 个兑换端点测试 + 13 个码格式测试,客户端 3 个
- 真实服务器端到端(Development、`http://127.0.0.1:5199`、`Server__AdminToken=dev-admin`):注册免费账号 → 管理端发一张标准版 30 天码 `FT-T8KW-ETEH-G3FP-V5C0` → 应用内兑换 → `GET /api/v1/account/me` 变 `standard`、`quotaDaily` 变 `null`、`planExpiresAt` = 30 天后
- 界面证据(UI Automation 驱动真实 exe,不是静态稿):`assets/qa/redeem-account-before.png`(770×936,免费版 + 每日 2,000 行 + 激活码卡)与 `assets/qa/redeem-account-after.png`(770×823,标准版 · 有效期至 2026-11-28,「今日用量」整行消失,提示「已开通 标准版。」)。脚本 `tools/qa/shot-redeem.ps1` 可复现

**残留风险**

- **发码只回一次响应,库里没有「未兑换码清单」查询接口**。管理端现在只能「生成 → 自己复制保存」,重跑一次就拿不到了。真要运营,得在 `RedeemStore` 上加按 `redeemed_at IS NULL` 过滤的列表端点。
- **码没有有效期**。`redeem_codes` 只有 `created_at`,没有 `expires_at`——一张两年前发出去的码今天仍然能兑。这是刻意的(不惩罚用户屯码),但意味着没有「作废过期码」的手段。
- **码没绑订单,理论可转卖/泄露**。任何拿到 22 字符的人都能兑,赎回形态无法追溯来源。当前靠「一次性 + 80 bit 熵」兜底。
- **兑换与登录共用按 IP 的限流(默认 20 次/分)**。同一 NAT(学校、公司、网吧)下十几个人同时兑换会撞限流,报的是 `rate_limited` 而不是更具体的提示。

## v50 上线准备:域名定为 `ftranslation.cn`,备案号留槽 ✅

**为什么这样改**:备案要等 1~20 天,而这段时间里凡是「只差一个字符串」的改动都该先落地并验证完。否则备案号下来那天,还要临时改四处、临时验证四遍——把等待期变成准备期。

**改了什么**

| 文件 | 改动 | 为什么 |
|---|---|---|
| `deploy/Caddyfile` | 站点键改成 `__DOMAIN__ www.__DOMAIN__` | 一个 block 服务两个主机名,一张证书覆盖两条;占位符出现两次,一条 sed 同时替换 |
| `deploy/Caddyfile` 头注释 | 写明真实域名与可直接复制的 sed 命令 | 部署时不用回来翻聊天记录确认域名 |
| `site/index.html` | `og:image` 改绝对 URL,新增 `rel=canonical` | 微信/QQ 抓 `og:image` 不解析相对路径,分享出去没有缩略图 |
| `site/index.html` 页脚 | 新增 `__ICP_NUMBER__`,链到 `https://beian.miit.gov.cn/` | 境内网站底部必须展示备案号并可点查 |
| `deploy/install.sh` | 页脚仍含占位符时打印警告 | 防止备案号忘了替换就对外 |
| `deploy/README.md` | 真实 sed 命令 + 新增「页脚的备案号」小节 | 同上 |

**刻意没改的一处**:`src/FastTranslation.Core/Account/AccountApiClient.cs:18` 的 `DefaultBaseUrl` 仍是 `http://127.0.0.1:5199`。服务器还没部署、备案还没通过,现在改成线上域名会让本机全部 E2E 与 `dotnet run --project server/FastTranslation.Server` 直接连不上。它属于**第一次正式发版**那个批次,不是现在。

**验证**

- `caddy adapt`(v2.10.2,占位符替换为真实域名后)退出码 **0**;展开 JSON 的 `apps.tls.automation.policies[0].subjects` = `["ftranslation.cn", "www.ftranslation.cn"]`,确认一张证书覆盖两主机名
- `caddy fmt --diff deploy/Caddyfile` 无差异(格式合规)
- `bash -n deploy/install.sh` 通过
- 页脚证据:`assets/qa/site-footer-before.png` 与 `-after.png`(1280×360,用站点自身的 `assets/site.css` 渲染,非伪造样式),像素 diff 覆盖整幅;after 页脚右栏第三行是带下划线的 `__ICP_NUMBER__` 链接,颜色继承页脚灰字
- 本轮没动任何 C# 文件,`git status --short` 只有 `deploy/` 三个 + `site/index.html` + `docs/IMPL.md`

**残留风险**

1. **`www` 必须单独加解析记录**。云解析里只加主域 A 记录的话,`www.ftranslation.cn` 无解析,Caddy 对两主机名签证书时 www 那条会失败,连带影响整张证书。上线时 `@` 与 `www` 两条一起加。
2. **占位符会原样显示**。备案通过前站点本就不该对外(此刻 Caddy 被 `install.sh` 刻意 disable),但若有人手工 `systemctl start caddy`,页脚会露出 `__ICP_NUMBER__`。install.sh 的警告是唯一防线。
3. **域名实名与工信部同步仍未完成**(注册局审核中 → 通过后还需 2~3 天同步),备案尚未提交。若管局要求「域名证书」,在阿里云域名控制台 → 域名列表 → 管理 → 下载域名证书。
4. `beian.miit.gov.cn` 在部分网络环境下访问慢,备案号链接点开可能超时;这是工信部站点自身的问题,不影响合规性。

## v51 学习闭环:截图存单词本 + 四档复习 + 单窗口七页 ✅

**为什么这样改**

v44 做完了「保存」,但那时候的账只结了一半:

- **截图翻译和单词本是两件事**。截图翻译走的是 `qwen-mt-image` 直接把图翻成文字,屏幕上出现的译文看得见、抄不走。用户想记下里面某个词,只能自己再敲一遍。OCR 单独抽一路之后,识别到的原文进了结果窗的可编辑框,「保存到单词本」才算真的可用。
- **存了不复习等于没存**。v44 的生词本只有三个视图(词/短语/句),没有任何调度:存 500 条和存 5 条在界面上没有区别。这次补上四档复习与到期队列,`notes` 表加了 6 列复习状态。
- **四扇窗各管一段,入口是散的**。此前主窗口、设置窗、账号窗、结果卡各自为政:换一个 API Key 要开设置窗,看套餐要开账号窗,读完一条结果要回主窗口才有列表。现在合成一扇窗 + 左侧栏七页,唯一还独立的是复习窗 —— 它要独占键盘(空格揭示、1–4 打分),被嵌到主窗口里反而会被侧栏抢焦点。

三个阶段刻意这样切:

- **复习状态存在服务端,调度算法在客户端**。多端同步要的是「这张卡现在到哪一台阶」,不是「这张卡什么时候该出现」;算法留在客户端以后调参不用动数据库。服务端只存 `stage`/`due_at`/`last_grade`/`review_count`/`lapses`/`reviewed_at` 六个结果字段。
- **四档而不是两档**。两档(认识/不认识)会让「模糊」这一档无处安放,用户只能二选一地撒谎;四档(忘记/模糊/熟悉/记住)对应 1/2/3/4 四个键,判断成本低。
- **单窗口而不是继续加窗**。窗口越多,「这个功能在哪」这个问题就越贵;侧栏一列七项一眼看完。

**算法口径**

四档 → 下一台阶 → 下次到期(`src/FastTranslation.Core/Notes/ReviewSchedule.cs`,服务端 `Domain/ReviewSchedule.cs` 是镜像,改一处必须改另一处):

| 评分 | 下一台阶 | 说明 |
| --- | --- | --- |
| 忘记 `forgot` | 1 | 回到 10 分钟后,`lapses` +1 |
| 模糊 `fuzzy` | 2 | 1 天后 |
| 熟悉 `familiar` | 3 | 3 天后 |
| 记住 `known` | 从 stage ≥ 4 起逐级 +1,否则直接 4 | 4 → 7 → 14 → 28 → 56 天,封顶 stage 7 |

台阶间隔表:`0 = 未排期`、`1 = 10 分钟`、`2 = 1 天`、`3 = 3 天`、`4 = 7 天`、`5 = 14 天`、`6 = 28 天`、`7 = 56 天`。**服务端只存结果,不参与调度**;`review_count` 每次评分 +1,`lapses` 只在「忘记」时 +1。

复习队列的组装:到期卡(`due_at <= now AND stage > 0`,按 `due_at` 升序)+ 今日新卡(`stage = 0`,按 `created_at` 倒序),新卡日上限默认 20、队列上限 50;`due` 与 `fresh` 分开返回,前端进度条用的是两者之和。

**做了什么**

服务端(`server/FastTranslation.Server/`):

| 文件 | 内容 |
| --- | --- |
| `Storage/ServerDatabase.cs` | `notes` 加 6 列(`stage`/`due_at`/`last_grade`/`review_count`/`lapses`/`reviewed_at`),走 `EnsureColumn` 而非改 `CREATE TABLE`:`CREATE TABLE IF NOT EXISTS` 对老库一行都不执行,新列只能 `ALTER TABLE` 补。`ix_notes_user_due` 也必须在 `EnsureColumn` 之后建,否则老库上建索引会因缺列报错 |
| `Storage/NoteStore.cs` | 四个新方法:`ListDue`(到期卡)、`ListFresh` + `CountFreshSince`(今日新卡与配额)、`SetReview`(写台阶、到期时间、次数与 lapses);列清单五处同步扩到 18 列 |
| `Domain/ReviewSchedule.cs`(新) | 上面那张表的镜像;服务端项目不引用 `FastTranslation.Core`,与 `Domain/NoteKind.cs`、`Domain/NoteClassifier.cs` 同理 |
| `Api/NotesEndpoints.cs` | `GET /api/v1/notes/review`(队列 + `freshQuota`)与 `POST /api/v1/notes/{id}/review`(打分);两个端点都先过「账号可用 → 有单词本权益」两道闸 |
| `Api/Contracts.cs` | `GradeNoteRequest`、`ReviewQueueResponse` |
| `Gateway/GatewayEndpoints.cs` | `POST /v1/ocr`:客户端把截图 Base64 上来,网关转 `qwen-vl-ocr`,按张计费并记入 `usage_events` |
| `Domain/Pricing.cs` | `qwen-vl-ocr` 单价条目 |

客户端:

| 文件 | 内容 |
| --- | --- |
| `Core/Notes/ReviewSchedule.cs`(新) | 调度算法、`ReviewGrade` 枚举、`IntervalLabel`、`Display`、`TryParseGrade` |
| `Core/Notes/NoteTypes.cs` | `Note` 加 6 个尾参数与 `GradeDisplay`/`DueLine` 派生属性;`ReviewQueue` 记录 |
| `Core/Notes/NotesApiClient.cs` | `ReviewQueueAsync` / `GradeAsync` |
| `Core/ImageOcrTypes.cs`(新)、`Providers.QwenMt/QwenOcrClient.cs` / `GatewayOcrProvider.cs`(新) | 截图识别的两条路:直连 DashScope 与走自建网关 |
| `App/ImageTranslationCoordinator.cs`、`Platform.Windows/ImageResultWindow.cs` | 结果窗从「一图一译」变成「图片 + 可编辑识别框 + 复制图片/另存为/保存到单词本/关闭」 |
| `App/MainWindow.xaml(.cs)`、`App/Pages/*`(新,7 个页面) | 单窗口 + 左侧栏七页:首页、单词、短语、语句、复习、套餐、设置;设置窗与账号窗被删,内容搬进页面 |
| `App/ReviewWindow.xaml(.cs)`(新) | 今日复习:题目在上、虚线槽在下,空格揭示译文,1–4 评四档,Esc 退出;评完进空态 |
| `App/AccountCoordinator.cs` | `ReviewQueueAsync` / `GradeNoteAsync`,继续走 `WithSessionAsync`(401 自动刷新) |

**验证**

- `dotnet build FastTranslation.slnx -c Release`:**0 警告 0 错误**
- `dotnet test`:4 个项目 **541 全绿**(Core 192 / QwenMt 87 / Platform.Windows 87 / Server 175),失败 0 跳过 0。本轮新增:`ReviewScheduleTests` 43 例(8 台阶 × 4 档的转移 + 自定义档测试 + 显式元组表的间隔校验)、`NotesReviewTests` 15 例(队列拆分、配额夹到 0、评分后离开新卡、非法档 400、别人的卡 404、`forgot` 只加 lapses、老库迁移补 6 列)、OCR 相关若干
- 真实服务端端到端(Development、`http://127.0.0.1:5199`、SQLite `publish/devserver/qa.db`):注册 → 存 4 条笔记 → `GET /notes/review` 返回 `due:[]`、`fresh:4`、`freshQuota:20` → 对两张卡打分 → `stage`/`due_at` 落库正确 → 再查队列,到期卡按 `due_at` 升序排出
- 界面证据(UI Automation 驱动真实 exe,不是静态稿;脚本 `publish/uia-nav.ps1`、`publish/select-row.ps1`、`publish/review-shot.ps1`、`publish/wheel-test.ps1`):

| # | 文件 | 取样位置 |
| --- | --- | --- |
| 1 | `publish/shots-v49/HomeButton.png` | 侧栏「首页」高亮,输入区 + 语言下拉 + 复制译文/存到单词本/翻译 |
| 2 | `publish/shots-v49/WordSelected.png` | 侧栏「单词」→ 选中第 1 条 `resilient` → 右侧详情「复习」四档 + 「上次记住 · 7 天后」 |
| 3 | `publish/shots-v49/PhraseButton.png` | 侧栏「短语」高亮,列表为短语 |
| 4 | `publish/shots-v49/SentenceButton.png` | 侧栏「语句」高亮,列表为整句 |
| 5 | `assets/qa/page-plan.png` | 侧栏「套餐」→ 账号卡 + 你的套餐 + 本月用量 + 激活码 + 侧栏「标准版」角标 |
| 6 | `assets/qa/page-settings.png` | 侧栏「设置」→ 五组卡片 + 底部固定动作条 |
| 7 | `assets/qa/review-mask.png` | 复习窗 1/2 未揭示:虚线槽 + 四档置灰 + 小字 10 分钟/1 天/3 天/7 天 |
| 8 | `assets/qa/review-revealed.png` | 空格揭示后:译文 + `Code.exe · 10-29` 来源行 + 四档可点 |
| 9 | `assets/qa/review-empty.png` | 队列评完:「这一轮过完了。」+ 再看看单词本 |
| 10 | `publish/shots-v49/ImageResult.png` | 译图结果窗:复制图片/另存为/保存到单词本/关闭 四个 pill + 可编辑识别框 |
| 11 | `publish/shots-v49/HomeDark.png`、`assets/qa/review-mask-dark.png` | 深色:首页与复习窗 |
| 12 | `assets/qa/main-review-badge.png` | 两张今天到期的卡 → 侧栏「复习」红点 `2`,列表行显示 `今天到期` |

  设置页的滚轮回归单独测过:指针停在 API Key 输入框上滚 5 格,`Body` 的 `VerticalScrollPercent` 从 0 → 43.57% —— 输入框不能把滚轮吃掉(`Body_PreviewMouseWheel` 隧道逻辑从 `SettingsWindow` 搬到 `SettingsPage` 后仍生效)。

**兼容性**

- 老库打开即迁移:`EnsureColumn` 逐列补 6 个复习字段,`notes` 读取位置扩到 `0..17`;迁移测试用 v47 形状的库跑过一遍。
- v1.2.x 客户端打到新服务端不受影响:`/notes/review`、`/notes/{id}/review`、`/v1/ocr` 都是新增端点;`NoteDto` 只是多出 7 个带默认值的字段,旧客户端反序列化会忽略。
- 客户端与服务端的 `ReviewSchedule` 是两份镜像代码,靠同一组单测数字(8×4 转移表)对齐;改档位必须同时改两处,两处文件头都写了这句。

**残留风险**

- **套餐页不收款,「订阅」按钮只记录意向**。真正的开通通道是激活码(v49),页面上没有支付流程;用户点「订阅」看到的是「用激活码开通」的引导。
- **OCR 单价未逐字核对**。`qwen-vl-ocr` 的 `Pricing` 条目按 DashScope 文档的图片计费档抄写,没有拿真实账单对过;计费只影响网关侧的用量记录,不影响用户能不能用。
- **复习调度的参数没有真实用户数据支撑**。10 分钟 / 1/3/7/14/28/56 天是照通用间隔重复经验取的,`MaxStage=7` 之后不再延长;真跑一段时间后可能要按留存数据调。
- **托盘原生菜单没有「复习」入口**。加一项要动 `TrayIconService` 的菜单结构(Platform.Windows),本版入口是侧栏「复习」与单词页右上角「去复习」。
- **`FT_UITEST=image-result` 是 QA 钩子**,用合成图打开译图窗只为截图;真实截图翻译路径是「热键 → 全屏框选 → OCR → 结果窗」,那段区域选择浮层是分层窗口,`PrintWindow` 拍不到内容,自动化截图必须绕开它。

## v52 部署:整套流程在真机 ECS 上跑通,修掉两个只在真机才暴露的缺陷 ✅

**为什么这样改**

v47 把 deploy/ 五个脚本写完了,但残留风险第一条就是「整套流程从没在真机跑过」。今天公钥进了 `/root/.ssh/authorized_keys`,就把这台 2 核 2G 的杭州 ECS(Ubuntu 26.04.1,`resolute`)当成验收环境,从 `bootstrap.sh` 一路跑到网关真翻译。纸面推演漏掉的两件事都在这一次撞出来了。

- **`vm.swappiness` 静默失效**。旧脚本把 sysctl 的写入放在「新建 /swapfile」的分支里,而阿里云的 Ubuntu 镜像**自带** `/swapfile`(4G),分支根本不进 —— 跑完 `sysctl -n vm.swappiness` 还是 20。修法:swappiness 的写入挪到分支之外,两条路径都写(`deploy/bootstrap.sh:11-24`)。
- **升级路径是坏的**。旧 `install.sh` 上来就 `cp -r server/. /opt/fasttranslation/`,服务在跑的时候拷自己在运行中的 apphost 上会撞 Linux 的 ETXTBSY:`cp: cannot create regular file '/opt/fasttranslation/./FastTranslation.Server': Text file busy`,`set -euo pipefail` 让脚本在第一步就退出。首装看不出来,因为首装时服务还没起过。修法:调整成「先校验生产配置 → 停服务 → 拷二进制 → 装单元/站点/备份/Caddyfile → 起服务并打健康检查」,配置校验放在停服务之前,这样配置缺失而 `exit 1` 的那次不会把已经在跑的服务撂下(`deploy/install.sh:14-49`)。
- **Caddy 装完就自己 started**。cloudsmith 的 deb 会 enable 并拉起 `caddy.service`,监听 `:80` —— 备案号还没下来,这一层不能对外。`bootstrap.sh` 现在在 `apt-get install caddy` 之后立刻 `systemctl disable --now caddy`,`install.sh` 只在 `Caddyfile` 的占位符被替换掉之后才重新 enable。
- **加固原来压根没有**。README 承诺的只有 swap/账号/目录/Caddy/libicu,`sshd -T` 实测 `permitrootlogin yes` + `passwordauthentication yes`,主机防火墙 `Status: inactive`(整台机器只靠阿里云安全组挡着)。补了一段 hardening:`/etc/ssh/sshd_config.d/99-ft.conf` 写 `PermitRootLogin prohibit-password` / `PasswordAuthentication no` / `KbdInteractiveAuthentication no` / `MaxAuthTries 4`,`sshd -t` 通过才 reload;ufw 先 `allow OpenSSH/80/443` 再 `--force enable`(顺序反了就把当前这条 SSH 掐了)。两处都是**有前提的**:authorized_keys 为空时刻意跳过 sshd 并打印警告,否则这脚本就是把自己锁在门外的最后一个动作;想全程跳过就 `FT_SKIP_HARDEN=1`。

**真机验收数据(2026-10-29,杭州 / 2 核 2G / Ubuntu 26.04.1)**

| 检查项 | 实测 |
| --- | --- |
| `bootstrap.sh` 两次(初装 + 加固后重跑) | 退出 0;`ftserver` uid 999 / gid 983;`vm.swappiness=10` 生效 |
| Caddy 源在境内 ECS 可达 | `caddy` v2.11.7,18.1MB / 16s;apt 走 `mirrors.cloud.aliyuncs.com` 内网镜像 58MB/s |
| 上传 49MB 产物 | scp 39 秒(入向不限,只有出向 3Mbps) |
| `install.sh` 首装 | `enabled + active`,`MemoryCurrent` ≈ 84 MB(可用内存 1288 MB),`/health` = `{"status":"ok"}` |
| `install.sh` 重复执行(升级) | 停→拷→起,数据不丢(2 用户 / 1 笔记),`NRestarts=0` |
| 端口暴露 | 只有 `0.0.0.0:22` 与 `127.0.0.1:5080`;Caddy disabled,80/443 无监听 |
| 网关真翻译 | `POST /v1/chat/completions` → 200 / 535ms,上游 `usage` = 43 / 17 tokens |
| 按字符记账 | 请求 71 字符 → 月额度与日额度各 +71;`usage_events` 落 `cost_micros` 75–77(≈0.0008 元/次) |
| 免费日额度闸门 | 1975 字符后的下一次 → `429` + 头 `X-FT-Quota-Reason: daily_quota_exhausted` + 体 `{"error":"daily_quota_exhausted"}`,正是客户端 `QuotaRefusal.Describe` 认的形状 |
| 跨设备隔离 | 同账号换指纹仍 200(日额度按设备算,月额度按账号算) |
| 订阅闸门 | 免费账号 `POST /api/v1/notes/` → `403 {"error":"subscription_required"}`;`POST /api/v1/admin/accounts/{email}/plan` 升 light 后 → 200 且 `quotaDaily` 变 null |
| 只读不受闸门影响 | 免费账号 `GET /api/v1/notes/` → 200(total 0) |
| 401 / refresh | 无令牌 → 401;`/auth/refresh` 换到新 access(399 字符)并轮换 refresh |
| 备份 | `ft-backup.sh` 产出 4909 字节 `.db.gz`,`/etc/cron.d/fasttranslation-backup` 04:17 UTC |
| 磁盘 | 7.1G / 40G(19%) |

**验证**

- `bash -n` 过 bootstrap 与 install;`sshd -T` 读回 `permitrootlogin prohibit-password` / `passwordauthentication no` / `maxauthtries 4`;`ufw status numbered` 四条 ALLOW(22/80/443 + v6);加固后仍能用密钥登录,隧道里的 `/health` 仍返回 ok。
- 网关/闸门/记账这一批断言全部打在真服务器上,脚本 `publish/e2e-server.mjs`、`publish/e2e-quota.mjs`(publish 不入库,记录数据后删)。上游 Key 用 DPAPI 从本机 `qwenmt-api-key.bin` 取(**熵是 `ft-qwenmt-api-key`**,见 `src/FastTranslation.Platform.Windows/DpapiSecretStore.cs:25-39` —— 传 `$null` 会抛 `InvokeMethodOnNull`),值不落对话、不落仓库,只落 `/opt/fasttranslation/appsettings.Production.json`(root:ftserver 640)。
- 本轮只改 `deploy/` 三个文件与本文档,没动任何 C#;全量 build/test 由并行会话负责,当前工作区有他们未提交的 `Pages/ReviewPage.xaml` 等改动,提交时按路径挑文件,没有 `git add -A`。

**残留风险**

1. **真 WPF 客户端还没连过这台服务器**。`src/FastTranslation.Core/Account/AccountApiClient.cs:18` 的 `DefaultBaseUrl` 仍是 `http://127.0.0.1:5199`,而服务器只在回环 5080 上;HTTP 契约(头、体、429/403 形状)已用 Node 逐条打过,但界面层的回落提示与账号窗读数要等域名 + Caddy 起来后发首个正式版才算验过。
2. **签名密钥与管理令牌目前只有两份**:服务器上的配置文件 + 本机 `publish/.server-secrets.json`(gitignore 内)。轮换 SigningKey 会让所有已登录设备的 access token 立刻失效(refresh 仍可用),轮换 AdminToken 会打断管理脚本 —— 真要换记得两处一起改。
3. **备案没下来之前 80/443 是开着的**(ufw 与安全组都放行),只是 Caddy disabled 无进程监听。备案号到手后要按 README 的 sed 替换域名并 enable caddy,顺带把 `site/index.html` 的 `__ICP_NUMBER__` 填掉。
4. **`/swapfile` 是镜像自带的 4G**,脚本不校验已存在那份的权限与 fstab 条目;换镜像或换可用区时这条要重看。
5. **单实例单进程**。SQLite WAL + `ProtectSystem=strict` 决定了这台机器不能横向加副本,升级期间会有一两秒的 502 窗口(`Restart=always` / `RestartSec=3`),对翻译工具可以接受。

## v53 部署:控制台文件管理与密钥单点(2026-10-29) ✅

**为什么这样改**: 引导脚本把 `PasswordAuthentication no` 一刀切之后,阿里云 Workbench 的「文件管理」报 `login.error.passwordAuthenticationIsProhibited` 打不开。日志(`journalctl -u ssh`)显示这些会话来自 `100.104.86.13` / `100.104.86.48` —— **文件管理走的是真实 sshd 会话并用实例密码认证**,和「免密连接」(云助手 `aliyun` agent)是两条路。同时查 `/root/.ssh/authorized_keys` 发现加固前**只有一把密钥**,就是开发机笔记本上的 `ft-workstation` —— 换电脑或重装系统就只能走控制台重置密码,是单点。

**改了什么**: 不整体回退,而是**按来源放行**。在 `/etc/ssh/sshd_config.d/99-ft.conf` 末尾加 `Match Address 100.104.0.0/16` 段(`PasswordAuthentication yes` + `PermitRootLogin yes`),阿里云内网段可用实例密码,公网仍只认密钥。`deploy/bootstrap.sh` 的 drop-in 同步补上这段(脚本用 `if [ ! -f ]` 守卫,已有该文件的机器不会自动补,需手工加,本次就是手工加的)。另外新建一把 ed25519 应急密钥 `ft-emergency-save-2026-10-29` 入 authorized_keys,私钥在开发机 `~/.ssh/ft-emergency`(另存一份 `publish/ft-emergency-copy-put-this-off-this-laptop`,**需拷到别的设备才算兜底**)。

**怎么验证**:
- `sshd -T -C user=root,host=<hostname>,addr=203.0.113.9` → `permitrootlogin prohibit-password`、`passwordauthentication no`(公网)
- `sshd -T -C user=root,host=<hostname>,addr=100.104.86.13` → `permitrootlogin yes`、`passwordauthentication yes`(阿里云内网)
- 笔记本强制品种 `-o PreferredAuthentications=password` 连公网:仍 `Permission denied (publickey)`;带密钥连:正常;`sshd -t` 通过后 `systemctl reload ssh`
- authorized_keys 现有两把:`ft-workstation`(RSA 3072)与 `ft-emergency-save-2026-10-29`(ed25519);`/etc/passwd` 里只有 `root` 有登录 shell

**残留风险**: ①`Match Address` 的可信度依赖来源 IP 不被伪造 —— 100.104.0.0/16 是阿里云内网服务段,不可从公网路由,但仍属「按 IP 白名单放行弱面」的折中。②Ubuntu 26.04 默认开 `PerSourcePenalties`,日志里 `198.51.100.10` 的大量连接被判 `connections without attempting authentication` 丢弃;若 Workbench 复用同一批来源 IP,文件管理可能出现偶发失败,届时需对该段关掉罚分。③实例密码仍可用于控制台 VNC 与 `sudo`,这两条路不受本改动影响。④上一版把「实例没设密码」当成事实是错的,实测 `passwd -S root` = `P 2026-10-29`。
