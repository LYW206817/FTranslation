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
