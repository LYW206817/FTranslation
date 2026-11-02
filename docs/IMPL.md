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
- 全站日预算 5 元是**止损开关,不是防刷**:被刷爆的后果是所有人一起回落到自带 Key(还算体面),但当天免费体验就没了。没有 IP/设备级限速,只有每账号 40 请求/分钟。**语音功能评估时发现的连带问题**:语音也要花钱且音频按秒连续计,5 元/日全站熔断只够 5.8 小时语音,且熔断会连文本翻译一起停 → 语音必须拆独立预算桶 `Gateway:Speech:DailyBudgetMicros`(见 `docs/superpowers/specs/2026-10-31-语音与文档翻译-方案评估.md` §3.5、§6.2)。
- 激活码还没做,`plan_expires_at` 只能靠管理接口手填;真开卖前必须补 `POST /api/v1/notes` 之外的兑换链路,否则每来一个用户都要手工改库。
- 图片翻译按 `ImageQuotaChars=1000` 折字符记账,与真实成本(约 0.004 元/张)只是粗略对应;截图翻译量大时会低估消耗。
- 网关让**所有**登录用户的翻译都经过服务器,~~香港国际型线路对内地用户有延迟与丢包(设计 §2.4 已记)~~ **该顾虑已作废**:实际部署在**阿里云华东1 杭州**(境内),实测 `ping YOUR_SERVER_IP` 24ms / 0% 丢包。真正的容量约束改成**出向 3 Mbps 带宽**(入向不限,`docs/IMPL.md:1125` 实测),纯文本翻译无压力,但流式中继(语音)约 11 路并发就到顶(见 `docs/superpowers/specs/2026-10-31-语音与文档翻译-方案评估.md` §3.5);单机 ASP.NET 没有水平扩展方案,用户量上来要先解决这个。
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

## v54 复习进页内 + 套餐与设置改两列 ✅

**为什么**:v51 把复习做成一扇独立窗口,从单窗口外壳点出去再弹新窗,和「一扇窗七页」的其余部分不一致;套餐页与设置页则各自把一个 720/760 宽的竖列靠左摆,窗口右侧整片空白,像没做完。两件事都是「形状和内容不匹配」,一起改。

**复习改成页内**(`feat(复习): 复习改成主窗口内的一页`,2eadbef)
- 流程:侧栏「复习」→ 页内列出今天要看的条目(单词/短语/语句混排,原文 + 译文 + 「今天到期」或「今日新词」)→ 每行复选框 + 顶部全选 →「开始复习(6)」→ 同一页进入逐卡复习(空格揭示,1-4 评四档,跳过不算评过)→ 「这一轮过完了」可「再选一批」或回单词本
- 新增 `src/FastTranslation.Core/Notes/ReviewSession.cs`:`ReviewSession` 只持有「勾选了哪些卡、当前第几张」,界面不管顺序;12 个单测覆盖跳过、评完、越界、空队列
- 删除 `ReviewWindow.xaml(.cs)`;`App.xaml.cs` 去掉 `ShowReview`;`MainWindow` 去掉 `ReviewRequested`,`ShowPage(MainPageId.Review)` 就是入口
- 键盘:页面宿主的空格/1-4 靠 `PreviewKeyDown` 隧道处理,只在卡片模式接管;否则在列表页打字会被吃掉
- 四档按钮条 `MaxWidth=620` 居中。满宽铺开时四个按钮各 320px,像工具栏;`HorizontalAlignment=Center` 又会退化成内容宽度(一排小按钮),正确写法是保留 Stretch 只加 MaxWidth —— WPF 在 Stretch + MaxWidth 受约束时居中排布

**套餐与设置改两列**(`feat(界面): 套餐与设置改成两列`,93ff05a)
- 两页都用 `MaxWidth=980` 的居中容器;依据 ui-ux-pro-max 的「正文行宽 65-75ch、不要让文本铺满视口」——不是把内容拉满屏,而是把宽度用在两列上
- 套餐页:横跨顶部的账号卡(邮箱/套餐/刷新用量/退出登录)+ 左列「能买什么」(套餐卡、月付年付、订阅)+ 右列「用了多少」(本月/今日用量、激活码)
- 套餐页未登录态:左列登录表单,右列新增「登录后有什么不一样」三条(免费额度按账号算 / 生词本存在服务端 / 复习进度跟着账号走),原来这半边是空的
- 设置页:左列「它怎么给我翻译」(翻译与外观、取词与写回、系统),右列「它连到哪里」(服务、排除应用);底部动作条加「这些设置只存在这台机器上,不随账号同步」

**顺带修掉的两个真 bug**
1. `MainWindow.ShowPage` 只在「页面已缓存」时调 `Reload`,第一次进入的页面是 `Build` 出来的、从没读过数据。表现是套餐页首访用量空白(要点一次「刷新用量」才出数字)、单词本首访永远显示不出内容(点「刷新」才有空态文案)。改成 `Build` 之后也走一次 `RefreshPage`,缓存命中与新建两条路径用同一个方法。
2. `PlanPage.OnAccountChanged` 只重画不重取用量。启动时的会话恢复是异步的,账号晚到就再也不会补取,用量卡一直空白。现在账号变化时一并 `LoadUsageAsync`。

**验证**
- `dotnet build FastTranslation.slnx -c Release` → 0 警告 0 错误
- `dotnet test` → 548 全绿(Core 199 / Platform.Windows 87 / QwenMt 87 / Server 175)
- 截图(1820×1155 = 1040×660 逻辑 × 175% DPI,取样即在整张窗口图上直读):
  - `assets/qa/layout-plan.png`:左列套餐卡,右列用量卡(首访即有 `0 / 10,000 字符(本月)`)与激活码卡
  - `assets/qa/layout-settings.png`:左列三个分组,右列两个分组,两列底边基本齐平
  - `assets/qa/layout-word.png`:首访即显示「这一栏还是空的」空态,而不是空白列表
  - `assets/qa/review-pick.png` / `review-card.png` / `review-revealed-inpage.png` / `review-done.png` / `review-pick-empty.png`:复习五态
- 本地开发服务端:`Server__SigningKey=ft-dev-signing-key-0123456789abcdef`、DB `publish/devserver/qa.db`、`http://127.0.0.1:5199`;客户端设置页「账号服务地址」指向它,截图里的账号是 `qa-layout@example.com`(免费版)

**兼容性**:服务端零改动,数据库零改动;`ReviewWindow` 删除后不再有机型依赖。客户端设置的键名未变,老 `settings.json` 直接可用。

**残留风险**:①复习页的键盘处理只覆盖空格/1-4/回车/Esc,输入法组合键仍会先到输入框(页面里没有可聚焦输入框,实际无影响)。②两列宽度按 1040 逻辑宽设计,窗口拖到很窄时两列会各让一半宽度,未做断点折叠。③`ReviewSession` 只保证顺序,重复点击「开始复习」不会重新洗牌,题目顺序恒为队列顺序。

## v55 账号与套餐拆页:改名称、改密码 ✅

**为什么**:套餐页原来同时承担「我是谁」和「买什么」两件事——已登录的人想看一眼剩余额度或改个名字,得先经过一张价格表;未登录的人进套餐页先撞上登录表单,而他真正想看的可能是价格。v54 把两页铺成两列后,这个问题反而更明显:左上角那张横跨整行的账号卡里塞着邮箱、套餐、刷新、退出登录,和下面的订阅卡抢注意力。拆页的判据是「这一页回答一个问题」:账号页回答「我现在是什么身份、还剩多少」,套餐页回答「我能买什么、额度怎么算」。

**页面结构**(`feat(账号): 拆出账号页`,deb7f2a)
- 侧栏在「套餐」上方插入「账号」,`MainPageId` 加 `Account`,`ShowPage` 走同一个 `RefreshPage` 通道
- 账号页已登录态:圆形首字头像(`AccountProfile.Initial`,中文取首字、邮箱取首字母大写)+ 名称 + 套餐角标 + 邮箱;右侧「刷新」「退出登录」
  - 左列「当前套餐」(`PlanNameText`/`PlanValidityText`/本月与今日 `MeterBar`/「看套餐与价格」跳套餐页)
  - 左列「修改名称」、右列「修改密码」、右列「数据」说明(生词本与复习进度都在服务端,退出登录不会删)
  - 没设名称时不再重复显示邮箱:第二行 `EmailText` 隐藏,第一行 `DisplayNameOrEmail` 已经是邮箱
- 账号页未登录态:左列登录表单(邮箱/密码/验证码/去注册),右列「登录后有什么不一样」三条(免费额度按账号算 / 生词本存在服务端 / 复习进度跟着账号走),底部「账号:未登录」
- 套餐页删掉账号卡,只留订阅卡、「包括什么」三条、用量、额度怎么重置(月/日/复习队列三段)、激活码

**服务端两条写接口**
- `PATCH /api/v1/account/profile`(`UpdateProfileRequest(string? DisplayName)`):去空白后 ≤24 字符、拒绝控制字符(名称会进窗口标题区与侧栏,换行会撑坏布局)、空串表示清空回落到邮箱。返回更新后的 `AccountDto`
- `POST /api/v1/account/password`(`ChangePasswordRequest(CurrentPassword, NewPassword, DeviceFingerprint, DeviceName)`):当前密码错 `400 password_mismatch`,新密码 <8 位 `400 password_too_short`,与旧密码相同 `400 password_unchanged`;成功后 `RevokeAllRefreshTokens` + 给本机换发一对 `AuthResponse`,所以「这台不用重新登录,别的设备下次刷新令牌时被拒」
- `users` 表加 `display_name TEXT NOT NULL DEFAULT ''`,走 `EnsureColumn` 热迁移;`AccountDto` 末尾加带默认值的 `DisplayName`,老客户端解析不受影响
- `AuthEndpoints.IssueTokens` 由 `private` 改 `internal`,让改密码复用同一套发牌逻辑,不复制粘贴

**客户端**
- `AccountApiClient.UpdateProfileAsync` / `ChangePasswordAsync`;`AccountErrorCode` 已有 `InvalidProfile`/`PasswordMismatch`,只补 `MapFailure` 的两个 case
- `AccountCoordinator.UpdateProfileAsync` / `ChangePasswordAsync`,都走 `WithSessionAsync` 并回写会话(改密码会换 refresh token,不写回下次就 401)
- `AccountProfile.DisplayName` + `DisplayNameOrEmail` + `Initial`

**验证**
- `dotnet build FastTranslation.slnx -c Release` → 0 警告 0 错误
- `dotnet test` → **560 全绿**(Core 203 / Platform.Windows 87 / QwenMt 87 / Server 183);v54 是 548,本次 +12:8 个服务端用例(`AccountProfileTests`:改名/清空/超长与换行被拒/未登录 401/当前密码错/新密码过短或重复/换密码后本机仍可用且旧密码失效/换密码踢掉其它设备的 refresh token)+ 4 个客户端映射用例(`AccountClientTests`:PATCH 路径与字段、错误码映射、改密码字段完整性、`password_mismatch` 映射)
- 提交后用 `git worktree add` 单独检出 `deb7f2a` 重跑 build 与 test,确认这一提交自身可编译可测试(工作区里还有别的会话未完成改动,不能只看工作区)
- 截图(1820×1155 = 1040×660 逻辑 × 175% DPI,取样即在窗口图上直读):
  - `assets/qa/account-signedin-free.png`:账号页已登录(头像「Q」、邮箱、免费版角标、本月 `0 / 10,000 字符`、今日 `0 / 2,000 字符`、改名称、改密码、数据说明)
  - `assets/qa/account-named.png`:点「保存名称」后头像变「永」、名称变「永威」、状态行「名字已保存。」,服务端 `display_name` 已落库
  - `assets/qa/account-signedout.png`:未登录双栏(左表单、右「登录后有什么不一样」、底部「账号:未登录」)
  - `assets/qa/account-dark.png`:深色下的账号页
  - `assets/qa/plan-trimmed.png`:精简后的套餐页(左订阅卡 + 包括什么,右用量 + 额度怎么重置 + 激活码)

**兼容性**:数据库只加一列且带默认值,老库自动迁移;`AccountDto` 新字段在末尾且可缺省。客户端删掉了 `PlanPage` 里的账号卡与 `RefreshAccountStatus` 调用,`App.xaml.cs` 的 `RefreshAccountStatus` 改为只更新侧栏与窗口标题。

**残留风险**:①改名称与改密码都必须联网,离线时按钮报「连不上账号服务器」,没有做离线排队。②改密码后旧 access token 在 30 分钟内仍然有效(服务端只撤销 refresh token),这是故意的,不做即时踢下线。③名称不去重,两个人可以都叫「永威」;账号页只用邮箱做唯一标识,没有暴露用户 id。④`EnsureColumn` 只加列不改类型,`display_name` 将来若想加长度约束需另写迁移。

## v56 激活码补完:作废与台账、跨档兑换不再亏天数 ✅

**为什么**:v49 把码做到了能发能兑,但运营侧三件事没落地(v49 自己列的残留风险):①发出去的码用没用,只能拿 sqlite3 手查;②打错批次、发错人、泄露的码没有任何作废手段;③跨档兑换亏客户。第三条是这次真机 E2E 实测出来的:手里 light/30 天(到期 1798446194),再兑一张 standard/30 天,到期变成 1795854194 —— **反而少 30 天**。根因在 `RedeemEndpoints.cs` 的 `stacks` 判断只在同档时才叠加,跨档走 `now + days` 把剩余天数扔了。而降档早已被 400 `redeem_plan_downgrade` 挡在门外,所以能走到这一步必然是同档或升档,没有任何理由重设计时起点。

**服务端**
- `server/FastTranslation.Server/Storage/ServerDatabase.cs`:redeem_codes 建表补 `revoked_at INTEGER`,并在 `Initialize()` 里 `EnsureColumn(connection, "redeem_codes", "revoked_at", "INTEGER")` —— 生产机这张表已经存在,`CREATE TABLE IF NOT EXISTS` 永远不会补列
- `server/FastTranslation.Server/Storage/RedeemStore.cs`:`RedeemCodeRecord` 加 `long? RevokedAt` + `IsRevoked`;`TryClaim` 的 WHERE 改成 `code = $code AND redeemed_at IS NULL AND revoked_at IS NULL`(**作废挡在存储层,不是只在大端点判一下**,并发兑换也不可能绕过);新增 `Revoke(code, now)`(只撤未兑的)、`LedgerRow(...)`、`List(status, take, skip)`(LEFT JOIN users 取买家邮箱,`ORDER BY created_at DESC, code`)、`Count(status)`、`IsKnownStatus`;四种 fate 的 WHERE 片段集中在私有 `Where(status)` 一处,保证 List 与 Count 口径不会分叉
- `server/FastTranslation.Server/Api/RedeemEndpoints.cs`:兑换分支在 `redeem_code_used`(409)之后插入 `redeem_code_revoked`(**410 Gone**);到期计算改成公平叠加 —— `long from = now; if (user.PlanExpiresAt is { } current && current > now) { from = current; } var expiresAt = from + (record.Days * SecondsPerDay);`
- 新增 `RedeemAdminEndpoints`:`GET /api/v1/admin/codes?status=open|used|revoked|all&limit=&offset=`(status 缺省 open,未知 status 400 `status_invalid`,limit 默认 50 上限 200)与 `POST /api/v1/admin/codes/{code}/revoke`(幂等 200 `code_revoked`;已兑换 409 `code_already_redeemed`;未知码 404 `code_missing`;规范化失败 400 `redeem_code_invalid`),两条都挂 `AdminEndpoints.RequireAdminToken`;`Program.cs` 里 `app.MapAdminEndpoints()` 之后 `app.MapRedeemAdminEndpoints()`
- `server/FastTranslation.Server/Api/Contracts.cs`:`RedeemCodeDto(Code, Plan, Days, Note, CreatedAt, RedeemedEmail, RedeemedAt, RevokedAt)`、`RedeemCodePageResponse(Items, Total, Limit, Offset)`
- 码的键形态没动:行按**打印形态** `FT-XXXX-XXXX-XXXX-XXXX` 存,端点先把用户输入 `Normalize` 再 `Format` 回来查(`RedeemEndpoints.cs:37-42`),这样运营在数据库里 grep 到的就是当初打印出来的那一串

**客户端**
- `src/FastTranslation.Core/Account/AccountTypes.cs`:`AccountErrorCode` 尾部加 `CodeRevoked`,`Display` 文案「这张激活码已经作废了,请联系我们。」
- `src/FastTranslation.Core/Account/AccountApiClient.cs`:`MapFailure` 加 `case "redeem_code_revoked"`
- 套餐页零改动:`Pages/PlanPage.xaml.cs` 的 `Redeem_Click` 统一读 `Error.Display`,新错误码自动露出

**验证**
- 四个测试项目 **571 全绿**:Core 204 / Platform.Windows 87 / Providers.QwenMt 87 / Server 193;`dotnet build src/FastTranslation.App -c Release -o publish/tmp-appbuild` → **0 警告 0 错误**(整套 `FastTranslation.slnx` 的 Release build 会被另一会话正在运行的 FastTranslation.exe 锁住 App 输出目录,是文件占用不是代码问题,故改用独立输出目录验证 App 侧编译)
- 新增 10 个测试。`tests/FastTranslation.Server.Tests/RedeemStoreTests.cs`(4 个,新建):作废的码 `TryClaim` 返回 false、已兑出去的撤不回、open/used/revoked/all 四种计数互斥、**旧库补列迁移**(手工建 pre-v56 的 redeem_codes 表 + 插一张老码,`Initialize()` 后 PRAGMA 里确有 `revoked_at`,老码照样能查到、能作废)
- `tests/FastTranslation.Server.Tests/RedeemEndpointTests.cs` 新增 6 个:`Redeem_RefusesARevokedCode`(410 + `redeem_code_revoked`)、`Redeem_UpgradeKeepsTheDaysAlreadyBought`(light/30 在手兑 standard/30,断言升档后到期时间比兑换前**更远**且差值 ≈ 30 天 —— 就是这次实测的亏天数场景)、`Admin_ListsCodesByFate`(open 里只见到没兑的那张且 `RedeemedEmail` 为空,used 里那张带上买家邮箱,`status=all&limit=1` 只回 1 行而 `Total` 仍是全量)、`Admin_ListRejectsAnUnknownStatus`、`Admin_RevokeIsIdempotentAndCannotClawBackSpentCodes`、`Admin_CodeRoutesNeedTheAdminToken`(无令牌 GET/POST 都 403)
- 真机 E2E(YOUR_SERVER_IP,服务只听 127.0.0.1:5080,走 ssh 隧道)在**部署了 v49 的生产机**上跑通收钱链路:注册 free → `POST /api/v1/notes/` **403 `subscription_required`** → admin 发码 → `POST /api/v1/redeem` 200 → 同一账号再保存 **200**(id=b486f96d-05c0-4fe3-be91-2bd71dea6315,kind=word);同码复用 409 `redeem_code_used`;同一张码的四种书写形态(横杠/无横杠/空格分隔/裸 16 位)全部认;standard 在手兑 light 码 → 400 `redeem_plan_downgrade` 且**码未被消耗**(`TryClaim` 在所有检查之后)。跨档亏天数就是这一轮暴露的,已修。当天 16:54 整套发到生产机(YOUR_SERVER_IP):`install.sh` 原地升级,迁移前先跑 `ft-backup.sh` 留下 `fasttranslation-20261029T085403Z.db.gz`(6.3K),起服务后 `/health` = `{"status":"ok"}`。真机验收 8 项:① 老库 `PRAGMA table_info(redeem_codes)` 由 7 列补到 8 列(多出 `revoked_at`),迁移前那 7 张已兑码在台账里照样读得到买家邮箱;② `POST /api/v1/admin/codes` 发 light×2 + standard×1;③ 新注册 free 账号兑 light/30 到期 1795856084,再兑 standard/30 到期 1798448084,**差 30.0 天**(同一条序列在修复前是 −30 天);④ 对没兑的那张连撤两次都是 200 `code_revoked`;⑤ 拿作废的码去兑 → **410 `{"error":"redeem_code_revoked"}`**;⑥ 台账 `status=open|used|revoked|all` 的 total = 0 / 7 / 1 / 8,四种 fate 互斥且总数对得上;⑦ 未知 status 400、无令牌 403;⑧ 清掉 E2E 造的 6 个账号 / 8 张码 / 27 条用量事件 / 2 条笔记后全库 0 行,服务仍 active —— 对外开放时第一个注册的人是干净的。

**兼容性**:只加一列且可空,已有库由 `EnsureColumn` 自动补;新路由全在 `/api/v1/admin/` 下,不改既有 `/api/v1/admin/codes`(发码)与 `/metrics`;`AccountErrorCode` 新增值在枚举尾部;老客户端遇到 410 的 `redeem_code_revoked` 会落到映射的默认分支,显示「账号服务器出错了,请稍后再试。」而不是报错崩溃。

**残留风险**:①码仍然没有有效期,这是故意的(v49 定的「不惩罚屯码」),要按批次过期得再加一列。②作废只能撤未兑换的;已兑出去的期数要收回只能靠 `POST /api/v1/admin/accounts/{email}/plan` 手工降档,没有「退款」这个概念。③发码响应是码的唯一副本(bearer instrument,库里只有码没有批次凭据),运营发一批必须自己存一批,台账接口只能查已发出的,不能替谁找回没保存的那份。④兑换与注册/登录共用按 IP 的 20 次/分限流(`RateLimiting.AuthPolicy`),真有人拿码表来刷会把同 IP 的登录一起挤掉 —— v49 已记,本次仍未动。⑤公平叠加后,升档时买家把 light 的剩余天数并进 standard 的到期,等于「standard 的价格 + 附送几天」,这是刻意选择,定价时要算进去。⑥生产库已清空(部署当天删掉我自己造的 6 个测试账号与 8 张码),所以 v56 的迁移路径只在「有存量数据」的备份里验证过,真出现存量用户后再次升级仍需先看备份。

## v57 侧栏顺序、激活码落位、网关异常回落 ✅

**为什么**:v55 拆完账号页之后,用户拿真机截图回了三条:①划词翻译的结果卡只剩一句红字「翻译失败,请重试」;②套餐页激活码单独挂在右列末尾,左列比右列矮一大截;③侧栏顺序希望是 套餐、账号、设置。

第一条不能靠猜。`SelectionCoordinator.RunSessionAsync` 的 `catch (Exception)` 把异常吞掉只留一句兜底文案,而这个类被 `App` 构造时压根没传 `log`——**线上出现这句话时没有任何线索可查**,这本身就是个缺陷。所以这一轮先把"看不见"变成"看得见",再去修能修的那部分:

- `src/FastTranslation.App/App.xaml.cs`:构造 `SelectionCoordinator` 时补上 `log: Trace`,`%TEMP%\ft-uitest-errors.log` 里会出现 `coordinator error: ...`。`Trace` 本来就存在(FT_UITEST 钩子用它),不需要新机制。
- 新增 QA 钩子 `FT_UITEST=translate`:启动后 1.8 秒跑一次**真实**的翻译链路(先 `ShowLoading` 再 `TranslateAsync` 再 `ShowCard`,和生产路径同序),把 `ok / err.Code / notice / 译文` 写进同一份日志。不模拟划词,所以桌面忙的时候也能用。用它在用户当前配置(账号服务地址为默认 `http://127.0.0.1:5199`、本机无服务端在跑、已登录一个旧会话)下复现:**`ok=True`,译文正常**,网关连不上时回落用户自己的 Key 这条链路是通的。也就是说那句红字来自一次性与环境相关的异常,不是链路设计问题——日志补上之后才有下次的定性依据。
- `src/FastTranslation.Providers.QwenMt/GatewayTranslationProvider.cs`:`_gateway` 回调整个包进 try。网关侧抛异常(取令牌、拼地址、意外的运行时错误)现在按"网关这次没结果"处理,继续走用户自己的 Key;只有**调用方自己取消**(`cancellationToken.IsCancellationRequested`)才原样上抛,交给 `SelectionCoordinator` 的取消分支静默处理。此前这类异常会让整次翻译直接落到兜底文案,而它其实与翻译本身无关。

另外两条是纯排版:侧栏 `<Button>` 顺序换成 套餐(带套餐名角标)→ 账号 → 设置,套餐名从账号行挪到套餐行(读作"当前套餐");套餐页把激活码卡片从左列尾搬到"包括什么"下面、右列只留用量与额度说明,两列高度对齐。

**验证**
- `dotnet build FastTranslation.slnx -c Release` → 0 警告 0 错误。
- `dotnet test FastTranslation.slnx -c Release --no-build` → **573 全绿**(Core 204 / Platform.Windows 87 / QwenMt **89** / Server 193),QwenMt 比 v56 多 2 个:新增 `GatewayThrowing_FallsBackToTheOwnKey`(网关抛 `HttpRequestException` → 用自己 Key 的译文,`own.Calls == 1`)与 `CallerCancelled_StillThrows`(调用方取消必须上抛、且不碰自己的 Key)。
- 截图:`assets/qa/plan-cols.png`(1820×1155,1040×660 逻辑 × 175% DPI;侧栏 套餐/账号/设置 顺序、激活码在左列、两列底部齐平)、`assets/qa/account-after-reorder.png`(账号页在新顺序下仍正常,套餐名角标挂在「套餐」行)。
- `FT_UITEST=translate` 实测日志:`translate hook: ok=True err=/ notice= out=那只敏捷的棕色狐狸跳过了那只懒狗。`

**兼容性**:无接口与数据结构变化;`GatewayTranslationProvider` 只多一层 catch,原有五条回落规则(网关未登录 → 用自己 Key;网关成功 → 用网关;取消/超长 → 不重试;其余失败 → 用自己 Key 并附说明)全部保持,`Cancelled_DoesNotFallBack` 等旧用例未改仍绿。

**残留风险**:①用户那次「翻译失败,请重试」在本机没能复现,现在只能保证**下次**一定留下 `coordinator error:` 行;真要定位还得等它再出现一次。②账号服务仍未部署到公网(默认 `AccountBaseUrl` 还是 `http://127.0.0.1:5199`),所以没架服务端时用量卡会显示「连不上账号服务器,请检查网络后重试。」——文案归文案,这不是网络问题,是还没有地址可连,v50 的域名槽没落地之前会一直这样。③`FT_UITEST=translate` 用的固定英文样例,中文选区的路径没被它覆盖。
## v58 首译提速:网关探测、连接被拒不重试 ✅

用户反馈「第一次启动之后的第一次翻译相较于后面几次很慢」。实测确实如此,而且慢得离谱:冷启动后第一次翻译约 **5 秒**,之后每次 **0.2 秒**。查下来是三个问题叠在一起,其中两个是真 bug。

**一、`QwenMtProvider` 改写了别人给的 `HttpClient.Timeout`(真 bug,影响正常生产)**

构造里原来无条件执行 `_http.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds)`。网关链路每个请求都会 `new QwenMtProvider(..., _gatewayHttp)` 复用同一个 `HttpClient`,而 `HttpClient` **只要发过一次请求就拒绝再改 Timeout**,抛 `InvalidOperationException: This instance has already started one or more requests`。于是:

- 第一次翻译:网关正常被问一次(超时设置成功);
- 第二次起:`new QwenMtProvider` 直接抛异常 → 被 `GatewayTranslationProvider` 的 catch 当成「网关这次没结果」→ 静默回落到用户自己的 Key。

也就是说**网关只会被问一次,之后永远被跳过**——在网关活着、额度在扣的生产环境下,这会让「先用网关、用完再回落自己的 Key」的整套策略失效,而且完全无声。修法:只给自己造的那个 client 设超时,别人给的一律不动(`_ownsHttp` 的语义顺便对齐)。

**二、「积极拒绝」不该重试(真 bug,让每次翻译白等 2 秒)**

`QwenMtProvider` 的异常重试对所有网络异常一视同仁:失败 → 等 400ms → 再来一次。而本机网关没起的时候,"目标计算机积极拒绝"重试一次结果一模一样。更要命的是这台机器上**连一个没人监听的本地端口要 2 秒才拿到 RST**(裸 `TcpClient.ConnectAsync` 也一样,和 .NET、和代理设置都无关,是系统层的过滤驱动/代理驱动拖的),两次就是 4.5 秒。

修法:新增 `IsWorthRetrying` —— `ConnectionRefused / HostNotFound / NoData` 不重试,超时与连接重置照旧重试一次。三个新单测钉住:`ConnectionRefused_IsNotRetried`(只发 1 次)、`ConnectionReset_IsStillRetriedOnce`(发 2 次)、`InjectedHttpClient_KeepsItsOwnTimeout`(构造不再动 `Timeout`)。

**三、连不上的网关要记住,别人不用陪着等**

即使只重试一次,每次翻译仍要花 2 秒去问一个注定被拒的网关。加了两个机制:

- 启动时 `ProbeGatewayAsync` 用一次 `TcpClient.ConnectAsync`(3 秒上限)探网关端口。**先乐观地标记「不可用 60 秒」再探**,探通就立刻清掉——不先占位的话,探测本身要等两秒,启动后头几秒里发生的第一次翻译仍会把这 2 秒吃进去。
- 文本 / 图片 / OCR 三条网关链路都先看这个标记(`_gatewayDownUntil`),真发请求失败且错误码是 `Network` 时也写一次标记,60 秒后自动重试。429、额度用完、401 这些是网关**活着并且在回答**,不标记。

**实测(同一台机器、本机网关未启动、用户自己的 Key 有效)**

| 场景 | 改前 | 改后 |
|---|---|---|
| 冷启动后第 1 次翻译 | 5076 ms(其中网关两次连接 4510 ms) | **565 ms** |
| 第 2 次 | 231 ms | 216 ms |
| 第 3 次 | 216 ms | 293 ms |

剩下的 565 − 216 ≈ 350 ms 是到 DashScope 的 TLS 握手 + 首次 JIT,属于正常的冷启动代价;没有为了这 350 ms 去加人工预热请求。

**同时补的观测手段**:`FT_UITEST=translate` 钩子改成连续跑三次真实链路(三句不同文本,绕开 LRU 缓存),每次分别记录 `loading / call / provider / card` 四段耗时,这组数字是上面那张表的来源。

**验证**
- `dotnet build FastTranslation.slnx -c Release` → 0 警告 0 错误。
- `dotnet test FastTranslation.slnx -c Release --no-build` → **576 全绿**(Core 204 / Platform.Windows 87 / QwenMt **92** / Server 193),QwenMt 比 v57 多 3 个(见上)。
- 版本号三处同步到 **1.3.4**。

**改动文件**:`src/FastTranslation.Providers.QwenMt/QwenMtProvider.cs`(超时归属、`IsWorthRetrying`)、`src/FastTranslation.App/App.xaml.cs`(`CreateApiHttpClient` 统一三个 HttpClient、`ProbeGatewayAsync`、`_gatewayDownUntil`/`NoteGatewayFailure`、`FT_UITEST=translate` 三段计时)、`tests/FastTranslation.Providers.QwenMt.Tests/QwenMtProviderTests.cs`。

**残留风险**:①`_gatewayDownUntil` 是进程内的,换机器/重启都要重新探一次;②网关恢复后最长要等 60 秒才会再被问到;③探测只连 TCP 不看 HTTP,端口通但服务 500 的情况仍按原逻辑走(会重试一次)。④那 350 ms 的 TLS 冷启动没有处理,如果以后要求「第一次也 <300 ms」再考虑预热连接。


## v59 注释全面中文化（AGENTS 注释规范改为「一律中文」）

**背景**
- 用户要求仓库内注释一律中文，历史注释同样翻译。
- `AGENTS.md` 的「注释」小节第一条从「语言跟随文件」改为「**一律用中文**」，并明确标识符、类型名、路径、URL、命令、版本号、错误码与代码片段保持原样；第三方文件（如 `installer/Languages/ChineseSimplified.isl`）不动。

**改动**
- 范围：174 个已跟踪文本文件（+1583/−1643 行），只改注释文字，不动代码、字符串字面量与标识符。
- 分布：tools 32、server 30、tests 30、App 26、Core 20、Platform.Windows 18、deploy 7、Providers.QwenMt 3、installer 2、站点与根配置 6。
- 类型：`.cs` 118、`.xaml` 8、`.ps1` 32、`.sh` 4，`.iss`/`.wxs`/`.py`/`.css`/`.csproj`/`.props`/`.gitignore`/`.gitattributes`/`Caddyfile`/`.service`/cron 各 1。
- 26 个原本无 BOM 的 `.ps1` 补 BOM（PowerShell 5.1 读中文注释不乱码）；其余文件编码、行尾保持原状。
- 保留英文：标识符、类型名、路径、URL、命令、版本号、错误码、引用的英文异常消息，以及 `<summary>` 等 XML 标签本身。

**验证**
- 代码不变性：`publish/verify_comments.py` 按语言剥离注释后与 `HEAD` 逐行比较，174 个文件 0 处代码差异；残留英文散文注释 0。
- 语法：32 个改动 `.ps1` 全部通过 PowerShell AST 解析；4 个 `.sh` 通过 `bash -n`；11 个 XAML/XML/csproj/wxs 可解析；`.py` 可编译；`installer/FastTranslation.iss` 用 Inno Setup 6 编译通过（生成 1.3.4 安装包）。
- `dotnet build FastTranslation.slnx -c Release`：0 警告 0 错误；`dotnet test`：576 全绿（Core 204 / Platform.Windows 87 / Providers.QwenMt 92 / Server 193）。
- 注释中的中文标点统一为全角，行内代码片段、路径与命令保持半角。

**风险**
- 纯注释改动，无行为变化；构建、测试与安装器编译均证实。
- `core.autocrlf` 下 `git` 会提示 “LF will be replaced by CRLF”，属仓库既有归一化行为，与本次无关。
- `deploy/README.md` 等既有中文文档里的半角标点未在本轮统一。

## v60 配色与动效:四档自评色、页面渐变、卡片与悬停 ✅

**背景**
- 用户用 `/frontend-agent /ui-ux-pro-max` 提了三条：四档按钮要能靠颜色一眼分清（靖绿 / 雾蓝 / 橘黄 / 鹤红）、整页底色要换成从左上到右下的珠光白→雾蓝渐变、按钮与卡片要有悬停浮起与换页衔接。
- `ui-ux-pro-max` 的本地库没有 WPF 栈的条目（已如实告知用户）；它给的产品色板与仓库既定调色板不一致，按 `AGENTS.md`「配色只走 `ThemePalette`」不采纳，只取两条通用规则：动效必须给「减少动态效果」留退路、进入与离开要用不同缓动。

**改动**
- `src/FastTranslation.Platform.Windows/ThemePalette.cs`：`ThemeColors` 从 17 个字段扩到 32 个，新增四档自评的「文字 / 底色 / 描边」共 12 条、页面与侧栏渐变端点 4 条、投影色 1 条；浅深两套各自给值（浅色靖绿 `#0E7C66`、雾蓝 `#35648F`、橘黄 `#A9611A`、鹤红 `#A9453B`）。
- `src/FastTranslation.App/ThemeBridge.cs`：新增 12 个画刷键、`ShadowColor`（`DropShadowEffect` 要的是 `Color` 不是 `Brush`）、`LiftShadow`（悬停抬起，Blur 14 / Depth 2）、`CardShadow`（卡片常驻，Blur 18 / Depth 1 / Opacity 0.55），以及 `SetGradient` 组装的两支渐变（页面 0.15→0.85、侧栏 0→1，侧栏只取同一色相更窄的一段）。
- `src/FastTranslation.App/App.xaml`：默认按钮与主按钮重写模板——圆角 8、悬停抬起 1.5px 并叠一层淡投影、按下回沉 1px；新增全局 `Card`（圆角 12 + `CardShadow`）；新增四档样式族 `GradeButton` 与四个变体（悬停叠 9% 同色 veil、按下 16% 并下沉、禁用转中性灰）；`SidebarItem` 悬停整行右移 2px。
- `src/FastTranslation.App/MainWindow.xaml(.cs)`：窗口底色改 `PageGradientBrush`、侧栏改 `SidebarGradientBrush`；`ShowPage` 换页时新页面淡入（170ms）并从下方 10px 就位（210ms），只动页面宿主不动侧栏。
- 六个页面 `UserControl` 的 `Background` 由 `WindowBgBrush` 改成 `Transparent`，让渐变从窗口透上来；三份重复的本地 `Card` 样式删掉，统一用全局那份。
- `src/FastTranslation.App/Pages/ReviewPage.xaml`：四档按钮换用新样式族，页内本地样式表减去 40 余行。

**踩到的坑**
- `Border` 没有 `BorderDashArray` 这类属性可借用（揭开前的虚线槽此前用 `Rectangle` 画的），本轮保持 `Rectangle` 方案。
- `TranslateTransform` 带 `x:Name` 后不能当 `Setter TargetName`：`Freezable` 不在模板 name scope 里，会报 `MC4111 无法找到 Trigger 目标`。改成对容器设 `RenderTransform` 并内联 `<TranslateTransform Y="1"/>`。
- `Padding` 必须设在 `ContentPresenter` 上：留在外层 `Border` 时主按钮的蓝底会缩在描边里，截图里成了「小胶囊 + 外边框」。

**验证**
- `dotnet build FastTranslation.slnx -c Release`：0 警告 0 错误；`dotnet test`：576 全绿（Core 204 / Platform.Windows 87 / Providers.QwenMt 92 / Server 193）。
- 截图（1400×1000 逻辑、175% DPI 下 1820×1155）：`v59-home.png`、`v59-home-dark.png`（首页浅/深）、`v59-review-pick.png`（勾选列表）、`v59-review-card.png`（未揭开，四档置灰）、`v59-review-revealed.png` / `-dark.png`（揭开后四档上色）、`v59-hover-grade.png`（悬停抬起）。
- 四档颜色取色：忘记 `#FAEAE7` 底 / `#A9453B` 字、模糊 `#FBF0E0` / `#A9611A`、熟悉 `#E9F0F8` / `#35648F`、记住 `#E7F5F0` / `#0E7C66`。

**风险**
- 渐变是静态的，不随窗口尺寸重排；极端窄窗口下侧栏渐变压缩后色差变小，但仍在同一色相内。
- 换页动效已接 `SystemParameters.ClientAreaAnimation`：系统里关掉动画时直接到位、不做淡入位移；但悬停抬起与按钮下沉走的是样式触发器，没有单独的「不动效」分支。
- 悬停抬起依赖 `DropShadowEffect`，软件渲染兜底下会有轻微模糊，深色主题里不明显。

## v61 交互返工:四档三级色阶，卡片去投影 ✅

**背景**
- v60 之后用户明确指出两点：四档不能只是悬停浮起，选中时应该明显加深；其他页面的常驻阴影在渐变底上形成灰雾，显得脏。此轮保留配色与页面渐变，撤掉依赖阴影的层级表达。
- `ui-ux-pro-max` 没有 WPF 专用条目，本轮只采用通用的「状态变化要可感知」与「层级优先用色阶/描边，不用大面积阴影」原则，没有再引入一套与 `ThemePalette` 冲突的色板。

**改动**
- `src/FastTranslation.App/ThemeBridge.cs`：每档除默认色外新增 `Grade*HoverBrush`，由底色向文字色混合 20%；四档共用 `SetGrade`，避免浅深主题各自维护一组数值。`LiftShadow`、`CardShadow`、`ShadowColor` 全部移除，客户端不再创建卡片 `DropShadowEffect`。
- `src/FastTranslation.App/App.xaml`：四档样式改为默认浅底、悬停中档色、按下/选中实色白字的三级状态。普通按钮和 `Card` 取消所有投影，普通按钮保留换色与 1px 按下位移，侧栏保留 2px 悬停位移。
- `src/FastTranslation.App/Pages/ReviewPage.xaml(.cs)`：四档按钮增加 `SelectedGrade` 附加属性；评分请求发出后立即标记所按档位，选中期间仍保持命中测试，靠 `_grading` 防重复提交，避免 `IsEnabled=false` 的灰色样式盖住选中色。进入新卡、请求失败或切页时清除选中态。

**踩到的坑**
- 只给悬停加 `DropShadowEffect` 不能表达「已选」：WPF 的悬停状态随指针移走立即消失，评分请求期间的选中状态必须由页面显式维护。
- 不能靠 `IsEnabled=false` 同时做防连点和锁样式：禁用触发器会覆盖选中色。现在禁用触发器只用于「尚未揭示译文」，评分中改用 `_grading` 防重入。
- 开发会话文件不是任意 JSON：`AccountSessionStore.StoredSession` 只接受 PascalCase 的 `AccessToken/RefreshToken/ExpiresAtUnix/UserId/Email/Plan`；写错架构会静默回到未登录，不是令牌失效。

**验证**
- `dotnet build FastTranslation.slnx -c Release`：0 警告 0 错误；`dotnet test`：576 全绿（Core 204 / Platform.Windows 87 / Providers.QwenMt 92 / Server 193）。
- 截图（175% DPI，1820×1155）：`assets/qa/v61-account.png` / `v61-account-dark.png`（账号四张卡片仅细描边，无灰雾）；`v61-review-pick.png` / `v61-review-dark.png`（复习列表无投影）；`v61-review-revealed.png`（四档默认浅底）；`v61-grade-hover.png`（「记住」悬停加深）；`v61-grade-selected.png`（「记住」选中为实色靖绿白字）；`v61-next-card.png`（评分成功后选中色清空，进入第 2/4 条且四档重新置灰）。
- 取样位置：选中态看四档条最右「记住」按钮，默认态看同一张图其余三档；卡片只看边框外侧与渐变交界处，不再看按钮是否浮起。

**风险**
- 选中实色只在评分请求期间显示，本地服务端响应极快时可能只是一瞬；这是请求状态反馈，不是持久标记。
- 深色主题的四档实色对比度依赖 `ThemePalette` 现有颜色，本轮未额外引入高饱和色；低亮度显示器上建议以文字为辅，不只靠颜色判断。

## v62 修复:截图翻译恢复可用，OCR 认对响应形态 ✅

**背景**
- v61 之后用户报「截图翻译失效」：热键和托盘菜单都点了，但不弹译图窗。排查发现不是「点了没反应」，而是图片翻译已经成功、窗口却在最后一步抛异常被吞掉；同时 OCR 一直拿不到原文，译图窗底部的「识别到的原文」是空的，导致「保存到单词本」拿不到可编辑的文本。

**两个真 bug**

1. **跨线程访问 WPF 对象**（这是「失效」的直接原因）
   - `ImageTranslationCoordinator.RunAsync` 在 `await Task.WhenAll(imageTask, ocrTask).ConfigureAwait(false)` 之后继续在线程池线程上执行，末尾 `_present(...)` 直接调用 `ImageResultWindow.ShowResult`，抛 `InvalidOperationException: 调用线程无法访问此对象，因为另一个线程拥有该对象`。
   - 该异常被 `catch (Exception ex)` 接住，只变成一句托盘气泡，气泡在别的窗口下又容易被忽略，所以用户体感是「完全没反应」。
   - 修法：在 `App` 里给 `present` / `reportError` 两个回调用新加的 `OnUiThread` 包一层（`Dispatcher.CheckAccess()` 为真直接执行，否则 `BeginInvoke`）。协调器本身继续保持不碰 WPF——它的注释和单测都依赖这个边界，所以没有采用「去掉 `ConfigureAwait(false)`」这种把线程模型退回去的做法。

2. **OCR 请求体字段名错，且成功响应形态没认对**
   - 请求体把图片放在 `content[0].image_url`，那是 OpenAI 兼容格式。DashScope 的多模态端点要的是 `content[0].image`，用 `image_url` 会被直接拒：
     `400 InternalError.Algo.InvalidParameter: The item of \`content\` should be a message of a certain modal`。
   - 修好请求之后又暴露第二层：`qwen-vl-ocr` 的成功响应里，文本在 `content[0].ocr_result.processed_text`，不是当初按普通 VL 模型假设的 `content[0].text`。只认 `text` 会让每次 OCR 都判成「图里没有识别到文字」。
   - 修法：`QwenOcrClient.BuildPayload` 改用 `image`；`TryReadText` 先读 `ocr_result.processed_text`，空白则回落 `text`，纯字符串 `content` 仍兼容。

**改动文件**
- `src/FastTranslation.App/App.xaml.cs`：新增 `OnUiThread(Action)`；截图翻译协调器的 `present` / `reportError` 回调改为经它调度；三条网关链路（`GatewayTranslateAsync` / `GatewayTranslateImageAsync` / `GatewayRecognizeImageAsync`）统一走新增的 `GuardGatewayAsync`，未预料的异常记诊断后返回 null 回落到用户自己的 Key，取消照原样抛。
- `src/FastTranslation.App/ImageTranslationCoordinator.cs`：流程各节点补 `WindowsSelectionCapture.Diag` 诊断行（busy 冲突、选图前后、翻译/OCR 结果、异常类型）。
- `src/FastTranslation.Providers.QwenMt/QwenOcrClient.cs`：请求字段改 `image`；解析兼容 `ocr_result.processed_text`。
- `src/FastTranslation.Providers.QwenMt/QwenMtImageTranslator.cs`、`QwenOcrClient.cs` 构造：复用外部传入的 `HttpClient` 时不再设 `Timeout`（设了会抛 `InvalidOperationException`，让整条链路静默失败），超时只给自建 client。
- `tests/FastTranslation.Providers.QwenMt.Tests/QwenOcrClientTests.cs`：新增/加强三个断言——`image` 必须是字段名且不得出现 `image_url`、`ocr_result.processed_text` 必须能读出、空白 `ocr_result` 要跳过并回落 `text`。

**验证**
- `dotnet build FastTranslation.slnx -c Release`：0 警告 0 错误。
- `dotnet test`：**578 全绿**（Core 204 / Platform.Windows 87 / Providers.QwenMt 94 / Server 193）。
- 真实链路实测（`FT_DEBUG=1` 启动 + UIA 点火 + SendInput 拖框 700,450→1600,950）：
  - 修复前日志：`image: translate ok=True err=/; ocr ok=False err=InvalidResponse/...(400)...` 紧跟 `image: failed InvalidOperationException: 调用线程无法访问此对象`，窗口不出现。
  - 修复后日志：`image: translate ok=True err=/; ocr ok=True err=/`，译图窗正常弹出且底部带识别原文。
  - 截图：`assets/qa/v62-image-result.png`（1628×1125，译图 + 「复制图片 / 另存为… / 保存到单词本 / 关闭」+ 底部「识别到的原文（可修改后保存）」里是 `自动(中英互译) / qwen-mt-flash / 输入或粘贴文本,点击翻译;或在任意应用里划词使用小工具条。`）。
- 取样位置：有没有恢复看窗口是否出现（`hwnd` 变化 + 截图里的译图区）；OCR 是否修好只看底部「识别到的原文」输入框是否有字。

**风险**
- 「保存到单词本」按钮本身未在本机跑通：它走服务端 `SaveNoteAsync`，而本机 `AccountBaseUrl=http://127.0.0.1:5199` 的网关当前没有启动，点击只会得到回落提示。该链路属既有功能，本轮没有改它的逻辑，只保证它能拿到要保存的文本。
- 诊断日志只有 `FT_DEBUG=1` 时写 `%TEMP%\ft-debug.log`，正式包里不会产生任何日志文件。
- `OnUiThread` 用 `BeginInvoke` 不等待，所以「窗口已显示」与协调器 `finally` 置 `_busy=0` 之间存在极短的时间窗；这两件事互不依赖，没有做额外同步。

**发布记录 v1.3.7(2026-10-30)**
- 提交 `459ae7867a3be351d2f82c8ade8caaef545f66d9`；版本号三处同步改为 1.3.7（`Directory.Build.props`、`installer/FastTranslation.iss`、`docs/portable-readme.txt`）。
- 主仓库 Release `v1.3.7`（id 428680610）：`FastTranslation-1.3.7-Setup.exe` 46980711B / sha256 `8c2cde0892a3291f282302918ee58cbbf43cd045bce53139f9ddf947335eb582`；`FastTranslation-1.3.7-portable.zip` 62048723B / sha256 `68b171685a92b5bb807f4d309c7c309d49ca64f434399932064a3578f7a55955`。两个资产均 `state: uploaded`，digest 与本地一致。
- 更新通道仓库 Release `v1.3.7`（id 428686459）上传同一份 Setup.exe，`state: uploaded`，digest 一致。
- `version.json` 已更新（blob `17f064c25dd39df6c3c58ac2abb5c52549a65ea4` 上 PUT）：version 1.3.7 / url 指向通道资产 / sha256 小写 / size 46980711 / releasePage 指主仓库 tag。
- 清单四源轮询：Pages 在 13:36:07 已刷新为 1.3.7（GitHub Pages 有约 600 秒缓存，第一次 13:35:18 还是 1.2.2），gh-proxy raw 与 ghfast.top raw 立即就是 1.3.7。
- 外力事实：`github.com` 直连本次仍不可用（`Connection was reset`），`gh-proxy.com` 前缀 200 可取；下载候选顺位里的代理优先策略照常生效。

## v63 分类判据按字数分档、截图翻译等待态、译图窗固定尺寸可拉伸

用户三条诉求：分类「统一当成单词」；截图翻译等待期间没有任何提示；译图窗跟着图片大小变，不能拉伸。

### 1. 中文句子被判成单词（`NoteClassifier`）

- 根因：`src/FastTranslation.Core/Notes/NoteClassifier.cs:29` 原来的单词判据是 `IsSingleCjkCharacter(text) || (!text.Any(char.IsWhiteSpace) && text.Length <= MaxWordLength)`。中文句子没有空格、又经常不带句末标点，「她安静地离开了房间」9 个字符直接命中 `Word`。英文侧因为按空白切词，一直是对的——所以问题只出现在中文，且不是三条保存路径传错值（`App.xaml.cs` 两处与 `Pages/MainPage.xaml.cs:157` 都正常）。
- 改法：新增 `MaxCjkWordChars = 3` / `MaxCjkPhraseChars = 8`，先 `CountCjk`（逐 char 判 `0x4E00–0x9FFF`）；有汉字就走 `ClassifyCjk`：含分句标点 `，,、；;：:` → 语句；汉字 ≤3 → 单词，但带空白的中英混排（「API 失败」）判短语；4–8 → 短语；>8 → 语句。纯 ASCII 分支保持原样。
- 为什么是 8：初版定 12，「她安静地离开了房间」（9 字）落进短语，与「一句话」的直觉不符，收紧到 8。分句标点单独先判，是因为中文长句常靠逗号断开，字数不够但结构上已经是句子。
- 服务端 `server/FastTranslation.Server/Domain/NoteClassifier.cs` 是逐字镜像（`NotesEndpoints.cs:57` 优先采信客户端传入的 kind），两边必须同改，否则多端保存会出现不同归类。
- 测试：`tests/FastTranslation.Core.Tests/NoteClassifierTests.cs` 新增 `Chinese_text_is_judged_by_character_count`、`A_chinese_sentence_without_punctuation_is_not_a_word`、`A_mixed_sentence_still_uses_the_chinese_buckets`，分类器用例 24 条全绿。

### 2. 截图翻译等待态（转圈 + 「正在快马加鞭翻译~」）

- 框选结束后要等三段网络往返（译图 + OCR 并行，再做一次文本翻译），此前这段时间界面上什么都没有。`ImageTranslationCoordinator` 构造新增 `Action waiting`，在 `PickAsync` 返回、确认没被取消之后立刻回调；`App.xaml.cs` 把它接到 `_imageWindow.ShowLoading(...)`。
- 结果窗新增 `ShowLoading(string caption)`：中间是 18×18 的品牌蓝圆环 + 「正在快马加鞭翻译~」，按钮与文本区全部隐藏。图片行与等待面板叠在同一个 `Grid` 里互换，出图时窗口不再先缩后张。
- 踩坑：圆环原来抄的是取词浮层的 `StrokeDashArray = {34, 10}`，但 WPF 的虚线单位是线宽的倍数（`StrokeThickness = 2`），即 68 虚线 + 20 空隙，而 18px 圆的周长只有 56——整圈被画满，旋转起来像素几乎不变（连拍四帧只差 4 个像素）。改成 `{6, 2.5}` 后四帧差约 9984 个像素，动画才真的看得见。
- `Hide()` 里先 `StopSpinner()`：`RepeatBehavior.Forever` 的动画在窗口隐藏后仍占渲染线程。

### 3. 译图窗固定尺寸 + 可拉伸（`ImageResultWindow`）

- 原来是 `SizeToContent.WidthAndHeight` + 按工作区 70% 算缩放，图片一大窗口就跟着大。改为 `SizeToContent.Manual`，默认 **780×620 DIP**（工作区 9 折以内收窄），图片在 `Stretch = Uniform` 的格子里自适应，窗口尺寸不再随图片变化。
- 拉伸：`AddResizeGrips` 在右边、下边、右下角各放一个透明 `Thumb`（`SizeWE` / `SizeNS` / `SizeNWSE`），右下角另画两道斜杠当把手。`DragDelta` 走 `ResizeBy`，下限取 `MinWidth = 520` / `MinHeight = 360`。
- 抓手势：根 Border 的 `MouseLeftButtonDown` 原本直接 `DragMove()`，加了 `Thumb` 之后拖手柄会变成移动窗口。现在先 `FindAncestor<Thumb>` 判断，命中手柄就 return。
- `using ShapesPath = System.Windows.Shapes.Path` 别名：这个文件里 `Path` 已经被 `System.IO.Path` 占用（`Path.GetFileName` 等），不别名会编译不过。

### 验证

- `dotnet build FastTranslation.slnx -c Release` 0 警告 0 错误；`dotnet test` Core 214 + Platform.Windows 87 + Providers.QwenMt 94 + Server 193 全绿。
- 界面证据（175% 缩放，截图含 24px 桌面边）：`assets/qa/ui-v63/image-loading.png` 与 `image-result.png` 都是 1389×1109，折算 (1389-24)/1.75 = **780.0**、(1109-24)/1.75 = **620.0** DIP，与图片尺寸无关；等待态品牌蓝像素 230 个（bbox 679,545–709,575），UIA 读到「正在快马加鞭翻译~」；结果态右下角 `TextMuted` 像素 98 个，相对窗口右下角偏移 (1,1)。
- 转圈动效：`assets/qa/ui-v63/spinlong/spin-0..3.png` 连拍四帧，差分 9984 / 9989 / 9990 像素。
- 复现脚本：`tools/qa/shot-image-window.ps1`（UIA 找窗 + `CopyFromScreen`，跑 `FT_UITEST=image-loading` / `image-result` 两态）；`publish/` 下的 `spin-two.ps1` 与 `px-v63*.py` 是本次的临时探针，不进仓库。
- 残留风险：中文分档（3 / 8 字）是经验阈值，成语与短句边界（如 8 字的完整句子）仍会判成短语；用户要求「不要自作主张发版」，版本号本次未动。

## v64 截图翻译保存补上译文与窗内回执

截图翻译的「保存到单词本」一直是**只存原文**的：协调器在 C 段辛苦算出来的那句译文算完就丢了，`SaveScreenshotTextAsync` 把 targetText / sourceLang / targetLang / model 四个参数全填了空串，服务端也不拦（只要求 source 非空）。后果是复习揭示时答案栏空白、单词本详情里的译文行空白——而 `ImageResultWindow` 只显示识别原文，用户根本看不到那句译文丢了。

**改了什么**

- `src/FastTranslation.App/ImageTranslationCoordinator.cs`：`ImageCaptureResult` 追加 `SourceLanguage / TargetLanguage / Model` 三个带默认值的字段；C 段翻译成功时把 `translatedResult` 的语种与模型一起填进去（`QwenMtProvider` 回填的是 API 名，如 `English`/`Chinese`）。模型留空不补：设置页里的 Model 是文本模型，图片翻译用的是适配器默认模型，"模型"一栏宁可空着也不要写错。
- `src/FastTranslation.Platform.Windows/ImageResultWindow.cs`：新增 `ImageNoteContext`（译文 + 语种 + 模型）与 `ImageNoteDraft`（保存载荷）。`ShowResult` 多一个可选 `note` 参数，`SaveRequested` 由 `Action<string>` 改成 `Action<ImageNoteDraft>`。
- `ImageNoteDraft.Create` 是这次的关键判断：OCR 文本允许用户改（"识别到的原文（可修改后保存）"），一旦改过，之前那句译文就与原文对不上——此时宁可存空译文 + 空模型，也不要存一句对不上的。原文未改（只差首尾空白）才带上译文。
- 窗内回执：`MarkSaved(kind)` 把按钮改成「已保存」并禁用，标题行右侧显示「已保存到单词本」；失败走 `ShowSaveError(message)`（Danger 色，按钮保持可用以便重试）。每次 `ShowResult` 调 `ResetSaveState()`，否则第二张截图的窗口还挂着上一张的「已保存」。托盘气泡保留，但不再是唯一反馈。
- `App.xaml.cs`：`SaveScreenshotText(ImageNoteDraft)` / `SaveScreenshotTextAsync(ImageNoteDraft)` 把四个字段透传给 `SaveNoteAsync`；三条失败分支（未登录 / SubscriptionRequired / 其它）都在窗内留一句话。顺手改掉 `:859` 那句与实现不符的注释「译文在复习时按需再要」——按需翻译根本不存在。

**验证**

- `dotnet build FastTranslation.slnx -c Release` 0 警告 0 错误；`dotnet test` = Core 214 + Platform.Windows 92 + Providers.QwenMt 94 + Server 193 全绿（Platform 从 87 涨到 92，新增 `ImageNoteDraftTests` 5 例）。
- 截图（`tools/qa/shot-image-window.ps1`，新增第三档 `image-saved` 钩子，产物在本机 `assets/qa/ui-v64/`）：三态都是 1389×1109 @175% 缩放 ⇒ 固定尺寸没被这次改动破坏；UIA 读到「已保存到单词本」与按钮「已保存」。
- 布局核对（UIA 相对窗口坐标，窗口 1365×1085）：标题行右侧状态 `relX=1192 relY=793 w=147`，左侧标签 `relX=26 relY=793 w=1166` —— 两者同一行、左标签右边界 1192 正好接上状态起点，没有重叠；按钮「已保存」`relX=1132`，与「另存为…」`relX=994` 不挤。

**残留风险**

- 用户在识别框里改文本后点保存，单词本会得到一条**只有原文**的笔记（reviewing 时仍是答案空白）。这是刻意的取舍：错位的译文比没有译文更坏。要补的话应该在窗口里加一个「重新翻译」按钮，而不是悄悄存旧译文。
- 服务端仍然接受空 targetText，历史数据里的空译文本节不回溯。

## v65 截图翻译提速：谁先回谁先上屏，网关 5xx 也进冷却

用户 m03296：阿里那边响应是毫秒级，为什么客户端有明显等待，尤其截图翻译要好幾秒——"我们的卖点就是实时速度"。

### 实测：慢在哪一跳

探针 `publish/TimingProbe`（net10.0-windows 控制台，ProjectReference Platform.Windows + Providers.QwenMt，在 gitignore 内）用 `DelegatingHandler` 打印每一跳 `hop METHOD host path -> status in Nms`，直接调真实 DashScope：

- 文本 `qwen-mt-flash`：冷启首跳 460ms，预热后稳定 **约 240ms**。文本翻译本身并不慢。
- 图片 `qwen-mt-image-2.0`：POST 先回 200，再 GET 返回的 OSS 图，**2.6–3.3s**（1389×1109 / 131935B）；大图 2778×2218 / 403203B 到 **4576ms**。
- OCR `qwen-vl-ocr`：617–1873ms，典型 **约 0.9s**。

⇒ 慢的不是文本模型，而是**图片翻译这一跳本身就按秒计**。既然单跳省不掉，就只能把「串行等待」改成「谁先回谁先上屏」。

顺带否掉了两个看似能省的优化：

- 降采样：缩到 2400 / 2000 / 1920 / 1600 / 1280 分别 4278 / 3970 / 2943 / 2654 / 3058ms，收益不稳且非单调（1280 反而比 1600 慢），不值得为一个不稳定的 0.2–1.6s 引入「大图变糊」。
- PNG→JPEG 转码：无稳定收益，不做。

### 1. 流水线重排（`ImageTranslationCoordinator`）

原来 `Task.WhenAll(imageTask, ocrTask)` 之后才做 C 段文本翻译，等于「先等最慢的图片，再等文本」，两段时间完全串行。

现在：

- `TranslateRecognizedAsync(ocrTask, settings)`：OCR 一回来立刻发文本翻译，**不等图片**。
- `PresentEarlierAsync(imageTask, textTask, originApp)`：`await Task.WhenAny(...)`，谁先回谁先 `_present(...)` 上屏；图先到就先只上译图，文字先到就先只上原文 + 译文。
- 后面补齐缺的那一半：`needsPicture = image.Succeeded && !shown.Image` / `needsText = recognized.Length > 0 && !shown.Text`，两样都不缺就直接返回，绝不重绘。
- 失败语义收紧成 `if (!image.Succeeded && recognized.Length == 0)` 才报错——只要有一半能出结果就不打扰用户。

**实测对照**（同一张图，两轮）：

| 顺序 | 首屏 | 译图齐全 |
| --- | --- | --- |
| 旧：WhenAll(A,B) 再 C | 3098 / 2959ms | 同左 |
| 新：谁先回谁先上屏 | **1484 / 1667ms** | 2466 / 2447ms |

首屏直接砍半，纯文字路径 1443–1503ms。

### 2. 网关 5xx 也进冷却（`App.xaml.cs`）

`NoteGatewayFailure` 原来只对 `Network` 开 60 秒冷却。但网关 `Gateway.Enabled=false` 时，三条链路（`/v1/chat/completions`、`/v1/image`、OCR）都会立刻返回 `503 gateway_disabled`（`server/FastTranslation.Server/Gateway/GatewayEndpoints.cs:44/:116/:171`），503 被 `QwenMtProvider` 归成 `ServerError`，永远不会冷却——于是每次翻译都先白跑一趟注定失败的网关往返，再回落用户自己的 Key。

改成 `Network` 或 `ServerError` 都进冷却（`GatewayDownWindowMs = 60_000`）。这条直接影响首字延迟，所以和提速放在一起。

### 3. `ImageResultWindow` 的「已保存」不被二次上屏冲掉

流水线变成两次 `ShowResult`（文字先到、译图后到）之后，原来的 `ResetSaveState()` 会把用户刚点出的「已保存」刷回「保存到单词本」。新增 `private bool _saved`：`ShowLoading` 归零、`MarkSaved` 置真、`ShowResult` 里 `if (!_saved) ResetSaveState();`。

### 验证

- `dotnet build FastTranslation.slnx -c Release` 0 警告 0 错误；`dotnet test` = **593 全绿**（Core 214 / Platform.Windows 92 / Providers.QwenMt 94 / Server 193）。
- 端到端毫秒数即上表的 1484 / 1667 与 2466 / 2447；单跳数据同「实测」小节。
- 界面侧未再截图：本节的改动在时序与失败冷却，不改版面；等待态与固定尺寸仍是 v63 那两张图。

### 残留风险

- 图片翻译这一跳本身仍是 2.5–4.6s，客户端无法压到毫秒级；用户看到的是「先出译文、再补上译图」，不再是一片空白。
- 两次 `ShowResult` 意味着窗口会重绘一次；已用 `_saved` 保住保存态，但用户正好在这两次之间拖动窗口，位置不会被记忆（窗口本来也不记位置）。
- 网关冷却的副作用：服务端真的 5xx 之后，即使立刻恢复，客户端也会最多 60 秒继续用用户自己的 Key。这是刻意选择——宁可多花用户自己的一点额度，也不要每次翻译都白等一趟网关。

## v66 · 截图翻译：文字先到就先看，译图原地补

v65 把截图翻译改成「谁先回谁先上屏」之后，用户体感变成了「等齐了再一起出来」。查下来不是时序没生效，而是**分段的时机不对**：v65 里只要文字先到，窗口就把图区收成 0 行、把文本区铺满；译图回来时再撑开图区——中间那 0.35–1.1s 里用户看到的是一张「纯文字卡」，译图到达时版面又跳一次，反而更像卡顿。

真机采样（UIA 每 80ms 采样一次窗口）也证实两种顺序都会出现：
- 23:02:56 那次：等待态从 ms=384 一直持续到 ms=9370，译图先到，文字 0.75s 后补。
- 22:59:47 那次：`image: done at 1782ms`，三段几乎同时结束。

### 改法

- `ImageResultWindow.ShowResult` 新增 `bool picturePending`（第 6 个可选参数，旧调用点不用改）。文字先到时走这一支：**图区留在原位、继续转圈**（文案「正在生成译图~」），文本区按固定高度 132 DIP 排在下面，译图回来就地填进图区 —— 版面只画一次，不再收放。
- `StartSpinner` 从无参改成 `StartSpinner(string text)`，两处文案提成常量 `LoadingText = "正在快马加鞭翻译~"` / `PictureLoadingText = "正在生成译图~"`。`ShowLoading` 仍走前者。
- `ImageCaptureResult` 末尾加 `bool PicturePending = false`，`PresentEarlierAsync` 里文字赢下 `WhenAny` 时置真。
- 补齐判据从两项变三项，补上 `needsSettle = shown.Text && !image.Succeeded`：文字已上屏但译图失败时也要再画一次，否则图区那个转圈会一直转下去（v65 漏掉的死循环）。
- 同一张截图会被 `ShowResult` 调两次，识别文本改为「内容变了才覆盖」：`if (!string.Equals(_recognizedOriginal, recognized.Trim(), StringComparison.Ordinal))`。否则用户在中间那 1 秒里改过的原文会被第二次上屏冲掉。

### 验证

- `dotnet build` 0 警告 0 错误；`dotnet test` = **593 全绿**（Core 214 / Platform.Windows 92 / Providers.QwenMt 94 / Server 193）。
- 三态截图（`tools/qa/shot-image-window.ps1` + 新增 `FT_UITEST=image-partial` 钩子，产物在 `assets/qa/ui-v66/`，均 1389×1109 px，折算 780×620 DIP）：
  - `image-loading.png`：只有「关闭」，文案「正在快马加鞭翻译~」，品牌蓝 186 px、bbox (679,533)-(708,563)。
  - `image-partial.png`：**新增中间态**——按钮只有「关闭」，文案「截图翻译 · 正在生成译图」，图区转圈 188 px、bbox (679,405)-(709,435)，下方已有识别文本区（UIA 读到 edit value 两行）+「保存到单词本」。
  - `image-result.png`：四个按钮齐、有图、文本区固定高度，无转圈（品牌蓝只剩图片里的蓝色内容）。
  - 转圈位置随图区上移（533 → 405），正好证明图区没被收掉、只是把转圈挪到了图区中央。

### 残留风险

- 两次 `ShowResult` 仍然存在（这是「先看到字」的代价）；已用 `_saved` 保住保存态、用 `_recognizedOriginal` 保住用户改动，但用户如果正好在这 1 秒里拖窗口，位置不记忆。
- 译图失败时窗口会多画一次（`needsSettle`）；这一次是空转，只为把转圈收掉。
## v67 · 网关可用性改成后台探测：空闲后的第一次翻译不再白等两秒

用户报的是「基本都在 1s 内出答案，但隔一段时间不用再翻，会明显等 3s 左右；可是结果卡只显示 100 多毫秒」。这个矛盾本身就是线索：`ElapsedMs` 只覆盖 provider 自己的 HTTP 往返，而挡住用户的那一段发生在它之前。

### 定位

探针（`publish/loop-probe.ps1`，裸 `TcpClient`，与 .NET、代理、UseProxy 全无关）量了本机几个环回端口：

```
5199 -> 2792ms ok=False     # 网关默认端口，无人监听
5198 -> 2031ms ok=False
1    -> 2042ms ok=False
53017 -> 1ms    ok=True      # 有人监听（本机 DSH Web GUI）
```

⇒ 这台机器上**任何没人监听的环回端口都要约 2 秒才拿到 RST**（过滤驱动/代理层行为，`docs/IMPL.md` v58 已记过同一现象）。用真 provider 打网关同样是 2174 / 2024ms；同期 DashScope 冷连接 404–661ms、热连接 110ms —— 阿里云那边确实没问题，用户的判断是对的。

而原来的熄灭逻辑是个 60 秒时间窗：`ProbeGatewayAsync` 启动探一次，失败写 `_gatewayDownUntil = now + 60s`；窗口一过，`GatewayTranslateAsync` 的 `if (Environment.TickCount64 < _gatewayDownUntil) return null;` 不再拦住，**下一次翻译就当场去连那个要两秒才 RST 的端口**，失败后再续 60 秒。用户说的「隔一段时间不用再翻就慢」正好落在窗口过期那一刻。

### 改法

把「冷却到某个时刻」换成「只信最后一次探测结论」：

- 新增 `src/FastTranslation.Core/GatewayHealth.cs`：`IsUsable`（默认 false）、`MarkDown()`、`MarkProbed(bool)`、`TryBeginProbe()/EndProbe()`（同一时刻只允许一次探测）。默认不可用的理由和原来一样：探测本身就要两秒，不能让启动后头几秒的第一次翻译把它吃进去。
- `App.xaml.cs` 的 `ProbeGatewayAsync` 换成 `RunGatewayProbeLoopAsync` 常驻循环：只在 `!IsUsable` 时探，探通即恢复走网关，间隔 `GatewayHealth.DownProbeIntervalMs = 60_000`。翻译路径**只读结论、从不自己发起探测**，那两秒永远只在后台线程上。
- 三条网关链路（translate / image / ocr）的守卫统一改成 `if (!_gatewayHealth.IsUsable) return null;`；`NoteGatewayFailure` 改成 `_gatewayHealth.MarkDown()`。
- 退出时只 `_gatewayProbeCts.Cancel()`、**不 Dispose**：探测循环此刻可能正挂在 `Task.Delay` 上，Dispose 会让它的注册抛 `ObjectDisposedException`。
- `FT_UITEST=translate` 钩子新增 `FT_UITEST_DELAY`（毫秒），用来把测量推迟到任意时刻，专门复现「空闲一段时间后再翻」。默认仍是 1800ms，旧用法不变。

取舍：网关是免费额度链路，不能因为一次失败就永久放弃，所以是「一直后台重探」而不是「一次失败永久降级」。代价是网关恢复后最多晚 60 秒才被重新用上，而这段时间用户走自己的 Key 照样能翻。

### 验证

同一台机器、同一个 `FT_UITEST_DELAY=70000`（启动 70 秒后才翻译，正好跨过原来那 60 秒窗），改前改后各跑一次：

改前（HEAD `5133602` 的独立 worktree 构建）：
```
00:15:46.909 gateway down for 60s: 网络错误:由于目标计算机积极拒绝 (127.0.0.1:5199)
00:15:47.237 translate hook [0]: loading=40ms call=4396ms provider=328ms ok=True
```
改后：
```
00:13:30.353 gateway probe: 127.0.0.1:5199 unreachable (SocketException)
00:13:36.611 translate hook [0]: loading=40ms call=322ms provider=313ms ok=True
```
⇒ 用户等待从 4396ms 降到 322ms，`call` 与 `provider` 基本相等（313 vs 322），也就是「面板显示多少，用户就等多少」终于成立。

- `dotnet build FastTranslation.slnx -c Release` 0 警告 0 错误。
- `dotnet test` = **597 全绿**（Core 218 / Platform.Windows 92 / Providers.QwenMt 94 / Server 193），其中 `GatewayHealthTests` 4 例覆盖：默认不可用、探通后可再用、探通后再失败、同一时刻只允许一次探测。
- 长时间运行时后台探测按 60 秒节奏打日志（`00:09:30`、`00:10:32` …），证实循环没有把 CPU 或等待压到前台线程。

### 残留风险

- 网关真的在线上（生产 `AccountBaseUrl`）时，后台重探的恢复延迟是 60 秒；这段时间的免费额度用不上，但翻译不受影响。
- 探测只做 TCP 连接，服务端进程活着但 `Gateway.Enabled=false` 时探通也会被判可用 —— 这种情况由 `NoteGatewayFailure` 按 5xx 兜底（v65 加的那条），只是会多花一次完整往返。
- 这台机器上「无人监听的环回端口要 2 秒」是系统层行为，没在这次里处理；它现在只影响后台线程。

## v68 · 主题令牌补两处缺漏：禁用态主按钮、提示文字与品牌底字色

用户拿着全应用双主题截图问「色调样式和主流统一一下」，逐页取证后量出三处不合格。都不是版面问题，全在令牌与样式层。

### 定位

**一、禁用态主按钮是「蓝底灰字」**。复习页「开始复习」在 `IsEnabled=False` 时仍保留品牌蓝底，字却是灰的，深色主题下对比只有 1.03:1。

根因是资源键根本没定义：`App.xaml:126-127` 的禁用触发器引用 `HairStrongBrush`，但 `App.xaml` 只声明了 15 支画刷、`ThemeBridge.Apply` 也只 `Set` 了 15 个键，**`HairStrongBrush` 从头到尾没人定义**。WPF 的 `DynamicResource` 找不到键不报错，`Border.Background` 静默取默认值（null = 透明），于是禁用态只剩「Foreground 变灰」这一半生效。`ThemePalette` 里明明有 HairStrong（浅 `#D9E3F1` / 深 `#343B46`），`ThemePaletteTests` 也断言它非空——所以测试一直全绿，只是没人检查「令牌有值」与「接进 WPF 资源字典」之间那道缝。
**二、提示文字太淡**。`TextMuted` 全仓 38 处引用（提示语、占位、计数、卡片副标题）：
- 浅色 `#8B97A9` 对 WindowBg `#F7F9FC` = **2.81:1**、对 Surface `#FFFFFF` = 2.96:1
- 深色 `#7C8798` 对 Surface `#1F242C` = **4.29:1**

**三、深色主题里压在品牌蓝实底上的白字** = `#FFFFFF` on `#4A82FF` = **3.54:1**。深色的品牌蓝比浅色亮一档，白字压不住；同样的组合还在引导窗三个步骤圆标里（`OnboardingWindow.xaml:22,31,49`）。

### 改法

- 新增令牌 `OnBrandText`（`ThemeColors` 第 6 个字段）：浅色 `#FFFFFF`（on `#2562FF` = 4.92）、深色 `#101820`（on `#4A82FF` = **5.05**）。深色用的是已有的「深墨」——和 `GradeSelectedTextBrush` 深色分支同一个值，不引入新色相。
- `ThemeBridge.Apply` 补 `Set(r, "HairStrongBrush", c.HairStrong)` 与 `Set(r, "OnBrandTextBrush", c.OnBrandText)`；`App.xaml` 顶部同步声明这两支 `po:Freeze="False"` 画刷。
- `PrimaryButton` 的 `Foreground="White"` → `{DynamicResource OnBrandTextBrush}`；禁用触发器的字色从 `TextMutedBrush` 改成 `TextSecondaryBrush`——底色既然是 HairStrong，`TextMuted` 只有浅 2.28 / 深 3.11，`TextSecondary` 是 **4.18 / 5.03**（WCAG 1.4.3 对 inactive 控件本就豁免，但没人愿意读一块糊字）。
- `TextMuted`：浅色 `#8B97A9` → `#667287`（4.61 / 4.86 / Hover 上 4.40），深色 `#7C8798` → `#8C97A8`（5.96 / 5.28 / 4.76）。
- 浮层（C# 画按钮，用不了 XAML 资源）同步：`FtTheme.OnBrandText` 新增，`Primary()` 的 `fg` 从 `Brushes.White` 换掉；禁用态底色/字色从写死的 `#A9C0F2`/`#E9EEFB` 换成 `HairStrong`/`TextSecondary`，和 XAML 侧对齐（旧那两个值在深色主题里是一块突兀的浅蓝）。
### 验证

- `dotnet build FastTranslation.slnx -c Release` 0 警告 0 错误。
- `dotnet test` = **600 全绿**（Core 218 / Platform.Windows 95 / Providers.QwenMt 94 / Server 193）。
- 新增两条测试：
  - `ThemePaletteTests.OnBrandTextIsReadableOnTheBrandFill`：两主题分别校验 `OnBrandText` on `Brand` ≥ 4.5，测试里自带 WCAG 相对亮度实现。
  - `ThemeResourceKeysTests.EveryDynamicResourceKeyInAppXamlIsDeclaredOrInjected`：扫 `src/FastTranslation.App/**/*.xaml` 里每一个 `{DynamicResource X}`，与「x:Key 声明的 ∪ ThemeBridge 注入的 ∪ SetGrade 循环生成的四档键」比对，缺一个就红。**反向验证过**：把 `Set(r, "HairStrongBrush", …)` 删掉，测试立刻报 `Collection: ["HairStrongBrush"]`——正是 v68 之前那份代码的状态(那时最新改动是 v67 的网关探测)。
- 截图（`tools/qa/shot-audit.ps1`，PrintWindow，1820×1155 @175%）：改前 `publish/ui-audit/`、改后 `publish/ui-after/`，双主题各 8 页。
  - 复习页「开始复习」（禁用）：深色底 `#343B46`（HairStrong）+ 字 `#A3AEBF`（TextSecondary）= 5.03:1，不再是蓝底；浅色底 `#D9E3F1` + 字 `#5C6B82` = 4.18:1。取样区 (1595,160)-(1755,195)。
  - 设置页底部说明文字：同一区域取样，改前主色 `#8B97A9`、改后 `#667287`。取样区 (350,1085)-(780,1115)。
  - 设置页「保存」按钮（可用态）：深色 `#4A82FF` 底 + `#101820` 字 = 5.05:1。取样区 (1655,1075)-(1770,1120)。

### 残留风险（本次没动，留作下一档）

- 侧栏角标白字压在 `Danger` 上：深色 `#FF6B6B` 底白字 = **2.78:1**（`MainWindow.xaml:49/59/69/79`）。
- 用 `BrandBrush` 作文字色的 7 处（`AccountPage.xaml:150,161`、`MainPage.xaml:44`、`PlanPage.xaml:99`、`ReviewPage.xaml:119`、`SettingsPage.xaml:230`、`UpdateWindow.xaml:33`）：深色下对 Surface 是 4.40:1；侧栏活动项 `#4A82FF` on `BrandFill #22304D` = **3.71:1**。要修得给它们单独一支「品牌作字」色（深色 `#8FB4FF` = 6.35:1）。
- 三处清空 `FocusVisualStyle` 且模板内没自绘焦点环（`App.xaml:299` FtComboBox / `:415` SidebarItem / `:496` GradeButton），键盘 Tab 看不到焦点。
## v69 · 界面审查整改：焦点环、加载态、字号阶与品牌作字

拿双主题全页截图逐页取证，量出 11 项问题，按 P0–P4 分档。P0/P1（禁用态主按钮、TextMuted 太淡、品牌底字色）随 v68 落地，这一版把剩下的 P2–P4 与 v68 结果卡里遗留的两条对比度收掉。

### P2 · 键盘焦点环

三处控件把 `FocusVisualStyle` 清空后模板里没画替代品（`App.xaml` 的 FtComboBox / SidebarItem / GradeButton），Tab 走过去屏幕上没有任何反应。改法照抄 TextBox 已有的范式：模板里加一支 `<Border x:Name="ring" BorderBrush="Transparent" BorderThickness="2" Margin="-2"/>` 垫在实底下面，`IsKeyboardFocused` 触发器把 `ring.BorderBrush` 描成 BrandBrush。三处现在分别是 `App.xaml:340`（FtComboBox，CornerRadius 11 / Margin -1，因为外框是圆角 10）、`:463`（SidebarItem）、`:554`（GradeButton）。

### P3 · 三个「中间态」

- **套餐页用量卡**（`Pages/PlanPage.xaml.cs:79` 新增 `ShowReading()`）：`LoadUsageAsync()` 先折叠 `UsageError`、调 `ShowReading()` 把四段文字写成「读取中…」并把两条进度条归零，再 await；失败分支现在会主动写「读取失败」并清空残留值。此前失败时进度条留着上一次的数，看不出是新数据还是旧数据。
- **单词/短语/语句页空态**（`Pages/NotesPage.xaml:175,187`）：12px + `TextMutedBrush` + MaxWidth 230 → 13px + LineHeight 22 + `TextSecondaryBrush` + MaxWidth 260。原来一行又小又灰的字摆在两块大白卡中间，看起来像加载残留而不是「这栏还是空的」。
- **设置页滚动区底部**（`Pages/SettingsPage.xaml:62`）：`BodyStack` 的 `Margin="24,0,24,6"` → `"24,0,24,18"`。原来滚到底时最后一张卡的脚注被视口边缘切成半行——不是布局溢出，纯粹少了半行内边距。

### P4 · 令牌、字号与样式去重

- **P4a 渐变令牌化**：`Pages/MainPage.xaml` 里硬编码的 `LinearGradientBrush x:Key="BoltRule"`（`#40A9FF` → `#2562FF`）是全仓唯一的裸色值，深色主题下 `#2562FF` 压在卡片上只有 3.17:1。改为令牌：`ThemePalette.ThemeColors` 新增 `BoltFrom` / `BoltTo`（浅 `#40A9FF`→`#2562FF`、深 `#4A82FF`→`#5BB4FF`），`ThemeBridge` 新增带端点的 `SetGradient(resources, key, fromHex, toHex, Point start, Point end)` 重载并注册 `BoltRuleBrush`，MainPage 改引用 `{DynamicResource BoltRuleBrush}`。那道渐变是斜着走的（0,0 → 0.35,1）而非上下铺满，所以端点必须能传。
- **P4b 字号收敛**：阶定为 10/11/12/13/14/15/17/19/21。清掉三档掉队的：`UpdateWindow.xaml:17` 标题 16→17（其余五处标题本来都是 17）、`Pages/MainPage.xaml:19,22,58` 14.5→14、`Pages/AccountPage.xaml:123` 头像 18→17。`MainPage.xaml:58` 保留 `LineHeight="26"`。10px 的六处（`MainWindow.xaml:49/59/69/79` 侧栏角标、`ReviewPage.xaml:112/118` 的 kind / 「新」chip）刻意不动——chip 级微型标签本来就不该跟正文同阶。
- **P4c 样式去重**：`Hint` / `CardTitle` / `FieldLabelTight` / `SmallButton` 四支在五个页面里各抄一份、参数一模一样，全部上移到 `App.xaml:43-67`，页面里的副本删掉。`Pages/NotesPage.xaml` 自己的 `GradeButton`（Height 30 / FontSize 12）**保留**：和 `App.xaml:538` 那支（Height 40 / FontSize 13）参数不同，它是挤在详情卡里而不是独占一行。

### 收掉 v68 残留的两条对比度

- **新增 `BrandText` 令牌**：v68 记过「用 BrandBrush 当文字色」的 7 处深色下只有 4.40:1（对 Surface）、3.71:1（侧栏活动项对 BrandFill）。文字和描边共用一个色是根因，所以拆一支独立的「品牌作字」：浅 `#1D4FE0`（对 `#FFFFFF` 6.47 / 对 `#EDF2FF` 5.77）、深 `#8FB4FF`（对 `#1F242C` 7.53 / 对 `#22304D` 6.35）。`ThemeBridge.cs:47` 注入 `BrandTextBrush`，`App.xaml:26` 声明默认值；共 8 处改用新键——两支样式 `App.xaml:435`（FtComboBox 选中项）与 `:496`（SidebarItemActive），加六个页面：`Pages/AccountPage.xaml:124,135`、`Pages/MainPage.xaml:36`、`Pages/PlanPage.xaml:73`、`Pages/ReviewPage.xaml:113`、`Pages/SettingsPage.xaml:212`。描边、图标、进度条继续用 `BrandBrush`——`UpdateWindow.xaml:34` 那处是 ProgressBar 的 Foreground，不是文字，不动。
- **侧栏角标**：`MainWindow.xaml:49/59/69/79` 四个角标文本 `Foreground="White"` → `{DynamicResource OnBrandTextBrush}`。深色 Danger `#FF6B6B` 上白字 2.78:1，`OnBrandText` 的 `#101820` 是 6.34:1；浅色 `OnBrandText` 就是 `#FFFFFF`，对 `#DC2626` 4.83:1，不变。

### 验证

- `dotnet build FastTranslation.slnx -c Release` 0 警告 0 错误。
- `dotnet test` = **602 全绿**（Core 218 / Platform.Windows 97 / Providers.QwenMt 94 / Server 193）。P4c 那轮是 95，本版末尾两条对比度带了 2 条 theory。
- 新增测试：`ThemePaletteTests.BrandTextIsReadableOnSurfaceAndOnBrandFill`（两主题断言 `BrandText` 对 `Surface` 与 `BrandFill` 各 ≥ 4.5，复用 v68 已有的私有 `Contrast` 实现）；`EveryTokenIsFilledInBothThemes` 的令牌数组加 `BoltFrom` / `BoltTo` / `BrandText`，断言 18 → **21**。
- 截图 `tools/qa/shot-audit.ps1 -ExePath <bin/Release/net10.0-windows/FastTranslation.exe> -Theme light|dark -OutDir publish/ui-v69`，双主题各 8 页 1820×1155（PrintWindow）。
  - 侧栏活动项 (40,165)-(130,195)：深色 `#22304D` ×1992 底 + **`#8FB4FF` ×361** 文字，浅色 `#EDF2FF` ×1992 + **`#1D4FE0` ×361**。文字主色命中数和换色前一样是 361 像素，说明只是换色没动版面。
  - 全图计数：`audit-dark-main.png` 里 `#8FB4FF` 137 处、`#4A82FF` 仍有 2044（描边/图标/进度条）、`#1D4FE0` 0 处；`audit-light-main.png` 正好相反（`#1D4FE0` 137、`#2562FF` 2046）。
  - 角标取样时该页没有未读数字，`#FF6B6B` 0 命中属正常。

### 残留风险

- 焦点环只在截图上验过存在，没做键盘 Tab 的逐控件巡览（UIA 驱动不支持 Tab 序断言）；`SidebarItem` 与 `GradeButton` 的 ring 用 `Margin="-2"` 外扩，紧贴容器边缘时会被裁掉 1–2px，要不要改成内缩留给下一版。
- `BrandText` 只覆盖了「品牌色当文字」这一类；`Accent`（`#40A9FF` / `#5BB4FF`）当文字用的场景目前没有，真要用得另配一档。
- 字号阶里 10px 那六处是刻意保留的，但如果以后再加 chip 类小标签，需要明确它属于 10 这一档而不是跟着正文走。

## v70 · 侧栏悬停画出点击时那圈品牌色边框

用户提了两轮，第二轮的定论（原话）：「当前鼠标悬浮上去特效是有淡淡的阴影，点击侧栏会有蓝色边框，我想要的效果是，悬浮上去就会有点击时的蓝色边框」。

第一版把悬停底色换成活动项的 `BrandFillBrush`，是这个需求的错读——用户要的「蓝色」不是蓝底，是点下去时那一瞬间出现的 2px 外环。范围仍然只有一条：**只改悬停，`SidebarItemActive` 一个字不动**。

### 为什么悬停原来「没有边框」

点侧栏时那圈蓝不是点击态的样式，是 `IsKeyboardFocused` 触发器画的 `ring`（`src/FastTranslation.App/App.xaml:488`）。点完 `SyncSidebar()`（`src/FastTranslation.App/MainWindow.xaml.cs:297-304`）把该项换成 `SidebarItemActive`，而活动项那支模板整个重写过（`App.xaml:499`，只覆盖左侧竖条，因为控件模板没法只替换继承模板里的某一个元素），里面没有 `ring`，所以蓝边一闪就消失——这正是用户觉得好看的瞬间。

### 改法

两处，都在 `src/FastTranslation.App/App.xaml` 的 `SidebarItem`：

- `App.xaml:462`：模板根 `<Grid>` 补 `x:Name="root"`。
- `App.xaml:479-485` 的 `IsMouseOver` 触发器：给 `ring.BorderBrush` 设 `BrandBrush`（和焦点环同一圈、同一个 `Margin="-1"` 与 `CornerRadius="9"`），`fill.Background` 保持原来的 `HoverBrush`（浅色 `#F0F4FA` / 深色 `#262C35`）。

v60 那个「被手推了一下」的 `TranslateTransform X=2` 也从 `fill` 挪到了 `root`——**这是第二遍才发现的坑**：位移只加在 `fill` 上时，填充整体右移 2px 会把环的右边整段盖住，截图上只剩左/上/下三段（右边缘 `#4A82FF` 命中 0）。改挂到 `root` 后环与填充一起挪，四边等宽。

### 取证

脚本 `tools/qa/shot-sidebar-hover.ps1`（`-ExePath -Theme -Out [-AutoId]`，默认 `WordButton`）：备份 settings.json → 注入 Theme → 杀进程 → 启动 → UIA 按 AutomationId 找按钮 → 光标先挪到 (5,5) 停 120ms、再 `SetCursorPos` 到按钮中心 → 停 900ms → `PrintWindow(hwnd, dc, 2)` 存 PNG → 还原 settings.json。截图 1820×1155。

侧栏那一列在截图里是 x∈[30,300]，逐行数品牌色像素分出两条带：y∈[152,214] 是当前页「首页」，y∈[221,284] 是悬停的「单词」项，两者之间的 y 218–219 与 286–287 就是悬停项的上下两条边框。

| 取样 | 深色 | 浅色 |
| --- | --- | --- |
| 环的上下两条（y 218–219 / 286–287） | `#4A82FF` ×474 | `#2562FF` ×474 |
| 悬停带内部（y 221–284） | `#262C35` ×16143、字 `#A3AEBF` ×114 | `#F0F4FA` ×16143、字 `#5C6B82` ×114 |
| 同帧当前页「首页」（y 152–214） | `#22304D` ×15613、字 `#8FB4FF` ×366 | `#EDF2FF` ×15619、字 `#1D4FE0` ×366 |

- 环色与活动项左侧竖条同色（`#4A82FF` / `#2562FF`），正是「点击时那一圈」。
- 中点 y=250 逐像素：左 `x31 #38579A → x32 #4A82FF → x33 #38579A`，右 `x297 #38579A → x298 #4A82FF → x299 #314F93`，两侧对称；上下各占 2 行。四边都在，宽度一致。
- 悬停带内部仍是 `HoverBrush`，没有变成活动项的 `BrandFill` —— 悬停＝「带蓝边的一行灰底」，活动＝「蓝底 + 竖条 + SemiBold」，两者一眼能分开。

### 验证

- `dotnet build FastTranslation.slnx -c Release` → **0 警告 0 错误**（构建前先 `taskkill //F //IM FastTranslation.exe`，否则 MSB3021 DLL 被锁）。
- `dotnet test FastTranslation.slnx -c Release` → Core **218** / Platform.Windows **97** / QwenMt **94** / Server **193** = **602 全绿**。
- 视觉证据：`publish/hover-v4-dark.png`、`publish/hover-v4-light.png`（`publish/` 不进仓库）。

### 残留风险

- 悬停与焦点环现在共用同一个 `ring`：键盘 Tab 到某一项、鼠标同时压上去时只显示一圈，两者视觉无差别。当前没有「焦点在这一项但鼠标在别处」的复合态，问题不大；以后若加键盘导航提示，需要把环改成两档颜色。
- 环是 `Margin="-1"` 外扩画在行外的，侧栏左边界距窗口边只有 ~28px，够用；若以后侧栏内边距收窄到 2px 以内，环会被窗口边界裁掉。

## v71 · 按审计结果校正文档与记忆文件的不符处

**为什么改**:语音实时翻译与文档翻译的方案评估过程中,发现「给所有会话注入记忆的文件」(`AGENTS.md`)以及若干说明文档里写死的事实已经和仓库现状脱节。这类文件错了不会让构建失败,却会持续误导之后每一个会话,所以单开一轮做校正。

### 改了什么

**`AGENTS.md`**
- 模块清单补全:图片模型写 `qwen-mt-image-2.0`(不是 `qwen-mt-image`),网关补 `/v1/ocr`,Providers 写明文本/图片/OCR 三类各有一套「直连」与「自建网关」通道,Core 与 App 的行补上实际存在的主要文件。
- 删掉已不存在的 `NotesWindow`(删除提交 `faa836b`),改指向 `Pages/NotesPage` 与 `Pages/ReviewPage`。
- 计数类断言一律换成实测:类 `sealed` 82 → **148**,`ConfigureAwait(false)` 118 → **108**,`docs/IMPL.md` 1300 行 → **1790 行(最新 v70)**。
- 令牌自检从 `git grep -n 'ghp_\|github_pat_'` 改为 `git grep -In 'ghp_[A-Za-z0-9]\{30,\}' -- ':!*.md'`:原裸模式必然命中本文件里的模式串本身,「应无输出」这条自检永远发出误报。
- 注释回报示例从 `Gateway/GatewayEndpoints.cs:334` 改到 `server/FastTranslation.Server/GatewayOptions.cs:48`(行号与语义双错位:该行现在是 `EstimateInputTokens`,图片折算常量在 `GatewayOptions.ImageQuotaChars`)。
- 提交 scope 白名单承认中文 scope 也是既有事实(`feat(界面)` / `feat(复习)` / `feat(单词本)` 等大量存在),不再与实际历史互相打架。
- 踩坑段三处纠正:heredoc 报错改为条件式描述(本机 MSYS2 bash 三种写法实测都跑通,`Unexpected character` 是 Node 解析器文案);BOM 现状写明 34/69;`$_` 的真实失败模式是 `cmd /c` 的引号剥离,不是 bash。
- 700 行上限补现状例外(`App.xaml.cs` 1291 行、`OverlayWindows.cs` 742 行,拆它们要连带搬实例字段,排在专门重构里做)。
- 版本号第 4 条的「最近就漏改过,停在 1.1.3」改成 1.3.7 之后的真实漂移;`.agents/issue` 标注只有 `.gitkeep`、尚未真用过。

**其它文档**
- `docs/PLAN.md` §2 的「T15 的安装器实际用 WiX v4 MSI(非 Inno Setup)」改为历史记录:T15 当时用 WiX,**v30 起已改用 Inno Setup**;`installer/Product.wxs` 与 `files.wxs` 是遗留死文件。§1 补一句指向方案文档 §2.6,说明「不分段不流式」在语音/文档两项新功能上的越界边界。
- `README.md`:安装段包名 1.2.2 → 1.3.7;测试数 24/48/6 更正为 **218/97/94/193(合计 602)** 并补上缺失的 Server.Tests 一行;`IMPL.md 迭代史 v1–v29` → **v1–v70**。
- `site/index.html`:版本徽标、hero 说明、两张安装卡文件名 1.2.2 → 1.3.7;包体积按实测改成约 45 MB / 60 MB(原写 70 MB 是旧数字)。
- `docs/playbooks/release-and-installer.md`:发版自检的「`dotnet test` 408 全绿」改为当前基线 602 并注明以本机输出为准。
- `deploy/appsettings.Production.json.example`:补上 `Smtp` 段占位。代码一直在用(`ServerOptions.Smtp` → `Auth/VerificationMailSender`),example 里却从未有过这个段,照着 example 部署会漏配。

### 补充(第二轮):三份新方案文档按代码事实校正

第一轮只核了 `AGENTS.md` 与说明文档,三份新方案文档(`docs/superpowers/specs/2026-10-31-语音与文档翻译-方案评估.md`、`.agents/design/2026-10-31-语音与文档翻译-{设计文档,实现计划}.md`)里对现有代码的断言没核过。第二轮逐条对照源码,改掉 8 处硬错与 9 处偏差,其中两处会直接把实现带进坑里:

- **配置键前缀错了会静默失效**。文档写 `Gateway:Speech:*`,但配置根节是 `Server`(`ServerOptions.SectionName = "Server"`,`GatewayOptions` 挂在 `ServerOptions.Gateway` 下),完整路径必须是 `Server:Gateway:Speech:*`。键名写错不会抛异常——属性保持默认值,表现为 `Enabled` 永远 false、端点一直 503,排查起来像"开关没生效"而不是"配置绑不上"。同时补上实现前提:现有 `GatewayOptions` 是扁平属性,要支持这层嵌套必须新加 `public SpeechOptions Speech { get; set; } = new();`。
- **`RecordUsage` 没有 `source` 参数**。`server/FastTranslation.Server/Storage/AccountStore.cs:239` 的签名里没有它,SQL(`:274-276`)里 `'gateway'` 是硬编码字面量。文档原以为"只改端点就行",漏了 `AccountStore.cs`;不改则 `source='document'` 写不进去,设计文档 §11.2 的 `Document_source_header_is_recorded` 用例必红。改动清单据此从 16 项补到 17 项。

其余修正:`Intent` 是**四道**闸门不是五道(限流 → 日预算 → 账号禁用 → 额度,`Gateway/GatewayEndpoints.cs:221`);`LanguageCatalog` 是 74 语言不是 77;网关回调真名是 `GatewayTranslateImageAsync` / `GatewayRecognizeImageAsync`(文档写的 `GatewayImageAsync` / `GatewayOcrAsync` 不存在);「防刷三道闸」里的设备指纹 90 天在代码里搜不到,实际只有邮箱验证码与单设备日 2000 字符两道;基线 git 头 `0e71b6c` 已是 HEAD~2,改 `3c714a7`;`shot-main.ps1` 实际叫 `main-shot.ps1`;`PricingTests.cs` 尚不存在(标为新建);侧栏行高 38px 不是 34px(`App.xaml` 的 `SidebarItem` `Height=38`),`MainWindow.xaml.cs` 的 344 行是整文件而 `Create` 的 `switch` 只有 12 行。

版本号整体后移一位:文档 1.4.0 定为 **v72–v75**、语音 1.5.0 定为 **v76–v79**,因为本节的 v71 已经占用了原定的起点。§1.3 从「四个必须先拍的决策」改为五个(补 Q5 独立日预算与带宽硬顶),§11 补回漏掉的 Q3 并重排编号。

### 验证

- 纯文档与配置样例改动,未触碰源码;`deploy/appsettings.Production.json.example` 已用 Python `json.load` 校验合法。
- `python -c` 校验 + `git status --short` 干净(仅本机 `publish/` 内产物,已在 `.gitignore`)。
- 提交:`0de8fb9`(AGENTS.md)、`2316a8c`(方案文档与计费 spec 勘误)、`1b1040c`(版本号与过期断言)、`19db100`(三份新方案文档按代码事实校正),已 `git push origin master:main`。

### 残留风险

- 审计里「无法机器核实」的条目(GitHub Pages 600 秒缓存、`ghfast.top` 连不上、`SendInput` 返回 0、UIA 在 Inno 向导里、旧 MSI 1603)保持原样:它们记录的是当时实测到的现象,本次没有条件复现,不做改动。
- `AGENTS.md` 里的计数(148 / 108 / 1790)同样会随时间漂移。本次已注明是实测值,但下次改动仍可能让它们过期——计数类断言本身就该少写。
- `publish/.server-secrets.json` 与 `publish/ft-emergency-copy-put-this-off-this-laptop` 仍留在本机(`.gitignore` 内,未进 git)。前者是 `docs/IMPL.md` v60 明确记录的两份密钥副本之一(签名密钥 + 管理令牌),后者是应急 SSH 私钥的异地副本,**都是有意保留的关键备份,不是泄漏**,不要随手删。

### 补充(第三轮):五个决策拍板,方案文档状态转为可实施

用户 2026-10-31 拍板 Q1–Q5(顺序 / 语音路径 / 额度折算 / 文档失败粒度 / 文档范围 / 语音预算与带宽),方案评估文档的状态从「待用户拍板」改为「决策已定,可进实施」:

- 方案文档 §1.3 标题与表头从「五个必须先拍的决策 / 建议」改为「五个已定决策(用户 2026-10-31 拍板) / 已定」,并加一句「不再讨论;有异议请改这一行并在 `docs/IMPL.md` 记一条,不要在实现里默认别的选项」——避免实现时有人按「建议」两字重新权衡。
- 方案文档 §11 从「待用户拍板」七条改写为「§11.1 已拍板(六行表)+ §11.2 仍待确认(三条,不阻塞 Phase D)」。原七条里「顺序」并进已拍板表(先文档后语音、两者可并行开工),「只抓某个 App」降为二期待确认。
- 设计文档状态行改成「决策已定(Q1–Q5)」,其「5 个待拍板项」改为「5 个已定决策,见其 §1.3 / §11.1」;实现计划头部加状态行「可开工 Phase D」。
- 没有新增 `vN`:本次只改文档状态,代码零改动,归入 v71 这一轮的收尾。

验证:三份文档 grep「待拍板 / 待用户」为 0 命中(`方案:6` 的「拍板后另出」也一并改成配套链接);`git diff --stat` 3 files changed, 26 insertions(+), 14 deletions(-)。

## v72 · 文档翻译(一):文档层与段模型

**为什么改**:用户 2026-10-31 拍板要先做文档翻译(方案文档 Q1)。百炼没有「文档→文档」的托管 API——alimt 的 `CreateDocTranslateTask` 要求 `FileUrl` 是公网可下载地址,结果链接 1 小时过期,回调还要 IP 白名单,从我们的网关转发根本不成立;`qwen-doc-turbo` 是抽取/摘要不是翻译,`qwen-long` 只出纯文本。所以只能自己做「本地解析 → 分段调 `qwen-mt` → 本地回写」这条链路。

**为什么先做 Core、且不引库**:docx 就是一个 zip,正文在 `word/document.xml` 的 `<w:p>`/`<w:r>`/`<w:t>` 里,`ZipArchive` + `XDocument` 是 BCL 现成的。`DocumentFormat.OpenXml 3.5.1` 的 dll 有 7.8 MB、反射重、裁剪有风险;`PdfPig` 5.6 MB 且 < 1.0 不遵 SemVer——都不值得为了省两百行代码引进来(桌面端至今零 PackageReference)。

### 改了什么

新增 `src/FastTranslation.Core/Documents/`(不碰 WPF,便于单测直接驱动):

- `DocumentFormat.cs`:`enum { Docx, Text, Markdown }`、`DocumentFormats.SupportedExtensions = ".docx / .txt / .md"`、`TryFromPath(string?, out DocumentFormat)`(扩展名 `OrdinalIgnoreCase`)。
- `DocumentSegment.cs`:`enum SegmentStatus { Pending, Done, Failed, Skipped }` 与 `record DocumentSegment(int Index, string Path, string Source, string? Target, SegmentStatus Status)`。**`Path` 不是数组下标**:docx 用 `"{part}#p{n}"`、txt/md 用行号——跳过空白段后下标与 XML 里的段落序数不再一一对应,按数组下标回写会整体错位一段(v72 第一版就是这么错的)。
- `IDocumentTranslator.cs`:`Format` / `LoadAsync` / `WriteBackAsync`,实现类必须**无状态**(作业可能被取消后续译,段的处理状态归 `DocumentJob` 持有)。
- `DocumentLimits.cs`:`MaxFileBytes = 10 MB`、`MaxChars = 200_000`、`MaxSegments = 5_000`、`MaxSegmentChars = TranslationRequest.MaxInputLength`(5,000),超限直接给中文提示让用户拆分。10 MB 对应整包 `XDocument` 的 5–10 倍内存;20 万字符约 0.45 元;5,000 段是「一段一请求、并发 4」的上限。
- `SegmentTextPolicy.cs`(internal):`ShouldTranslate` = 长度 ≥ 2 且不是纯空白/纯符号/纯数字;**纯数字与纯符号不该花钱翻**;`UntranslatedMarker = "【未翻译】"`;`WriteBackText` 三态(Done+Target→译文 / Failed→标记+原文 / 其余→原文)。
- `TextDocumentTranslator.cs`:txt/md 按行切段,读用 `new UTF8Encoding(false, false)`——抛异常的严格解码会把 GBK 文件读崩。md 不做语法感知(代码块里的英文也不翻),这是 MVP 的明确取舍。
- `DocxDocumentTranslator.cs`(189 行):只处理 `word/document.xml` 与 `word/header*`、`footer*`、`footnotes.xml`、`endnotes.xml`,**不碰 `comments.xml`**(审核批注,翻了会破坏批注归属)。段落级而非 run 级:纯文本 run 串成源串、`drawing/pict/object/fldChar` 当占位符、整段送译、译文写回第一个纯文本 run 的 `<w:t>` 并删其余纯文本 run,`<w:pPr>` 与 `rPr` 保留。
- `SegmentTranslator.cs`:合并短段(空行分隔、回切校验段数) + 并发 4 + 重试 3 + 二分降级,详见 v73。

工程:`FastTranslation.Core.csproj` 加 `InternalsVisibleTo`,测试工程加 `<None Include="Documents\Fixtures\*.docx" CopyToOutputDirectory="PreserveNewest" />`;夹具 `tests/FastTranslation.Core.Tests/Documents/Fixtures/sample.docx`(38 KB,python-docx 生成:`word/document.xml` 9 个 `w:p` 其中第 7 个是纯 `w:drawing` 无文本必须跳过,`word/header1.xml` 1 个)。

### 验证

- `dotnet build FastTranslation.slnx -c Release` 0 警告 0 错误。
- `tests/FastTranslation.Core.Tests/Documents/` 覆盖:格式识别、上限拒绝、段状态机、合并与回切、docx 加载 9 段(跳段占号)、docx 回写保结构(`w:br`/`w:tab`/`xml:space`)。
- 夹具实测:源中文全部译出,9 个 `w:p` 的 `pStyle` 全保留,第 7 个 drawing 段原样,加粗 run 的 `<w:b/>` 仍在——见 v75。

### 残留风险

- 段落级回写的代价是**段内局部加粗/斜体/颜色/超链接会丢**(整段只有一个 `<w:t>` 承接译文)。二期折中是「连续且 `rPr` 相同的 run 视为单元」;per-run 翻译会把句子切碎,已明确否决。
- 不处理 `comments.xml`:带批注的文档译完后批注仍在原文上,可能对不上位置。
- md 的代码块、行内代码不做语法保护(v73 的哨兵只保护行内代码/URL/公式这类「不像自然语言」的片段)。

## v73 · 文档翻译(二):合并、重试、降级、断点与回写

**为什么改**:`qwen-mt` 只吃单条 `role=user` 消息,没有批量、没有编号对齐参数,官方对「一次多段怎么保证顺序」零承诺。按「一段一请求」直译 100 页文档会发出 658 个请求——正好撞上游 60 RPM 与网关 40 RPM 的墙,并且每请求还有约 45 token 的固定开销白烧。所以必须自己做「合并 + 回切校验 + 重试 + 降级」这一层。

### 改了什么

**`src/FastTranslation.Core/Documents/SegmentTranslator.cs`(本版最终 505 行)**

- **合并**:连续、同一 part、`Index` 相邻、单段 < 2,000 字符、累计 ≤ 4,000 字符、最多 20 段,合成一个单元,段间用空行 `"\n\n"` 分隔。回切时按 `"\n\n"` 切回,**段数不符就整包重试**——模型把两段并成一段或多切了,只能作废这一包,不能猜。
- **占位符保护** `PlaceholderProtector`:行内代码、URL、邮箱、公式、变量这类「不该被翻译」的片段换成 `⟦n⟧` 哨兵(U+27E6/U+27E7,不含 ASCII 字母数字与标点,比 `[[1]]` 更难被模型改写),回写前按哨兵表反替换并**校验计数**,对不上整段回退原文。合并时每段各自 Protect(哨兵从 1 起),回切后按段还原,不会串号。
- **重试**:每包最多 3 次;**重试之间指数退避** 1 秒 → 2 秒 → 4 秒(最后一次不再等)。不退避的 3 次重试在几十毫秒内就烧完,等于只有 1 次机会。
- **降级链**:整包 3 次不成 → 单元只有 1 段就单段重试;多于 1 段就**二分递归**(而不是直接退化成「一段一请求」——20 段的单元一次退化成 20 个请求,等于把刚压下去的请求量又还给限流)。全部用尽 → 该段标 `Failed`,回写时保留原文并插 `【未翻译】`。
- **进度**:按「已完成的段数」用 `Interlocked.Add` 累计,不按单元(按单元计永远到不了 100%)。

**`SegmentTranslator` 里的 `RequestPacer`(v75 定型,详见 v75)**:请求数与字符量两条约束,取更长的那个时间片,逐个平滑放行。

**`src/FastTranslation.Core/Documents/DocumentJob.cs`(162 行):断点续译**

- 断点文件放 `%LOCALAPPDATA%\FastTranslation\documents\{SHA256(绝对路径|长度|LastWriteTimeUtc.Ticks)}.json`,只存 `{ i, path, t }`(序号、段路径、译文)——不存原文,也不存整份文档。
- 序列化用 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`:默认编码器会把中文转成 `\uXXXX`,断点文件膨胀六倍。
- 写盘先写 `.tmp` 再 `File.Move(overwrite: true)`,断电不会留下半个文件。
- 回填时校验 `TargetLanguage`、`TotalSegments` 与 `Path` 序列,任一不符**整份放弃**——错位回填会把 A 段的译文写进 B 段,比丢掉断点糟得多。坏文件 catch 后当没有断点。
- 整份译完立刻删断点,不留垃圾。

**`src/FastTranslation.Core/Documents/DocxDocumentTranslator.cs` 的回写**

- 先 `File.Copy` 源文件到输出路径,再改副本——**绝不覆盖源文件**:翻译中途出错时用户至少还有原件,而「原件被半成品覆盖」不可恢复。
- 改完一个 part 必须 `stream.SetLength(0)` 再写回;**漏了这一步生成的 docx 打不开**(旧内容残留在 zip 条目尾部)。这是本轮踩到并修掉的坑。
- `Apply` 保留 `w:br`/`w:tab` 等非文本 run 子元素,并给写回的 `<w:t>` 设 `xml:space="preserve"`,否则译文首尾空格被吞。

**`src/FastTranslation.Core/Documents/DocumentTranslationCoordinator.cs`(236 行):编排**

顺序是**格式 → 文件大小 → 解析 → 计字符 → 段数/字符上限 → 额度预检 → 输出路径 → 断点回填 → 逐段翻译 → 存断点 → 回写 → 删断点 → 交结果**。

- **额度预检必须在开始翻译之前**:译到 80% 才发现额度不够,用户既拿不到完整文档,已经花掉的额度也退不回来。文案 `"本月剩余额度只够 {0:N0} 字符,本文件约 {1:N0} 字符"`。
- 计字符只算 `NeedsTranslation` 的段——没译成的与跳过(空白/纯数字)的段都不计额度,服务端也只按真正送上去的字数记账。
- 输出名 `{原名}.{目标语言}.{扩展名}`,放源文件旁边(或用户选定目录)。
- 进度回调**直接调,不走 `Progress<T>`**:`Progress` 会投递到捕获的同步上下文,在 WPF 里等于「每译完一段切一次 UI 线程」,而调用方自己会切。
- 取消静默、其它异常一律 `_reportError(ex.Message)`;`QuotaExhaustedException` 单独一条文案,说明「已译好的段落已保存在断点里」。
- 构造 7 个参数全部 `ArgumentNullException.ThrowIfNull`,包括 `progress`。

### 验证

- `dotnet test tests/FastTranslation.Core.Tests -c Release --filter 'FullyQualifiedName~Documents'` → 50 通过 0 失败。
- 用例覆盖:合并阈值(2,500 字符段不合并)、进度从 0 到 1(`updates[0].Done == 0`、`updates[^1].Fraction == 1d`)、失败段标 `Failed` 不抛、已完成段跳过、取消传导、额度预检拒绝(`Refuses_to_start_when_the_quota_is_too_small`)。
- 真实端到端(sample.docx 9 段):`failed=0 chars=139`,输出 docx 部件清单与源完全一致,`pStyle`/加粗/页眉/纯 drawing 段全部保真。

### 残留风险

- 合并的段数校验靠 `"\n\n"` 回切,模型若在译文里留下多余空行会误判成「段数不符」而白白重试。已用二分降级兜底,代价是极端情况下请求数上升。
- 断点按「路径 + 长度 + 修改时间」认领,用户原地改了文件就会重新从头译(这是有意为之:宁可重译,不可错位)。

## v74 · 文档翻译(三):文档页、侧栏入口与截图钩子

**为什么改**:Core 层在 v72/v73 已经能跑完一份文档,但用户只有代码能用。这一步把它接到界面上:侧栏加「文档」入口、新页面承担「选文件 → 预估 → 翻译 → 看结果/看未译段」全流程。页面抽成独立 `UserControl` 而不是塞进 `MainPage`,是因为 `MainWindow.xaml.cs` 已经 375 行,而页面自身有拖放、语言选择、额度预检、进度节流、未译清单五块状态,塞进去必然破 700 行上限。

### 改了什么

**`src/FastTranslation.App/Pages/MainPageId.cs`**:枚举加 `Document`,放在 `Review` 之后、`Account` 之前(功能页聚在一起,账号/设置靠后)。注释从「七个页面」改为「八个」——**枚举顺序就是侧栏渲染顺序**,中间插入会连带移动后面所有按钮,这是有意为之。

**`src/FastTranslation.App/MainWindow.xaml`**:`ReviewButton` 与分隔线之间加 `DocumentButton`(`Style="{StaticResource SidebarItem}"`、`Tag="Document"`、`Click="Nav_Click"`、`Margin="0,2,0,0"`),顶部注释同步改八个页面。

**`src/FastTranslation.App/MainWindow.xaml.cs`**(375 行):把原来内联在 `Build` 里的 `Create(page)` 抽成 `private UserControl CreatePage(MainPageId page)`,加 `MainPageId.Document => Document()` 分支与 `_documentPage` 缓存字段、`RefreshPage` 加 `Document` 分支;构造参数加 `DocumentTranslationCoordinator documents`(第 7 位)。另加三个 public 转发方法:

```csharp
// v72: 文档作业的三个回调从 App 一路转到这里，再由页面落到控件上。转一层是为了让
// App 不必知道页面是否已经构造过——窗口藏在托盘里时作业照样能跑完，只是没人画它。
public void PresentDocument(DocumentJobResult result) => _documentPage?.Present(result);
public void ReportDocumentError(string message) => _documentPage?.ReportError(message);
public void ReportDocumentProgress(double fraction, int done, int total) =>
    _documentPage?.ReportProgress(fraction, done, total);
```

**`src/FastTranslation.App/Pages/DocumentPage.xaml`(141 行)** + **`.xaml.cs`(453 行)**:拖放区(`AllowDrop` + 虚线框,照 `ReviewPage.xaml` 的 `Rectangle` 虚线写法)+「选择文件」`PrimaryButton`(`Microsoft.Win32.OpenFileDialog` 而非 WinForms 的 `FolderBrowserDialog`——桌面端不引 WinForms)+ 目标语言 `FtComboBox`(复用 `LanguageCatalog`,74 种)+ 额度预估文案 + `MeterBar` 进度(样式在 PlanPage 里是页内资源,**App.xaml 没有全局版,本页自带一份**)+ 完成态【打开所在文件夹】/【再译一份】+ 未译清单(`ItemsControl`,每条带段路径与原文,底部一个「重试未翻译的段」)。提示条复用 `DangerBrush`,**不新增主题令牌**——`ThemeResourceKeysTests.EveryDynamicResourceKeyInAppXamlIsDeclaredOrInjected` 会让未声明的 `DynamicResource` 直接红,新增令牌就得同步改 `ThemePalette`、`ThemeBridge` 与两套主题测试,为一个提示条不值得。

**`src/FastTranslation.Core/Settings.cs`**:`AppSettings` 加 `DocumentTargetLanguage = "en"` 与 `DocumentLastFolder = ""`(JSON 缺字段即默认值,不需要迁移)。

**`src/FastTranslation.App/App.xaml.cs`**(1383 行):

```csharp
// v72: 文档翻译单独一条 provider 链，**不经过 TranslationService**——那个服务每次新请求会
// 取消上一次在途请求（划词时"最后一次输入赢"），而文档是一份文档几十段并发在飞，
// 走它就会变成每段互相打断、只有最后一段能译出来。
_documentProvider = new GatewayTranslationProvider(GatewayTranslateAsync, ownProvider);

// 作业的三个回调转给主窗口再转给文档页：窗口可能正收在托盘里，作业照样要能跑完。
_documents = new DocumentTranslationCoordinator(
    format => format switch
    {
        DocumentFormat.Docx => new DocxDocumentTranslator(),
        _ => new TextDocumentTranslator(format),
    },
    TranslateDocumentSegmentsAsync,
    outputDirectory: () => null,   // null = 落在源文件旁边
    present: result => _mainWindow?.PresentDocument(result),
    reportError: message => _mainWindow?.ReportDocumentError(message),
    progress: (fraction, done, total) => _mainWindow?.ReportDocumentProgress(fraction, done, total));
```

`TranslateDocumentSegmentsAsync` 照网关回调的形状(登录态 + 网关健康 + 令牌 + `QwenMtProvider` 现造),但**返回 `string` 且失败要抛异常**——不能像划词那样返回一个带 `Error` 的结果对象,因为 `SegmentTranslator` 靠异常决定「重试还是标未译」。额度/余额耗尽抛专用 `QuotaExhaustedException`(v75)。另加 `ShowDocument()`;`ExitApp` **不 Dispose** `_documentProvider`(它包着与划词链路共用的 `ownProvider`,Dispose 会顺带关掉用户的 Key provider)。

**`tools/qa/shot-document.ps1`(86 行)**:四态 × 浅深各一,共 8 张截图。用 `PrintWindow(hwnd, hdc, 2 /*PW_RENDERFULLCONTENT*/)` 而不是 `CopyFromScreen`——第一次用 `CopyFromScreen` 截到的是前台浏览器窗口,`SetWindowPos(HWND_TOPMOST)` 不足以保证应用在最前。四种形态由 `FT_UITEST=document / document-progress / document-done / document-failed` 钩子驱动,直接喂回调给页面,**不跑真实翻译、不耗额度**。

### 验证

- `dotnet build FastTranslation.slnx -c Release` 0 警告 0 错误;`dotnet test` 661 通过 0 失败(Core 277 / Platform 97 / Providers 94 / Server 193)。
- 8 张截图目视通过:空态、进度态(「已完成 42 / 120 段」+ 35% 蓝条)、完成态(「季度报告.en.docx · 120 段 · 计 8,432 字符」)、未译态(红框「有 2 段没译成」+ 段明细 + 重试按钮);深色四态配色跟随。
- 侧栏「文档」出现在「复习」之后、「套餐」之前。

**截图当场抓出两个真 UI bug:**

1. **进度态与空态字节完全相同**(SHA256 一致):`ReportProgress` 只更新字段与节流计时器,从不把 `ProgressCard` 置 `Visible`;而 `RunAsync` 里那句 `Visibility = Visible` 排在 coordinator 首次报进度**之后**才轮到 UI 线程。修法是把「露出进度卡」放进 `ReportProgress` 本身。
2. **未译卡片在视口外看不见**:页面是 `ScrollViewer`,1040×660 装不下「拖放 + 翻译成 + 进度 + 结果 + 未译」五张卡,有段没译成时用户看到的和全部译成时一模一样。`BringIntoView()` 直接调无效——`Visibility` 刚改、此刻卡片还没有高度,必须 `Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ...)` 等这一轮布局跑完再滚。

### 残留风险

- 页面 453 行,离 700 行上限还有余量,但已接近「该拆」的体感阈值;若二期加 xlsx 选项与「高级(术语表)」区,应先把结果区与清单区拆成两个子控件。
- 只有拖放与文件对话框两种入口,不支持多文件排队——一次一份,与 `Document()` 单例工厂一致。

## v75 · 文档翻译(四):节流、额度耗尽与真实端到端验收

**为什么改**:v72–v74 的实现用 `publish/probe-doc` 探针跑 100 页真实 docx(658 段 / 162,849 字符)时暴露出一串真问题:3 分 41 秒跑完、**63%(417 段)失败**,日志里一片 `RateLimited`。顺着这条线查下去,发现问题根本不在我们的代码写得慢,而在三处对上游行为的错误假设。这一版专门修它们。

### 改了什么

**1. `src/FastTranslation.Providers.QwenMt/QuotaRefusal.cs` —— `insufficient_quota` 被误判成限流(最严重的一条)**

`DashScope` 直连返回 429 时,响应体是**对象**形状:

```json
{"error":{"message":"You exceeded your current quota, …","type":"insufficient_quota",
 "param":null,"code":"insufficient_quota"},"request_id":"…"}
```

而 `ReadErrorCode` 原来只认自建网关的字符串形状 `{"error":"<code>"}`,遇到对象返回 `null`,于是落到 `ClassifyStatus` 的 `429 => RateLimited`,再被无意义地重试一次,最后给用户的提示是「免费额度正在排队,这次用了你自己的 Key」——**账户额度已经耗尽,却告诉用户「稍后再试」**。现在两种形状都认(`JsonValueKind.String` 与 `JsonValueKind.Object` 里的 `code`),新增常量 `InsufficientQuota = "insufficient_quota"` 与文案「API Key 的额度或余额不足」。

**2. `src/FastTranslation.Core/Documents/SegmentTranslator.cs` —— 合并阈值 + 按字符量平滑节流 + 重试退避**

- **合并阈值**:`MergeMaxSegments` 5 → **20**、`MergeMaxSegmentChars` 80 → **2,000**、`MergeMaxTotalChars` 400 → **4,000**。旧值让 658 段发出约 658 个请求、峰值 180 RPM;合并到接近 `TranslationRequest.MaxInputLength`(5,000)后请求数降了一个数量级。留 1,000 字符余量是因为占位符保护会小幅膨胀原文。
- **`RequestPacer` 重写为「请求数 + 字符量」双约束的平滑时间片**(它中间还经历过一版「滑动窗口记账」,被实测否掉了,见下):用 `_slots` 串行、算 `charTicks = chars * TicksPerMinute / _charsPerMinute`,取 `max(_requestIntervalTicks, charTicks)` 作为这一笔占用的时间片,`_nextTicks = start + 片长`。默认 `RequestsPerMinute = 30`、`CharsPerMinute = 24,000`。
- **重试退避**:`WaitAfterFailureAsync(attempt, maxAttempts, ct)` = 1 秒 → 2 秒 → 4 秒,最后一次不再等。旧代码 3 次重试之间零等待,几十毫秒内就烧完,等于只有 1 次机会。

**3. `src/FastTranslation.Core/QuotaExhaustedException.cs`(新文件)+ 整份作业快速中止**

额度耗尽意味着「再试一次也一样」,而文档翻译里一段失败会被重试 3 次再标「未翻译」——658 段 × 3 次请求全打在同一个已经枯竭的额度上,用户多等几分钟,看到的仍是「有 400 多段没译成」。

- 新异常类型 `QuotaExhaustedException`,XML 注释说明「为什么要单独一个类型」。
- `SegmentTranslator` 两处 catch 前加 `catch (QuotaExhaustedException) { throw; }`;`TranslateAsync` 用联动 `CancellationTokenSource` 记住第一个原因并**叫停还在排队的单元**——否则每个单元还要先按 30 RPM 排队 2 秒才轮到它去撞同一堵墙。
- `DocumentTranslationCoordinator` 单独一条用户文案:「…已译好的段落已保存在断点里,额度恢复后再对同一份文件点开始会接着译。」——断点已经写盘,换 Key 或续费后重开就接着译,不说清用户会以为白等了。
- `App.xaml.cs` 的 `TranslateDocumentSegmentsAsync` 遇到 `TranslationErrorCode.QuotaExhausted` 抛专用异常,而不是普通 `InvalidOperationException`。

### 上游配额实测(决定节流参数的依据)

用 `publish/probe-raw`(裸 `HttpClient` 直连 DashScope,`<chars> <intervalMs> <count>`)测同一账号:

| 字符数 | 间隔 | 次数 | 成功 | 折算 |
|---|---|---|---|---|
| 200 | 1,000ms | 20 | 20 | 7,356 字符/分 |
| 200 | 6,000ms | 8 | 7 | 1,778 字符/分 |
| 2,000 | 3,000ms | 5 | 5 | 25,511 字符/分 |
| 4,000 | 4,000ms | 12 | 3 | 13,612 字符/分 |
| 4,000 | 6,000ms | 10 | 10 | **31,149 字符/分** |
| 4,000 | 8,000ms | 4 | 4 | 28,735 字符/分 |
| 4,000 | 12,000ms | 8 | 7 | 16,590 字符/分 |

⇒ **约束是「按字符/token 量计的滑动窗口」(qwen-mt-flash 官方 35,000 TPM,错误链接就是 `#token-limit`),不是 RPM,也不是封号。** 同样 30 RPM,一段 4,000 字符 = 120,000 字符/分,远超 35,000 TPM——**纯按请求数节流挡不住**。这就否掉了中间的「滑动窗口记账」版本:它允许一次性突发掉整个窗口额度,探针里 28 个请求就把窗口打满、随后整份中止。最终取 24,000 字符/分(留约 30% 余量,剩下留给重试与同账号的划词请求)。

### 验证

- `dotnet build FastTranslation.slnx -c Release` → 0 警告 0 错误。
- `dotnet test tests/FastTranslation.Core.Tests -c Release --filter 'FullyQualifiedName~Documents'` → **50 通过 0 失败**(新增 `RequestPacerTests` 6 个 Fact、额度耗尽中止用例)。
- `dotnet test --filter 'FullyQualifiedName~Quota'` → 7 通过 0 失败(新增 `DashScopeQuotaExhaustion_IsNotMistakenForRateLimiting`,服务端回 429 + 真实对象形状 body,断言 `QuotaExhausted`、文案正确、`calls == 1`)。
- 全量 `dotnet test`(本轮收尾,四个项目)→ **669 通过 0 失败**(Core **284** / Platform.Windows **97** / Providers.QwenMt **95** / Server **193**),比 v74 那轮的 661 多出来的 8 条正是本版新增的节流退避、额度耗尽中止与 `insufficient_quota` 归类用例。
- 100 页真实端到端(`publish/probe-doc`,改造前基线):`PREVIEW segments=658 chars=162849` → `elapsed=221.2s`、`failed=417`(63%);改造后重跑:`elapsed=184.3s`、`requests=29`,随后被账户额度(`insufficient_quota`)中止。**这组数字证明了两件事:请求数确实降了一个数量级(658 → 29),以及本机测试账号的额度不足以跑完 162,849 字符。**
- 100 页纯本地回写(假翻译器,不耗额度):658 段 / 162,849 字符,**解析 + 回写全程结构无损**(样式、表格、页眉、加粗 run 全部保住)。

### 残留风险

- **验收线改写**:原计划 Task 10 第 1 条要求「100 页端到端 ≤3 分钟」。按实测上游上限(24,000–31,000 字符/分),162,849 字符的理论下限就是 **5.3–6.8 分钟**,3 分钟内跑完在物理上不可能——除非降级到付费更高 TPM 的模型。因此本条验收改为**「受上游配额限制,按字符计:24,000 字符/分以内不触发限流」**,3 分钟的原始指标作废并在此记录原因。
- 本机测试账号额度已耗尽,**100 页端到端 `failed = 0` 未能在本轮取得最终证据**;已用 9 段 `sample.docx` 与 100 页纯本地回写两条路径交叉验证正确性,真实全量端到端待额度恢复后补测。
- `RequestsPerMinute` / `CharsPerMinute` / `RetryBackoff` 是进程级 `internal static`,便于测试置零,但同进程内多个作业会共享一份节奏(当前 UI 一次只跑一份文档,暂不构成问题;若将来做多文档排队需改成实例级)。
- 24,000 字符/分是保守值。若上游实际放宽(或换更贵的模型档),可上调;反之若用户使用自带 Key 且额度紧张,30 RPM 的请求数约束会更早成为瓶颈。

### 发版收尾（版本号与文档面）

这一版同时把「产品版本号」和「对外文档」推到 1.4.0。按 AGENTS.md 的五处清单逐条改，用临时脚本 `publish/bump-140.py` / `publish/bump-portable.py` 做替换——**每个替换点先断言出现次数再替换**，避免 `1.3.7` 这种短字符串在别处误伤（`publish/` 整目录在 `.gitignore` 里，脚本不进仓库）。

- `Directory.Build.props` 的 `<Version>` 与 `installer/FastTranslation.iss` 的 `#define AppVersion`：程序集版本与安装包版本。
- `site/index.html` 四处硬编码：版本徽标、hero 说明、两张下载卡的文件名。
- `docs/portable-readme.txt`：标题行、安装段的包名，并在「更新记录」顶部加 `v1.4.0(2026-10-31)` 一节。

同时补上文档翻译的**对外说明**，否则下载页写着 1.4.0、正文却只讲划词翻译：

- `README.md`：能力列表新增「文档翻译」一条；「已知限制」补上 `.docx`/`.txt`/`.md` 的格式范围、10 MB / 20 万字符 / 5,000 段三个上限，以及「段落级回写会保住字体字号颜色段落样式，但段落内部的局部加粗/超链接会被合并成一种」这个真实代价；测试数字同步为 284 / 97 / 95 / 193（共 669）。
- `docs/portable-readme.txt`：新增 `【文档翻译】` 小节，写清产出文件名规则、原文件不动、只有段落文字出网、断点续译、段落级回写的取舍、三个上限、`.xlsx`/`.pptx`/`.pdf` 尚不支持、以及「速度受你自己 Key 的配额限制」——便携包用户是直接拿 Key 直连的，不写清楚会以为是程序慢。
- `site/index.html`：**新增第 03 节「文档」，后面四节编号顺延为 04–07**（导航同步加「文档」）。这一节用两张真实截图（浅/深各一，取自 `tools/qa/shot-document.ps1` 的完成态）加六条要点，讲清「整份文件、版式还在、原文件不动」。截图从 1820×1155 压到 980 宽放进 `site/assets/img/document-{light,dark}.png`（`assets/qa/` 在 gitignore 里，不能直接引用）。
- `site/assets/site.css` 两处小改：新增 `.mono` 正文等宽片段样式（原来只有 `footer .mono`，正文里的 `.docx` 会落回正文字体，与页脚不一致）；新增 `.doc-points` 两列要点，**并在 880px 断点里强制回一列**——用了 `column-count:2` 却不加窄屏回退时，手机宽度下每列只剩两三个字宽，实测截图里直接变成竖条。

验证：

- `site/` 用 Chrome headless 全页截图（1440 宽与 800 宽两档、浅色与 `--force-dark-mode` 深色），截图放在 `publish/site-shot/`（不进仓库）；确认新节版式与其余各节一致、编号连续、窄屏要点回到一列、深浅两套配色都正常。
- 版本号五处改完后 `grep -rn "1\.3\.7" --include=*.html --include=*.md --include=*.iss --include=*.props .` 无命中。

残留风险：

- 站点上的两张文档页截图是**开发机浅/深两种主题**的实拍，不含任何真实用户文件（演示数据是「季度报告.docx · 120 段 · 8,432 字符」），但用户若放大细看仍能看到窗口里的测试账号邮箱一角；截图已按 980 宽缩放，字号下细节不可读。
- 官网尚未提到语音翻译（下一版 1.5.0 才做），`#how` 一节仍然是「一颗工具条，三个动作」，届时需要一并改。

## v76 前置小节：Phase P0 语音 POC 结论（2026-11-01，不随 1.4.0 发版）

**为什么有这一节**：1.4.0 交付后用户说「继续」，按实现计划进入 Phase P0——三个探针（WASAPI 采集 / DashScope 实时 ASR / Caddy wss 透传），产出是结论不是代码。三个探针都在 `publish/`（已 gitignore），结论如下；三条全通，**Phase V（1.5.0）可以开工**。

### 探针一：WASAPI loopback 采集（publish/probe-wasapi）

- `src/FastTranslation.Platform.Windows/WasapiLoopbackCapture.cs`（310 行，进仓库）+ 控制台探针。实测：**默认渲染设备混音格式 48000 Hz × 2ch × 32bit float（blockAlign=8）**，事件驱动每秒约 60 次回调（10ms 包）。
- 采 5 秒（其中 2 秒 440Hz 正弦）：byte-count 比 1.002、有声窗 RMS 0.2486 对静音窗 0.00007（6800 倍）、事件回调 308 次。**loopback 在端点空闲时不出包**——字节数只按有声时长计，这是特性不是 bug（真实产品恒有静音段，计数要按 wall 时钟）。
- 16k 单声道 WAV 由 python wave 复核：3.08s/16000Hz/RMS 0.2445/过零率 856/s（440Hz 理论 880/s）——重采样频谱正确。
- 踩坑三条，全部已修并写进代码注释：**① IMMDevice 的 IID 写成了 IMMDeviceCollection 的 `0BD7A1BE…`（真值 `D666063F-1587-4E43-81F1-B948E807363F`，SDK 头文件机验）**——所有 GUID 校验必须机器字符串比对，肉眼读十六进制不可靠；**② ComImport coclass 壳类不能 sealed**（C# 显式引用转换禁止 sealed 类→接口，QI 转换要求非 sealed，CS0030）；③ WAVEFORMATEXTENSIBLE 在 C 里 4 字节对齐要垫 2 字节（SubFormat 落偏移 24），.NET 结构体复现不出嵌套对齐，按字节偏移读是唯一不踩坑的读法。

### 探针二：DashScope 实时 ASR 直连（publish/probe-asr）

- ClientWebSocket 连 **`wss://dashscope.aliyuncs.com/api-ws/v1/inference`**（普通域即可，无需 workspace 前缀域），模型 **`qwen-audio-3.1-asr-flash-streaming`**，run-task 协议六步全部走通；Key 用 DPAPI 从本机 `qwenmt-api-key.bin` 解出，熵 `ft-qwenmt-api-key`（与 DpapiSecretStore.cs 同构）。
- 7.66 秒中文测试音频（PowerShell SAPI 合成后转 16k）实测：**语音开始后 ~0.5s 出首个非空部分结果，部分结果节拍 0.5–1.0s；finish-task 后 ~0.30s 出终稿**（带标点、end_time=7040）。部分结果与终稿同 sentence_id、文本漂移会被改写——**部分结果绝不送 MT** 这条设计铁律被机证。
- **usage 在终稿 result-generated 帧上**（payload.usage，会话累计）：`duration=7`（7.66s 音频向下取整）、input_tokens=96、output_tokens=17；**task-finished 帧无 usage** → 服务端中继要跟踪会话内最后一个 usage 在 finally 里记账。1 秒全零静音也出空终稿但无 usage（纯静音不计费）。
- **不发 finish-task 的后果（决定性实验）**：服务端不发终稿、不关流，23 秒后 `task-failed code=CLIENT_ERROR message=request timeout after 23 seconds.`——finish-task 必发；同时得到无音频空闲超时 ≈23s（真实产品 loopback 恒有 100ms 块，不会触发；服务端中继需把该错误映射为可恢复）。
- task-failed 形状：`header.error_code`（ModelNotFound / CLIENT_ERROR）+ `header.error_message`。

### 探针三：Caddy wss 透传（publish/probe-wss，本机等价验证）

- 生产腿被基础设施挡住：ECS 22 端口对开发机不通（BatchMode 超时；ping 19ms 通），`ftranslation.cn` 尚无 A 记录——与 deploy/Caddyfile 注释一致，**ICP 备案下来前域名不生效**。生产域验证属部署阶段事项，等备案后重跑同一探针即可。
- 本机等价床：Caddy v2.10.2（`publish/caddy/ext/`）+ python stdlib RFC6455 echo + C# 探针。结果：**明文 ws 与 TLS internal 的 wss 各 50×1KB 二进制帧逐位一致、10 秒心跳全通；连接存活中执行 `caddy reload`，10 个心跳全部无恙（state=Open）**——Caddy 优雅 reload 不掐活 WS，客户端重连逻辑只需处理真实网络断。
- 探针自身教训两条：`buf.AsSpan(1).SequenceEqual(payload.AsSpan(1))` 在 buf 是 64KB 收包缓冲时**长度不等恒 false**（要按 `AsSpan(1, n-1)` 截窗）；C# 插值字符串里嵌三元 `?:` 会 CS8361（`:` 被当格式分隔符，要加括号）。

### 对 Phase V 设计的修订

1. 服务端中继记账：以「最后一个 result-generated 的 payload.usage」为准（duration 秒），finally 落账；task-finished 无 usage。
2. 语音会话空闲超时按上游 23s 设计（客户端恒流 100ms 静音块不会触发；中继把 CLIENT_ERROR 映射为可恢复错误）。
3. 客户端字幕状态机确认「部分结果只更新行不投翻译队列、终稿替换」。
4. 生产 wss 验证（探针三同款）列入 1.5.0 发布前置检查单，与 ICP 备案同步。
## v76 语音 POC 结论与 WASAPI 回环采集（Phase P0，1.5.0）

日期：2026-11-01～11-02 · 目标：回答方案评估的三问，把「能不能做」变成「怎么做、多快、贵多少」。POC 不进版本，结论进仓库。

### P0 三问的实测答案

1. **设备级回环能否稳定出 16k PCM——能。** `WasapiLoopbackCapture` 在本机（Realtek + Nahimic）拿到混音格式 48kHz×2ch float32（blockAlign=8），事件驱动健康度 ~60 回调/秒（100ms 包节奏），5 秒采集 1167360 字节 ≈ 有声时长×384000B/s——**回环在端点空闲时不出包**（这一点决定了服务端 usage 与客户端字节统计都不能按墙钟算）。探针全 PASS（byte-count 1.002/tone-span/tone-energy/event-driven），16k 下混重采样经 python wave 验证时长 3.08s、RMS 0.2445、过零率 856/s（440Hz 理论 880/s）。
2. **DashScope ASR 直连语义——全部机证。** 模型 `qwen-audio-3.1-asr-flash-streaming`，`wss://dashscope.aliyuncs.com/api-ws/v1/inference`（普通域即可，不需要 workspace 前缀域）。run-task→task-started 0.40s；首句终稿延迟 = 语音发完（finish-task）后 **0.30s**；部分结果同 sentence_id、end=-1、文本会被改写，终稿整体替换——**部分结果绝不送翻译**由实测证实。1s 纯静音也出 final（空 text）+ task-finished，**无 usage**（纯静音不计费）。usage 只在终稿 result-generated 帧上（`payload.usage`，会话累计：`{"duration":7,"input_tokens":96,"output_tokens":17}`，音频秒向下取整）。**不发 finish-task 则服务端 23s 后 task-failed(CLIENT_ERROR,request timeout)**——finish-task 必发。task-failed 形状 = `header.error_code` + `header.error_message`，坏模型名 0.05s 内回包。
3. **Caddy wss 中继——本机等价全绿。** Caddy v2.10.2 反代：WS 升级透传、50×1KB 二进制逐位无损、TLS(tls internal) 终止、**优雅 reload 不掐活连接**（10 个心跳跨越 reload 全部无恙）。生产腿（ftranslation.cn）当前无 DNS 解析、22 端口超时——ICP 备案前域名不生效，Caddyfile 的 `/v1/*` 反代与 ACME 都动不了，留待备案后用同一探针重跑。

### 代码

- `src/FastTranslation.Platform.Windows/WasapiLoopbackCapture.cs`（约 300 行，零依赖）：共享模式 + LOOPBACK|EVENTCALLBACK 双 0 初始化、混音格式按**字节偏移**读（WAVEFORMATEXTENSIBLE 的 SubFormat 落偏移 24，.NET 结构体 Pack 复现不出嵌套对齐）、后台线程 WaitAny(200ms) 超时兜底（个别驱动静音期不发事件）、`Failed` 走事件不跨线程抛、`DeviceInvalidated` 事件交调用方重建。
- 探针 `publish/probe-wasapi/`、`publish/probe-asr/`、`publish/probe-wss/`（publish/ 不进仓库）：录 5 秒、直连 ASR、穿 Caddy。16k 重采样 WAV 经 python wave 人工复核。

### 踩坑

- `v76: COM QI 恒 E_NOINTERFACE——IMMDevice 的 IID 被我写成 IMMDeviceCollection 的（0BD7A1BE…）；真值 D666063F-1587-4E43-81F1-B948E807363F。教训：十六进制肉眼比对不可靠，一切 GUID 以 SDK 头文件（MIDL_INTERFACE）机验为准。`
- `v76: CS0030——COM coclass 壳类必须非 sealed，C# 的显式引用转换规则禁止 sealed 类转接口，封送层的 QI 转换依赖它。`
- `v76: CS9191——Marshal.QueryInterface 第二参签名是 in Guid，传 ref 在 TreatWarningsAsErrors 下编译失败。`
- 探针时序：`SoundPlayer.PlaySync` 启动延迟 ~0.9s，断言窗必须由实测首末块夹逼，不能按名义时轴切窗；回环空闲不出包 ⇒ 字节数下限按有声时长算。
## v77 Core 语音三件：重采样、字幕服务、传输协调器（1.5.0）

### AudioResampler（`src/FastTranslation.Core/Audio/AudioResampler.cs`）

定点相位重采样：相位累加器以 1/16000 输入帧为单位、每产一个输出 `+= sourceRate`，44.1k→16k 这类非整倍率长会话零漂移；线性插值 + 多声道均值下混 + `*32767` 截断写小端 PCM16。`Flush` 尾帧按最后一帧保持。5 测试：48k stereo→16k mono、分块与整段逐字节一致（余数跨 Process 保留）、44.1k、下混、Flush。

- `v77: 缓冲消费指针被按「下一个输出位置」推进，而插值只读过 frame/frame+1——48k 下每个输出多消费 1 个采样，长流式累积成 head>tail（Array.Copy length=-1）。修复：相位位置与消费指针分开记账，发射后 `_acc += _srcRate; _acc -= 16000*(frame+1); _head += (int)(frame+1)*_ch;`——frame+2 才是下一个输出的插值下界，不能丢。`
- `v77: 测试助手输出缓冲 4096B < 整段一次 Process 的 16000B 产出——整段被截断后与分块结果比对必然失败。缓冲按最坏产出分配。`

### SpeechTranscriptService（`src/FastTranslation.Core/Speech/SpeechTranscriptService.cs`）

终稿驱动：非终稿只更新行不进翻译；终稿进 500ms 防抖窗，到期把窗口内多句终稿按 `\n` 合并成**一次** MT 请求（qwen-mt 的固定 45 token 开销按批摊）；回填按 §5.2——句数对齐逐句回填、不齐整段回填第一句（不赌对齐）。有界队列容量 8 满则丢最旧批并计数；并发 SemaphoreSlim ≤2；**不复用 TranslationService**（它「新请求取消上一在途」的语义会打断并发批）。6 测试：部分结果从不进翻译、终稿不取消前句、并发上限、丢最旧、按行回填、句数不齐整段回填。

### ISpeechTransport + SpeechTranslationCoordinator（`src/FastTranslation.Core/Speech/`）

传输抽象只暴露 4 事件（ready/sentence/usage/error），WS 类型不进 Core。协调器状态机 Idle→Connecting→Streaming→Finishing→Failed：100ms 泵推音频（环形缓冲 2s 容量）；断线**重连一次**并从环形缓冲重放未确认音频（重连前 `_sentenceIdOffset += _maxSentenceId`，字幕主键跨会话单调）；error→Failed；StopAsync 只在 Connecting/Streaming 有效，幂等；DisposeAsync 内置 2s 超时。测试钩子 `DrainAudioForTestAsync()`。

- `v77: 定时器抖动——真 100ms 定时器与测试步骤竞争，注入 SilentTimeProvider（CreateTimer 返回空转 ITimer）后 8 连跑全绿。TimeProvider.CreateTimer 的签名机验自 ref 包源码，凭记忆写成泛型版两次 CS0115。`
- `v77: fire-and-forget 任务的一切断言必须轮询（WaitUntilAsync），`await Task.Yield()` 一次不保证后台批已跑完；`dotnet test --no-build` 在改完测试源码后会跑旧二进制，出现「修了还挂」的假信号。`

Core.Tests 301 例全绿。
## v78 服务端 /v1/asr 语音中继与语音独立额度（1.5.0）

### 形状

- `Gateway/SpeechEndpoints.cs`：`Map("/v1/asr")`。**升级前跑全链闸门**：未配置→503 `speech_disabled`；限流→429；语音日预算→429 `budget_exhausted`；账号禁用→403；额度不足→429 + `X-FT-Quota-Reason`（故意 429，客户端静默切自有 Key）；全过后非 WS 请求→400（这个 400 同时是客户端升级前探测的「绿灯」）。
- `Gateway/DashScopeSpeechGateway.cs`：双向泵。客户端首帧 start→`run-task`（`run_task_id` 全会话复用），二进制原样转发（服务端不解析音频），stop→`finish-task`；上游 task-started→`{"type":"ready"}`、result-generated→`{"type":"sentence",…}`（防御式解析：sentence_id/text 可空、sentence_end 判 final）、usage 帧透传、task-failed→`{"type":"error"}`。`Task.WhenAll` + `MaxSecondsPerSession=1800` 硬超时。
- **记账在 finally**：usage 只出现在终稿帧，task-finished 帧没有；客户端半路断开也要照记——`RecordUsage(source: "speech")`，额度字符 = `max(秒,10)×20`，成本按 ASR 真实 token 计微元。

### 配套改动

- `GatewayOptions.cs` 增 `SpeechOptions` 8 键（配置路径必须写全 `Server:Gateway:Speech:*`，少一层会静默绑不上）：Enabled/WsUrl/WorkspaceId/Model/QuotaCharsPerSecond=20/MinSeconds=10/MaxSecondsPerSession=1800/`DailyBudgetMicros=2_000_000`。**语音独立预算桶**：全站 5 元桶只够 ~5.8h 语音且烧完连文本翻译一起 429；2 元 ≈ 2.3h/日。
- `AccountStore.RecordUsage` 增 `source` 参数（替换 SQL 字面量 `'gateway'`），`ReadTodayCostMicros(string source)` 带过滤——预算桶按来源分账。
- `Pricing` 补 `qwen-audio-3.1-asr-flash-streaming` 6/4.5 元每百万 token（PerImage=0）——不入表会静默回落 flash 价少收钱。

### 测试（5 条）

未带 token 401 / 超额 429+`X-FT-Quota-Reason` / 未启用 503（DisabledSpeechFactory：`GatewayFactory` 硬编码 Enabled=true，子类覆写虚属性）/ 客户端断连仍记账（不走 HTTP：假上游 `connect` 注入 + 内存 WebSocket 对 + 真库）/ ASR 价不是 flash 回落。Server.Tests 198 全绿。

### 踩坑

- `v78: catch (WebSocketException) 挂 when (!timeoutCTS.IsCancellationRequested) 会把正常断连误报 500——requestAborted 取消会连带 linked CTS，守卫永远为假。守卫只用于真超时分支。`
- `v78: 嵌套 record struct 误写在命名空间层（CS1527/CS0051/CS0057）——移进类内。`
- `v78: SpeechGatewayFactory 手打锚点两处漂移（类名/虚属性名），python 断言拦住未写盘——改生成代码前先 grep 真锚点。`
## v79 客户端语音界面与额度降级（1.5.0）

### 界面

- 侧栏新增「语音」页（`Pages/SpeechPage.xaml`，Review 与 Account 之间）：开始卡（主按钮 + 一句隐私说明）、设置卡（目标语言下拉[首项手工补「自动（中英互译）」，语言表没有 auto 项]、显示原文开关、字幕条数 100/200/500）、快捷键卡（复用 `HotkeyGesture.TryParse`，失焦校验失败保留原值）、额度提示行（App 取数后经 `MainWindow.SetSpeechQuota` 转发，页面不碰账号）。设置项实时落盘（`SpeechHotkey/SpeechTargetLanguage/SpeechShowSource/SpeechMaxSentences`）。
- 字幕浮窗 `Speech/SpeechWindow.xaml`：720×320 无边框圆角 12、可激活（要拖动与点关闭，不上 NOACTIVATE）、`ContentRendered` 后再定位底部居中（SourceInitialized 时 ActualWidth=0 会错位）；头部状态点 + 计时 + 「已用 N 字符」；字幕流按 sentence_id 原地替换/追加 + 环形裁剪 + 自动滚底，非终稿 55% 透明度；底部「结束/清空」。`BooleanToVisibilityConverter` 必须在 XAML 资源里声明（ctor 里塞 Resources 晚于 InitializeComponent，绑定会拿不到）。
- 入口三件：托盘 id 1008「语音翻译」、热键 Ctrl+Alt+V（第三个 HotkeyService 实例——每实例自建 message-only 窗口，RegisterHotKey 按 (hwnd,id) 唯一，HotkeyId 可共用）、`FT_UITEST=speech / speech-window / speech-window-failed` 三形态 + `tools/qa/shot-speech.ps1`（PrintWindow PW_RENDERFULLCONTENT，浅/深 × 页面/浮窗/失败 共 8 张）。

### 会话组装（App.xaml.cs）

`RunSpeechTranslation`（在会话中→前置浮窗；未登录→气泡提醒）→ `StartSpeechSessionAsync`：**额度预检**（`AccountCoordinator.ReadUsageAsync`，剩余 <1 分钟直接气泡拒绝，免得窗口开了、会话建了才开始 429）→ 建窗 → GatewaySpeechTransport（Bearer + X-FT-Device，wss 由 https 基址推导）+ SpeechTranscriptService（翻译走文档链路的组合 provider，**绝不经 `_translation`**）+ SpeechTranslationCoordinator → `StartSpeechCapture`（WasapiLoopbackCapture → AudioResampler 48k float32→16k PCM16 → PushAudio）。usage 事件按服务端口径换算「已用 N 字符」（max(秒,10)×20）。`EndSpeechSession` 统一释放：采集 Stop/Dispose → 协调器 StopAsync → **fire-and-forget DisposeAsync（UI 线程同步等待会死锁）** → 浮窗 EndSession。

### 额度与降级

- **升级前探测**（GatewaySpeechTransport.ConnectAsync）：先发普通 GET `/v1/asr`，400=闸门全过的绿灯；503→「语音服务暂未开启」、403→「账号状态异常」、401→「登录已过期」、429+`X-FT-Quota-Reason`→「语音额度已用完…」、429 budget→「今日语音额度已用完…」、其余 429→「请求太频繁」。拒绝文案以 InvalidOperationException 抛出，协调器**原样上屏**（新增 Core 测试锁住这一语义，不能被「无法连接语音服务」盖掉）。
- **设备切换不结束会话**：`DeviceInvalidated` → 停旧采集 → 1.5s 后重建（音频引擎端点切换需要时间，立刻重开常拿不到新端点）；字幕、网络连接、计时全部保留，用户感知只是字幕停顿一下。
- **浮窗点 X = 结束会话**：不能让会话在看不见的窗口里继续烧额度。

### 验证

build 0 警告 0 错误；四项目 301/97/95/198 全绿；8 张截图（`assets/qa/speech-*`）；本机起 Release 版人工确认托盘菜单、热键注册、页面四态。

### 残留

端到端真声音（播视频→看字幕）需要真实 DashScope Key 在网关转发下验证，留到部署联调；额度预检依赖 `/api/v1/account/usage`，离线或后端 5xx 时提示「暂时读不到额度信息」并放弃本次会话。

## v80 许可证改为 AGPL-3.0（1.5.0）

### 为什么

MIT 允许别人把本项目（含自建网关）改成闭源后对外提供服务而不用回报源码。本项目的服务端会把翻译请求经网关对外提供网络服务，AGPL-3.0 第 13 条正好覆盖这个场景：谁改了它再拿去做网络服务，就要把对应源码提供给那些用户。自己用、或原样分发二进制不受额外影响。

### 改了什么

- `LICENSE`：整份替换为 GNU Affero General Public License v3.0 官方原文（34,523 字节，无 BOM、LF 换行，未改一字）。
- `README.md` 许可段：MIT → AGPL-3.0，补一句第 13 条的通俗说明与版权行；项目结构里的迭代史范围同步到 v1–v80。
- `THIRD_PARTY_NOTICES.md`：开头补一行「本项目自身代码以 AGPL-3.0 发布」；第三方组件清单不变（依赖仍是 MIT / Apache-2.0，与 AGPL 兼容）。
- 历史设计文档加注，避免与现行 LICENSE 冲突：`docs/superpowers/specs/2026-10-22-fast-translation-design.md` 头部加「2026-11-02 起改为 AGPL-3.0，下文 MIT 为当时记录」；`docs/superpowers/plans/2026-10-23-fast-translation-v1.md` 头部加英文 License note；`docs/ZCODE-HANDOFF.md` 的「MIT 自有」改为「AGPL-3.0，此前为 MIT」。
- `docs/IMPL.md:349` 与 `docs/portable-readme.txt:190` 的 MIT 字样是按版本累加的历史记录，保留不动。

### 验证

- 本次只改文档与许可证，无代码改动，构建产物不受影响（未重跑 build/test，改动不触及 .cs / .xaml / csproj）。
- 提交后由 GitHub 的 license 检测确认仓库许可证识别为 AGPL-3.0（`GET /repos/LYW206817/FTranslation` 的 `license` 字段）。

### 残留风险

- AGPL 与「闭源分发 / 部分应用商店条款」不兼容：以后若要上架需单独授权或双许可。
- 历史提交里的 `LICENSE` 仍是 MIT 原文——历史版本按 MIT 授权是事实，不改写历史；从本提交起新版本按 AGPL-3.0。
- 第三方 GPL 项目仍只是「学思路」；将来若复制其代码，AGPL 与 GPL 的兼容性需逐案确认。

## v81 备案上线一键脚本 go-live.sh，并修掉升级会覆盖 Caddyfile 的隐患（1.5.0）

### 为什么这样改

备案号下来那一刻要连做六件事：云解析加记录、替换 `Caddyfile` 占位符、填页脚备案号、`caddy validate`、`systemctl enable --now caddy`、逐项验收。手工做最容易漏两处：**页脚备案号**（境内站点必须展示，漏了是合规问题）和**域名是否真的解析到这台机器**（指错了会先怀疑证书，白查半小时）。所以收成一条命令，带只读的 `--dry-run`、五项断言和回滚命令。

顺带修掉一个真缺陷：`install.sh` 原来无条件 `install -m 0644 "$HERE/Caddyfile" /etc/caddy/Caddyfile`，而上传目录里的模板永远带占位符 —— 也就是说**上线后任意一次升级都会把配好的 Caddyfile 换回占位符，接着那段占位符检测把 caddy 停掉，网站直接下线**。现在检测到已配好的 Caddyfile 就保留（要重铺模板用 `FT_FORCE_CADDYFILE=1`）。

### 脚本

`deploy/go-live.sh`（218 行，LF，`set -euo pipefail`）：

- 参数：`--domain`（默认 ftranslation.cn）、`--email`、`--icp`、`--expect-ip`、`--skip-icp`、`--dry-run`
- 前置体检（任一不过直接退出，不改文件）：`getent ahostsv4` 确认解析、`--expect-ip` 比对、`ss` 确认 80/443 没被非 caddy 进程占着、占位符现状
- 变更：`cp -a` 留 `.bak` → `sed` 两处占位符 → 页脚 `__ICP_NUMBER__` → `caddy validate` → `enable --now`（已在跑则 `reload-or-restart`）
- 等待：轮询 `https://<域名>/health` 至多 90 秒（ACME HTTP-01 首次签发要几十秒）
- 断言五项：80 回 301/308；`/health` 含 `"ok"`；证书 `subject`/`enddate`（有 openssl 时）；页面不含 `__ICP_NUMBER__` 且含 `beian.miit.gov.cn`；页面含传入的备案号
- 收尾：打印 `systemctl is-enabled/status caddy`，失败时打印一行回滚命令

### 验证

- `bash -n deploy/go-live.sh` 与 `bash -n deploy/install.sh` 均通过；两个文件确认 LF（无 CR）。
- **生产机上的 `--dry-run` 没跑成**：本机当前出口 IP 是 `223.104.121.58`（武汉移动 4G），而安全组 22 只放行 `59.173.184.134/32`，`ssh root@114.55.166.224` 直接 `Connection timed out`，`python socket.connect` 探测 22 同样超时。脚本的真机行为等能 ssh 时补验（这是本次交付的已知不足，不写成已验证）。
- 因此本次没有新增/修改任何 C# 代码，`dotnet build` 与 573 项测试的结论沿用上一版（未受脚本改动影响）。

### 残留风险

1. `--icp` 是自由文本，直接进 `sed s/__ICP_NUMBER__/<值>/`：值里若含 `/` 会让 sed 报错（备案号字符集是汉字+数字+`号-`，正常不会触发；真要含就在脚本里换分隔符）。
2. `go-live.sh` 只改服务器上的 `/var/www/fasttranslation/index.html`，不改仓库里的 `site/index.html`：下次 `install.sh` 整目录同步站点会把页脚覆盖回占位符。脚本收尾已经打印这条提醒，但仍需人工提交一次仓库副本。
3. DNS 传播有 TTL，`--expect-ip` 可能在记录刚加时失败（脚本报「wrong record or stale cache」），等 10 分钟重跑即可，不是脚本缺陷。
4. 脚本假设 Caddy 用默认的 `/etc/caddy/Caddyfile` 与静态根 `/var/www/fasttranslation`（与 `install.sh` 的两个变量一致）；换路径要同步改两处常量。
5. 备案仍在管局审核：2026-10-29 12:48 提交、订单 2038868333211，页面显示 1~4 步（提交审核 / 阿里云初审 / 待提交管局 / 工信部短信核验）已全部完成，当前停在第 5 步「管局审核」，预估 10 个工作日、法定最长 20 个工作日。预计出号窗口 11-12 前后（Workday 计数从 10-30 起算：7 个工作日 ≈ 11-09，10 个 ≈ 11-12，20 个 ≈ 11-26）。
## v81 语音额度可突破与识别语种错位（1.5.0 修复）

### 为什么

代码审查（2026-11-02）发现两个 P0：

1. **额度可被单会话顶穿十几倍**。`SpeechEndpoints` 只按「最小会话」(MinSeconds×QuotaCharsPerSecond=200 字符) 做额度预检，而会话硬顶是 1800 秒、记账只在 `DashScopeSpeechGateway` 的 finally 里一次性记 36,000 字符。Free 日额度 2,000 → 一场会话就超日上限 18 倍。限流器只数每分钟请求数（40/min），没有并发会话上限，同一账号可以同时开几十条各自按 200 预检的会话；`ReadTodayCostMicros("speech")` 又只在会话开始读、结束才写，并发时集体读到 0。三道闸全部形同虚设。
2. **识别语种提示方向错**。客户端把「译成哪种语言」当作 `language` 发给服务端，服务端原样塞进 DashScope 的 `language_hints`。选「译成中文」时，英文音频被硬按中文识别——字幕从第一句就开始错。

### 服务端改了什么

- 新增 `Domain/SpeechQuota.cs`：纯函数 `SessionSeconds(remainingChars, charsPerSecond, minSeconds, maxSeconds)` = `max(min, min(剩余可讲秒数, 硬顶))`。额度算错直接等于烧钱，所以按 `QuotaPolicy` 的做法抽成纯函数 + 单测。
- 新增 `Gateway/SpeechSessionLimiter.cs`：按账号的并发会话计数（`TryEnter`/`Exit`，`ConcurrentDictionary` + CAS 循环，`max≤0` 不限制）。`SpeechOptions.MaxConcurrentSessionsPerUser` 默认 1；`Program.cs` 注册单例。
- `SpeechEndpoints`：额度决定后先算 `remaining = min(RemainingMonthly, RemainingDaily ?? int.MaxValue)`，再算 `sessionSeconds = SpeechQuota.SessionSeconds(...)`；`TryEnter` 失败 → 429 `speech_busy`；「非 WS→400 / Accept / RelayAsync」包进 `try/finally { sessions.Exit(userId) }`，把 `sessionSeconds` 传进中继。
- `DashScopeSpeechGateway`：
  - `RelayAsync` 多收一个 `sessionSeconds`，硬顶用它（不再直接用 `MaxSecondsPerSession`）。
  - 记账从「finally 一次性」改成 `Bill(...)` 局部函数：每收到一帧 `result-generated` 就按**增量**（deltaSeconds / deltaTokensIn / deltaTokensOut）插一行 `usage_events(source='speech')`；finally 只补足 `MinSeconds` 的差额。日预算与额度闸门因此看到的是实时花费，客户端半路跑掉也不会丢账。
  - 会话硬顶到期时给客户端发一条 `{"type":"error","code":"session_timeout",...}`，再关流——否则用户只看到「连接中断」，会以为是网络问题。
  - **关流收口到 `RelayAsync` 的 finally**：上游泵不再自己 `CloseAsync`。原先两处都在关，错误帧和关闭抢时序，实测时好时坏（新增测试在并行负载下就会收到 Close 而不是错误帧）。

### 客户端改了什么

- `GatewaySpeechTransport` 的 start 帧只报 `sampleRate`，不再带 `language`；`SpeechSessionOptions` 删掉 `TargetLanguage`（识别语种必须由 ASR 自己判断，译成哪种语言由翻译回调决定），四处构造点同步收窄。
- 升级前探测复用应用唯一的 `_gatewayHttp`（原来每次会话 `new HttpClient`），并用 10 秒的 linked CTS 限时——共享客户端的 45 秒超时不能拖着开始会话。
- 采集停止移出 UI 线程：`Stop()` 内部 `Join` 采集线程最多 2 秒，在 UI 线程上同步等会把窗口冻住（结束会话、切换设备两条路径都改）。
- `AudioResampler.MaxOutputBytes(inputSamples)`：按「输出帧数×2+余量」算精确上限，替换 `floats.Length/2*6+64` 这种 9 倍拍脑袋分配（48k 立体声 100ms 块只需 3.2KB，原来每块分配 28.8KB，一秒约 300KB gen0 垃圾）。

### 测试

- 新增 `SpeechQuotaTests`（折算边界 + 并发闸门 3 条）、`Asr_returns_429_when_the_account_already_has_a_session`、`Session_timeout_tells_the_client_why_it_stopped`、`MaxOutputBytes_matches_what_a_block_actually_produces`。
- 断言 `run-task` **不再**含 `language_hints`（锁住 P0-2）。
- 顺手修了一个测试隔离坑：两个语音中继测试的 `SqliteConnection.ClearAllPools()` 会清掉**整个进程**的连接池，并行跑的其他测试打到失效连接上，表现为 `AccountProfileTests` 偶发登录 500。去掉后 Server 套件连跑 3 次稳定。
- 全量：`dotnet test` **702 通过 0 失败**（Core 302 / Platform.Windows 97 / Providers.QwenMt 95 / Server 208），连跑 2 次一致；Release build 0 警告 0 错误。

### 残留

- **重采样没有抗混叠低通**（设计 §5.1 写的是 3 抽头滑动平均，实现是直接线性插值）。48k→16k 会有一部分高频折回，可能让 ASR 把气声、齿音听错。这段改动会改变输出样本值，需要拿真实音频 A/B 才敢定，留到端到端真声联调一起做。
- `SpeechTranscriptService.DroppedCount`（队列满丢批次数）仍未上屏，用户看不出「字幕丢了几句」。属于体验项，等真声联调后再定是否显示。
- 真声端到端（播视频→看字幕）、`target_lang="auto"` 链路、生产 wss 腿仍待联调/备案。
## v82 语音链路剩余隐患收口：墙钟计费、跨帧 JSON、残留样本与后台任务异常（1.5.0 修复）

用户问「隐患都修复了吗?」之后逐条核对 v81 的遗留项,本版修掉 6 处,另 1 项明确不做并给出理由。

### 改了什么

1. **上游一条 usage 都没报时按墙钟计费**（`server/FastTranslation.Server/Gateway/DashScopeSpeechGateway.cs`）。
   之前只有 `latestUsage is { } usage` 一个分支:连接刚建就被砍、或全程静音时一帧 usage 都收不到,于是一分钱不记——
   预检已经放行、上游 ASR 算力已经花掉,这是可以白嫖的口子。现在 else 分支按
   `DateTimeOffset.UtcNow - startedAtUtc` 取秒数(上限 `sessionSeconds`)、不足 `MinSeconds` 按 `MinSeconds` 记。
2. **上游 JSON 跨帧拼接**（同上,`PumpUpstreamAsync`）。`ReceiveAsync` 只读一帧,`received.EndOfMessage=false` 时
   旧实现把半截 JSON 直接丢给 `JsonDocument.Parse`;而 DashScope 的长句终稿带 `words[]`、单帧可达数十 KB,
   一旦分帧就是「静默丢句」。现在用 `MemoryStream` 累积到 `EndOfMessage` 再解析。
3. **收尾补发重采样器残留样本**（`src/FastTranslation.App/App.xaml.cs` 的 `EndSpeechSession`）。
   `AudioResampler` 内部压着不足一块的样本(最多 100ms),之前从不调用 `Flush`,每段话的尾巴都被吞掉。
   现在结束前 `Flush` 一次并 `PushAudio`。
4. **后台任务异常不再静默**（同文件新增 `Observe(Task, string)`）。语音链路有 4 处 fire-and-forget
   (`StartSpeechSessionAsync`/`RestartSpeechCaptureAsync`/`StopAsync`/`DisposeAsync`),以前任一抛出都无人观察,
   用户只看到窗口一直转圈。现在统一 `ContinueWith(OnlyOnFaulted)` 回 UI 线程显示原因;`Task.Run` 里的
   `capture.Stop()/Dispose()` 也补了 `ObjectDisposedException/InvalidOperationException` 兜底。
5. **被丢弃的句子要说出来**（`SpeechWindow.EndSession(string? error, int dropped)`）。
   `SpeechTranscriptService.DroppedCount` 之前只存在不上屏,队列满丢句时用户只看到字幕缺词、以为模型漏译;
   现在结束态显示「已结束(N 句因处理不过来被丢弃)」。
6. **浮窗不再抢焦点**（`ShowSpeechWindow` 去掉 `Activate()`）。开始语音时用户多半正在打字,抢焦点会打断输入;
   窗口置顶 + `Show()` 已足够可见,要拖动或点关闭时用户自己会点它。

### 明确不修的一项

**重采样仍无抗混叠低通**(`src/FastTranslation.Core/Audio/AudioResampler.cs:94` 直接线性插值,设计 §5.1 曾提 3 抽头滑动平均)。
不做原因:48k→16k 是 3:1 抽取,理论上 8 kHz 以上会折回;但收益只能靠真机真声 A/B(看 ASR 是否正确率更高),
当前没有可用额度做端到端真声验证,先加一版无法证明收益的滤波只是增加回归面。已列入残留风险,端到端联调时一并验。

### 验证

- `dotnet build FastTranslation.slnx -c Release` → 0 警告 0 错误(**构建前必须先停应用与本地服务**,否则
  MSB3026/MSB3021 锁文件)。
- `dotnet test` → **704 通过 0 失败**(Core 302 / Platform.Windows 97 / Providers.QwenMt 95 / Server 210);
  Server 由 208 增至 210,正是本版新增的 `Bills_wall_clock_when_upstream_never_reports_usage` 与
  `Assembles_json_split_across_text_frames`。
- 无界面布局改动,截图沿用 v79(`SpeechWindow` 只改了结束态文案)。


## v83 · 设置页新增强调色：电光蓝 / 湖蓝 / 雾光紫（1.5.0）

### 为什么

用户要「电光 / 湖蓝 / 雾光紫」三种色号都能在设置里选（原话见 m05854）。此前主题只有浅色/深色两档，
品牌蓝一族直接写死在 `ThemePalette` 的表里，想换色相只能整站重排 —— 三版 HTML 设计稿（`publish/ui-redesign/`，
不进仓库）做出来才看清：用户真正要的不是新布局，而是**把色相抽成独立思考的第二维**。

### 怎么做的

- **强调色与明暗正交**：`AccentColors`（`src/FastTranslation.Platform.Windows/ThemePalette.cs`）只装品牌色一族九项
  （Brand / BrandHover / BrandPressed / BrandFill / BrandFillHover / BrandText / OnBrandText / BoltFrom / BoltTo），
  `Compose` 用 record `with` 把这九项盖到所属明暗主题上。页底、发丝线、正文、四档自评色一个不动 —— 所以三支切换
  只是换色号，布局与间距零风险。
- **电光蓝不单独建表**：基底 `Light` / `Dark` 本身就是电光蓝，`Use(Light, "electro")` 直接返回同一个实例
  （`UseAppliesTheAccentButLeavesTheRestOfThePaletteAlone` 用 `Assert.Same` 盯住这一点）。
- **品牌蓝基线顺带调色相**：浅色 `#2562FF` → `#3B6BF0`（偏 azure），闪电渐变 `#4B7BFF → #2450E8`。
  原因：#4B7BFF 压白只有 3.77:1 不够 AA，所以它只进闪电渐变与色块；实底按钮用 `#3B6BF0`（白字 4.62:1，旧值 4.92:1）。
- **落盘时机与主题一致**：设置页点保存写 `Settings.Accent`（`src/FastTranslation.Core/Settings.cs`，缺字段默认 `"electro"`，
  无需迁移），`App.OnStartup` 里 `ThemeBridge.Apply(ThemePalette.Resolve(_settings.Theme), _settings.Accent)` 生效。
  属性页提示语同步改成「主题、强调色与热键均在下次启动后生效」（**v84 起改成选完立即生效**，见下节：
  画刷本来就是可变实例，就地改色并不需要重建画刷树，当初「不值得」的判断是错的）。

### 界面

- `src/FastTranslation.App/Pages/SettingsPage.xaml` 外观卡新增「强调色」一行三枚 chip（`AccentChip` / `AccentChipDot` 两支样式，
  12×12 圆点 + 文字）。三枚色块**永远显示该色在当明暗主题下的真实品牌色**，不是固定色号。
- 选中态改 `BorderBrush=BrandBrush` + `Background=BrandFillBrush` + `FontWeight=SemiBold`，未选中 `BorderStrongBrush` + `SurfaceBrush`。

### 验证

- `dotnet build FastTranslation.slnx -c Release` → 0 警告 0 错误。
- `dotnet test FastTranslation.slnx -c Release` → **714 通过 0 失败**（Platform.Windows 107 / Core 302 / QwenMt 95 / Server 210）；
  两条 AA 对比度测试改成遍历三支强调色（`OnBrandTextIsReadableOnTheBrandFill`、`BrandTextIsReadableOnSurfaceAndOnBrandFill`），
  另加 `EveryAccentHasItsOwnBrand`、`ResolveAccent_FallsBackToElectro`、`UseAppliesTheAccentButLeavesTheRestOfThePaletteAlone`。
- 像素证据（新脚本 `tools/qa/shot-accent.ps1` + `tools/qa/count-color.ps1`，`PrintWindow(hwnd, dc, 2)` 实拍 1820×1155）：
  主窗在湖蓝/紫罗兰下的精确计数 —— `accent-main-light-lake.png` `#0E7EA8` 8229 px、`#3B6BF0` 0 px；
  `accent-main-light-violet.png` `#5B36F0` 8741 px、`#3B6BF0` 0 px；`accent-main-dark-lake.png` `#4FC3F0` 8229 px、`#4A82FF` 0 px；
  `accent-main-dark-violet.png` `#9B7DFF` 8230 px、`#4A82FF` 0 px。
  设置页三枚色块同帧出现三种色：`accent-set-light-electro.png` 里 `#0E7EA8` 189 px @(564,504)、`#5B36F0` 189 px @(694,504)；
  `accent-set-dark-violet.png` 里 `#4A82FF` 189 px @(412,504)、`#4FC3F0` 189 px @(564,504)。

### 残留风险

- **默认外观确实变了**：浅色品牌蓝从 `#2562FF` 改成 `#3B6BF0`，v69/v70 的截图不能再当像素基线。这是有意为之（旧钴蓝与湖蓝、
  紫罗兰放在一排时色相区分不够），但它不是「纯新增」变更，回归对比时要留意。
- 只对 Brand/OnBrandText 与 BrandText/Surface+BrandFill 两组做了 AA 断言；深色湖蓝的 `#123441` 填充、紫罗兰 `#2A2447` 填充上的
  其它正文色没有逐项跑对比度，仍是人工目测。
- 选中态靠「品牌色描边 + 品牌填充底 + 半粗字」三重区分，深色下三枚色块本身也都是品牌色，低视力用户未必分得清选中与未选中；
  若后续有反馈，加一枚勾选图标比继续加颜色更有效。

## v84 · 主题与强调色选完即生效，不再要求重启

### 为什么

用户看完 v83 直接问「怎么还重启生效呢？不能保存生效吗」。原来那句「下次启动生效」是从 v24 沿用下来的
（`docs/IMPL.md:351` 当时判断实时换肤要把 94 处 `StaticResource` 全改 `DynamicResource` + 调色板可变 + 窗口重建）。
但这两件事其实早就做完了：v24 起 XAML 里每处画刷引用都是 `{DynamicResource …}`，且画刷带 `po:Freeze="False"`，
`ThemeBridge.Set` 一直是**就地**改 `SolidColorBrush.Color`（`src/FastTranslation.App/ThemeBridge.cs:64-81`）。
也就是说缺的只有一次调用——画刷树不用重建，标题栏要单独再推一次 DWM。

### 怎么做的

- 新增 `App.ApplyAppearance(string? themeSetting, string? accent)`（`src/FastTranslation.App/App.xaml.cs`）：
  `ThemeBridge.Apply(ThemePalette.Resolve(setting), accent)` 之后遍历 `Application.Current.Windows`，
  对每个窗口补一次 `WindowChrome.SetDarkTitleBar(..., ThemePalette.Current == AppTheme.Dark)`。
  参数收「设置值」而不是解析结果，好让调用方原样传 `_settings.Theme`。
  启动路径（`App.OnStartup`）也改走这一个方法，于是「启动」与「运行时切换」只有一条代码路径。
- 设置页改成**选完即预览**（`src/FastTranslation.App/Pages/SettingsPage.xaml.cs`）：
  `ThemeCombo.SelectionChanged` 与 `AccentChip_Click` 都调 `PreviewAppearance()`；落盘仍然只发生在保存按钮里。
  `SelectedThemeSetting` 一个属性同时给预览与保存用，避免两处 switch 走岔。
- **预览要能退回去**：页面是缓存的（切页面不重建），所以 `Unloaded` 里 `RevertAppearance()` 把外观退回 `settings.json` 那一套；
  「取消」也走同一条（`ReloadFromSettings` 改下拉框会再预览一次，最后仍以保存值为准）。不这样做的话，看了预览又没保存，
  界面就会和设置文件长期不一致，下次启动「自己变回去」看起来像 bug。
- 提示语改成「主题与强调色选完立即生效；热键在下次启动后生效」——热键是注册表/`RegisterHotKey` 状态，确实只能重启才省事。
- QA 钩子 `FT_UITEST=live-dark`：浅色启动后 2.6 秒在**同一进程**里调 `ApplyAppearance(Dark, Electro)`，
  用来证明不重启也能换主题与原生标题栏。

### 验证

- `dotnet build FastTranslation.slnx -c Release` → 0 警告 0 错误；`dotnet test` → **714 通过 0 失败**（无新增单测：
  这条链路是 WPF 画刷与 DWM 的运行时行为，xunit 里没有消息循环可用，证据只能是像素）。
- 强调色实时切换（`tools/qa/shot-live-switch.ps1`，`PrintWindow` 实拍 1820×1155，同一进程三帧）：
  - `live-1-before.png`：`#3B6BF0`（电光）8423 px，`#0E7EA8`（湖蓝）只有 189 px —— 那是色块本身。
  - 点「湖蓝」色块后 `live-2-preview-lake.png`：`#0E7EA8` 8335 px，`#3B6BF0` 缩到 189 px（只剩电光色块）。
  - 未保存直接切回首页 `live-3-reverted.png`：`#3B6BF0` 8229 px、`#0E7EA8` **0** —— 预览已退回保存值。
- 主题实时切换（`tools/qa/shot-live-theme.ps1` + `FT_UITEST=live-dark`，浅色启动、同进程切深色）：
  `publish/live/live-4-dark.png` 里 `#F7F9FC`（浅色页底）**0** px、`#3B6BF0`（浅色品牌）**0** px、
  `#4A82FF`（深色品牌）8229 px；`#F3F3F3`（系统原生浅色标题栏）也是 **0** px ⇒ 客户区与标题栏一起换了。

### 残留风险

- 预览只覆盖主题与强调色。同一张卡里的目标语言、模型、热键、开机自启仍是「保存才动」，读起来是一致的（提示语写明了热键）。
- `Unloaded` 里的回退依赖页面被移出可视树。若将来把设置做成独立窗口或用 `Visibility` 隐藏，这条回退就失效，得改成显式的 `Closed`。
- 切换时没有过渡动画：整屏颜色瞬间跳变。要顺滑得给窗口做交叉淡入，代价是一次全屏重绘，暂不做。

## v85 · 雾光布局真正落进 WPF：浮卡侧栏、三段渐变底、双卡片主区（1.5.0）

v83/v84 只换了颜色，用户一眼看穿：「你这和图中严重不符啊，你只改了个颜色，样式一点没变」（m06129）。
这一版把 `publish/ui-redesign/v3b-haze-blue.html` 那版设计稿的**结构**搬进 WPF：渐变底 + 两团光、玻璃浮卡侧栏、线性图标、白色药丸、上下两张卡片、渐变主按钮。

**设计令牌（`src/FastTranslation.Platform.Windows/ThemePalette.cs`）**：新增六支，全部带默认值并追加在 record 末尾，`Compose` 的 `with` 表达式不受影响。

- `PageEnd`：三段渐变的第三站，浅 `#FFF4EF` / 深 `#1D1712`。155° 渐变收在一点暖色上，避免整片冷蓝读起来像办公软件。
- `Glass` / `GlassEdge`：侧栏浮卡的半透明白（浅 `#BDFFFFFF` / 深 `#B31B2029`）与 1px 描边（浅 `#FFFFFF` / 深 `#1FFFFFFF`）。卡必须透出底下的雾色，否则和页面底色之间只剩一圈阴影在分区。
- `GlowBrand` / `GlowWarm`：右上冷光、左下暖光的径向起点（浅 `#382F62FF` / `#26FF7A59`，深 `#3A4A82FF` / `#1FFF7A59`）。
- `Warm`：光晕那支暖色的实色版（浅 `#FF7A59` / 深 `#FF8F73`），顶栏模型药丸的圆点用它——直接拿 `GlowWarm` 太淡，看不见。

**`src/FastTranslation.App/ThemeBridge.cs`** 相应新增 `PageGradientBrush`（`PageFrom` 0 → `PageTo` 0.58 → `PageEnd` 1，(0,0)→(1,1)）、`BrandGradientBrush`、`GlowBrandBrush` / `GlowWarmBrush`（径向，Center 与 GradientOrigin 都是 0.35,0.35，外圈同色 alpha 0）、`GlassBrush` / `GlassEdgeBrush` / `WarmBrush`，以及三支 `DropShadowEffect`：`CardShadowEffect`（模糊 34 / 纵深 9）、`GlassShadowEffect`（30 / 7）、`NavGlowEffect`（品牌色，18 / 6）。投影强度按主题分两档——深色底下黑影子本来就显眼，同样的 alpha 会糊成一团。

**`src/FastTranslation.App/App.xaml`**

- `Card`：圆角 16、去边框、`Effect=CardShadowEffect`、内边距 18,16,18,18。**改掉 v61 的判断**：当年去掉投影的理由是「浅底上一圈灰外发光会显脏」，雾光布局的底本身是冷蓝渐变，阴影色相跟着 `Shadow` 令牌走，落在蓝底上是加深而不是发灰。
- `SidebarItem`：高度 40 → **35**（见下方踩坑 2）；悬停是纯白底 + 2px 品牌色环，不再用 v60 的 2px 右移；`SidebarItemActive` 改成 `BrandGradientBrush` 实块 + `NavGlowEffect`。
- 新增七支共用样式：`NavIcon`（`Segoe Fluent Icons, Segoe MDL2 Assets`，15px，宽 26，不定 `Foreground` 以便继承活动项的白字）、`CardSurface`、`TopPill`、`PillDot`、`Kbd`、`ChipButton`、`FlatLinkButton`；`PrimaryButton` 底色改渐变、圆角 10/9、加 `NavGlowEffect`。

**`src/FastTranslation.App/MainWindow.xaml` 整份重写**：底层两个 `Ellipse`（`IsHitTestVisible=False`，否则会挡住底下所有按钮）→ `Grid Margin=14` 两列 200/* → 玻璃侧栏（品牌方块 + 十个图标项 + 分隔条 + 底部取词开关白卡与账号行）→ `PageHost`。十个导航按钮仍是 `SidebarItems` 的直接 `Button` 子项（`MainWindow.xaml.cs` 的 `SyncSidebar` 靠 `OfType<Button>()` 遍历），分隔条是 `Border` 所以不影响。图标用 MDL2 字形而非十个 `Path`：装饰性的十枚图标不值得各写一段矢量。

**`src/FastTranslation.App/Pages/MainPage.xaml` 整份重写**：顶栏两枚白药丸（语言下拉平铺进药丸、模型名 + 暖色圆点）、主区 `0.85* / 1.15*` 两张卡片（原文 / 译文，译文左侧保留渐变竖线）、`Ctrl` / `Enter` 两枚键帽 + 渐变「翻译」主按钮、译文下方两枚 chip 胶囊、页脚两枚纯文字动作。12 个被代码引用的 `x:Name` 全部保留；设计稿里的「朗读」「已收录」两枚胶囊**没有**实现——产品里没有这两个功能，不能假造。

**无障碍**：十个导航按钮补 `AutomationProperties.Name`。内容从纯文字换成 `Grid` 之后 UIA 的 `Name` 变空，读屏软件读不出来，QA 脚本也点不到。

**验证**

- `dotnet build FastTranslation.slnx -c Release` → 0 警告 0 错误。
- `dotnet test` → Core 302 / Platform.Windows 107 / QwenMt 95 / Server 210 = **714 全绿**。
- `tests/FastTranslation.Platform.Windows.Tests/ThemePaletteTests.cs`：令牌计数 21 → 29（六支新令牌）。
- `tests/FastTranslation.Platform.Windows.Tests/ThemeResourceKeysTests.cs`：注入键的正则从 `[A-Za-z0-9]+Brush` 放宽到 `(?:Brush|Effect)`——三支 `DropShadowEffect` 也只在 `ThemeBridge` 里出现，否则会被误报成「引用了没人定义的键」。
- 像素证据（`PrintWindow(hwnd, dc, 2)`，1820x1155，缩放 1.75）：浅色 `(60,60) #E6EEFE` = `PageFrom`、`(1790,1140) #FFF4EF` = `PageEnd`、`(217,200) #2F5DEC` = 活动项渐变、`(700,300) #FFFFFF` = 卡片白、`(200,900) #F7F8FB` = 玻璃侧栏；深色对应 `#1B2232` / `#1D1712` / `#4278EF` / `#1F242C` / `#191D26`；标题栏 `#202020`（深）/ `#F3F3F3`（浅）。
- 十个图标码 `E80F E8F1 E8FD E7C3 E72C E8A5 E720 E8C7 E77B E713` 用 `GlyphTypeface.CharacterToGlyphMap` 验过：`Segoe Fluent Icons` 与 `Segoe MDL2 Assets` 两套字体里全部存在，不会显示豆腐块。

**踩坑**

1. `tools/qa/shot-main-theme.ps1` 用 `CopyFromScreen` 抓窗口，窗口比桌面大时抓进来的是桌面本身——它给出的浅色图整片 `#FBFCFF`，据此会误判「渐变没生效」。新增 `tools/qa/shot85.ps1`，一律 `PrintWindow(hwnd, dc, 2)` + `SetProcessDPIAware()`。
2. 侧栏十项按 v48 的 40 高排下来刚好顶到底部取词开关，**第 10 项「设置」被切到窗口外**，UIA 点不到，`audit-light-settings.png` 和 `audit-light-main.png` 的像素差只有 0.2%（两页完全一样）。高度改 35 后差异 28.6%，页面真正切过去了。判据用像素差而不是「脚本没报错」。
3. XAML 注释收尾误写成 `*/`，导致后面整支 `FlatLinkButton` 被吞进注释，并因为注释里又出现 `<!--` 报 `MC3000：XML 注释不能包含 '--'`。报错行号指向的是**下一个**注释，不在真正出错的那一行。
4. `TextBox` 没有 `LineHeight` 属性（`MC3072`），行高只能靠 `TextBlock` 的 `LineHeight` 或字号本身。

**残留风险**

- 其余七页（单词 / 短语 / 语句 / 复习 / 文档 / 语音 / 套餐 / 账号 / 设置）没有逐页重排，只是通过 `Card` 与页面透明底拿到了新的圆角、投影与雾色背景，各自仍用旧的 24px 页边距。层次是一致的，但和主页面比密度略松。
- 侧栏图标依赖 MDL2 字形。日后若换字体或换码，先在 `GlyphTypeface.CharacterToGlyphMap` 里验过再提交。
## v86 · 侧栏换页不再卡：去掉换样式重画与过场，九页提前构造；左上角换成真 logo（1.5.0）

用户的原话是「点击应用内侧栏卡顿」，不是翻译往返慢（他后来专门纠正过）。所以这一节只处理一件事：从点下侧栏到看见新页面之间那几帧。

**三处开销，都在 `src/FastTranslation.App/MainWindow.xaml.cs`**

1. `SyncSidebar()` 每次换页遍历十个按钮重新赋 `Style`。换 Style 等于让 WPF 丢掉旧模板、按新样式重建一整棵 `ControlTemplate` 树——十项重画十次，其中九项根本没变。
2. `PlayPageTransition()` 给 `PageHost` 套 170ms 淡入 + 210ms 上移 10px。页面自身还没排完版就先进入动画，观感上就是「点了之后等一下才动」。
3. 首次访问某一页要现场构造控件树：设置页 38ms、账号页 50ms，之后的第一帧还要 102–185ms。

另外证伪了一条猜测：`CardShadowEffect`（每张卡一只 `DropShadowEffect`）不是瓶颈。给 `ThemeBridge.cs` 临时加环境变量开关跑对照，帧时几乎不变（Word 21 vs 20、Plan 33 vs 27、Settings 33 vs 26、Account 38 vs 35），实验代码已原样撤回。

**改法**

- 新增 `src/FastTranslation.App/SidebarState.cs`：一个附加属性 `IsActive`。它的类注释写明为什么不能用 Style 表达活动态。
- `src/FastTranslation.App/App.xaml`：整支 `SidebarItemActive` 样式删除，活动态并进 `SidebarItem` 模板的一支 `DataTrigger`（绑定 `(local:SidebarState.IsActive)`），四个 Setter 复刻 v85 视觉——`ring` 透明、`fill` 用 `BrandGradientBrush` 加 `NavGlowEffect`、文字 `OnBrandTextBrush` + SemiBold。位置在 `IsMouseOver` 之后，活动块上不叠白色悬停底。
- `SyncSidebar()` 改成 `SidebarState.SetIsActive(button, active)`；`PlayPageTransition()` 与其调用、`using System.Windows.Media.Animation` 一并删除。
- 构造函数末尾 `Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, PrebuildPages)`，`PrebuildPages()` 把还没访问过的页 `Build` 出来各 `Measure`/`Arrange` 一遍。不塞进 `PageHost`——塞进去会真的画出来，用户会看到页面自己翻。尺寸还是 0 时重排一次，否则队列停下就再也不预热了。
- `src/FastTranslation.App/Pages/DocumentPage.xaml.cs`：构造函数里的 `Reload()` 删掉。宿主每次切页都会 `Reload`（包含第一次），留着这一行等于启动预热时就去问一次额度。

**数据**（`FT_UITEST=nav`，`call` = `ShowPage` 本身，`frame` = 其后第一帧，单位 ms）

- 基线 → 现在，首次访问：单词 45/118 → 19–36/92–112；短语 15/61 → 7/36；语句 13/50 → 7/34；复习 24/69 → 8/41；文档 29/78 → 9/41；语音 30/93 → 9/41；套餐 13/115 → 8/59；账号 50/155 → 5–6/70–72；设置 38/185 → 8–10/91–100。
- 再次访问：全部落在 4–17 / 15–43 一档，且不再有 170+210ms 的过场。
- 抖动约 ±30%（同一版本连跑三次同一项可以差 20ms），所以上表只当量级看。

**左上角换成真 logo**

- `src/FastTranslation.App/FastTranslation.App.csproj` 增加 `<Resource Include="..\..\assets\icon\icon-256.png"><Link>logo.png</Link></Resource>`；256 那一档在 200% 缩放下仍是满分辨率。
- `src/FastTranslation.App/MainWindow.xaml` 品牌区从「渐变方块 + 13px 的『译』字」换成 26x26 的 `Image`（`RenderOptions.BitmapScalingMode="HighQuality"`），右边保留 `Fast Translation` 字样。
- 像素证据（`PrintWindow` 实拍 1820x1155，深色）：奶油色 `#FAF3ED` 出现在 x 66–104、y 110–154，高 45px ≈ 26 × 1.75 DPI；同一行右侧是 `#E8ECF3` 的文字。

**视觉回归**

- 活动项（深色实拍）：填充带 y 187–247，61px ≈ 35 × 1.75，颜色 `#3E74E8`…`#4981FD` 逐行渐变，说明 `BrandGradientBrush` + 辉光仍在；浅色是 `#2A57EA`…`#3A6AF0`。
- 悬停（鼠标停在「单词」，UIA rect 406,471 311x61、窗口在 350,221，折到图内 y 250–311）：顶边 y 248–254 有 `#4A82FF` ×555 的 2px 环，块内是 `#1F242C`（Surface）+ `#E8ECF3` 文字，`SurfaceBrush` 悬停底没变。

**验证**

- `dotnet build FastTranslation.slnx -c Release` → 0 警告 0 错误。
- `dotnet test FastTranslation.slnx -c Release` → Core 302 / Platform.Windows 107 / QwenMt 95 / Server 210 = 714 全绿。

**残留风险**

- 启动后会把其余九页一起构造，内存里多驻留九棵树（原本点一遍也都会驻留），`DocumentPage` 的 `RestoreLastFolder()` 也随之提前到启动时读一次设置。
- 预热只做 `Measure`/`Arrange`，不做光栅化。首次显示最重的两页（设置 91–100ms、单词 92–112ms）仍有一帧级停顿，那是页面自身排版密度，留待逐页重排时一起收。
- 悬停环与焦点环共用 `ring`（v70 起就如此），Tab 聚焦与鼠标悬停同时成立时只显示一层。
## v87 · 全屏不再只剩中间一列：五个页面按宽度自动分列（1.5.0）

用户报的是「全屏状态下，文档/语音/套餐/账号/设置明显不适配」。先把病灶量出来：这五页的根容器一律是 `MaxWidth="980"` 居中（`DocumentPage.xaml:34`、`SpeechPage.xaml:10`、`PlanPage.xaml:33`、`AccountPage.xaml:33`、`SettingsPage.xaml:97` 的 `BodyStack`），窗口拉到全屏后左右两大片空白，内容还是窄窄一条。

按用户要求先出 HTML 效果图让他挑：`publish/ui-redesign/fullscreen/plan-a.html`（整宽仪表盘，内容区吃满、按卡片数切 1–2 列）与 `plan-b.html`（工作区 + 372px 常驻右栏）。他选了 A。

**改法**

- 新增 `src/FastTranslation.App/CardBoard.cs`：`Panel` 子类，两个依赖属性 `Columns`（期望列数）与 `MinColumnWidth`（默认 560，单列最小宽度），`Gap = 16`。`Measure` 用列宽 + 无限高去量子元素（卡片高度必须由内容决定，给固定高度就会在短卡底部拉出空白）；`Arrange` 按每列列顶依次往下排，高度用 `DesiredSize.Height`，所以卡片永远不会被拉伸。列高取最高那一列——最长列决定滚多远，短列尾部留白。
- 有效列数 = `Math.Max(1, Math.Min(Columns, floor((availableWidth + Gap) / (MinColumnWidth + Gap))))`（`CardBoard.cs:122`）。窄窗口分不出 560 就退回单列，所以窗口模式仍是原来那一列。
- 分列策略是「每次放进当前最矮的那一列」（`CardBoard.cs:92` 的 `File()`）。卡片高度彼此差几倍，按序号轮流放会出现一列很长一列很短；最矮优先能让两列高度差最小。
- 五页接上：文档 `Columns="2"`、套餐 `Columns="2"`、设置 `Columns="2"`、语音与账号 `Columns="1"`（效果图里这两页就是整宽单列）。页面 `MaxWidth` 从 980 放到 1440 并继续居中，页标题与设置页底栏（「这些设置只存在这台机器上…」+ 取消/保存）留在 `CardBoard` 外面、整宽贴页顶与页底。
- 卡片声明顺序按效果图重排（文档：拖放 → 失败 → 翻译成 → 进度 → 完成 → 错误；套餐：订阅 → 用量 → 包括什么 → 激活码 → 额度怎么重置）。所有 `x:Name` 与 code-behind 引用保留。

**数据**（`Win32 SetWindowPos` 改窗 + UIA `BoundingRectangle`，DPI 175%）

- 宽窗 2548×1440（=1456 DIP）：设置页「翻译与外观」L=467 T=168 W=985，「取词与写回」L=1484 T=168 W=985 —— 同一 Top、左右并列，两列成立；「服务」L=467 T=660、「系统」L=1484 T=760。
- 窄窗 1820×1155（=1040 DIP）：同一页四张卡 L 全为 467、T=168/660/1252/1822 —— 同列堆叠，退化单列成立。
- 阈值换算：两列需要可用宽 ≥ 560×2+16 = 1136 DIP，加侧栏 172 约等于整窗 1308 DIP。

**截图**（`tools/qa/shot-fullscreen.ps1 -ExePath -Theme -Width -Height -OutDir`，`PrintWindow` 原尺寸；都在 `publish/shots/`，该目录不进仓库）

- `full1720/`：1720×1080 浅色五页；`full1720dark/`：深色五页。卡片宽度跟内容区走、卡高 = 内容 + 内边距。
- `default1040/`：1820×1155（默认 1040×660）五页，单列、无横向滚动条，与 v86 观感一致。
- `scroll/full-light-settings-bottom-1720x1080.png`：设置页滚到底，能看到最后一张「系统」卡，底栏仍钉在底部。
- `wide2560/`：2548×1440 的两列实拍。窗口大于屏幕时 `PrintWindow` 有缩放伪影，所以两列的正式判据是上面那组 UIA 矩形，截图只作定性。

**验证**

- `dotnet build FastTranslation.slnx -c Release` → 0 警告 0 错误。
- `dotnet test FastTranslation.slnx -c Release` → Core 302 / Platform.Windows 107 / QwenMt 95 / Server 210，共 714 全绿。

**残留风险**

- 两列门槛是 560 DIP：本机 1440×960、175% 缩放下 1720 物理只等于 983 DIP，物理上压不出两列，两列只能靠 2548×1440 的 UIA 矩形证明；用户在 2560 级别屏幕上全屏才会看到两列。
- 自动分列的结果与效果图并非逐张一致（设置页左列是「翻译与外观 / 服务」，效果图是「翻译与外观 / 取词与写回 / 系统」），这是最矮优先的必然差异，不是布局错误。
- 账号页登录态（`SignedInPanel`）从左右两列 Grid 拍平成单列，本机没有账号可登，没有截图核对；`x:Name` 与事件都保留，风险只在观感。
- `CardBoard.Gap` 做成了 `public const`，本可以是 `private`。
## v88 · 分列按效果图写死：卡片自己声明落在哪一列（1.5.0）

**问题（用户 m07167：「全屏状态下和planA也完全不同」）**

- v87 的 `CardBoard` 用「逐张放进当前最矮的那一列」自动分列，把效果图里写死的左右归属打乱了。实机（v87 构建）设置页排成 左=翻译与外观 / 服务、右=取词与写回 / 排除应用 / 系统；`plan-a.html` 是 左=翻译与外观 / 取词与写回 / 排除应用、右=服务 / 系统。文档页同理（左=拖放 / 翻译成 / 翻译完成，效果图是 左=拖放 / 失败、右=翻译成 / 进度 / 翻译完成）。逐卡错位，所以用户说「完全不同」。
- 另一半原因是门槛：两列需要 `560×2+16 = 1136` 的可用宽，而 2560×1600 屏最大化后内容区正好在 1128–1145 之间摆动，稍窄一点就整页掉回单列。

**改法**

- `src/FastTranslation.App/CardBoard.cs`：删掉 `File()` 的最矮列投放，换成附加属性 `CardBoard.Column`（`DependencyProperty.RegisterAttached`，默认 0，`AffectsParentMeasure | AffectsParentArrange`）与 `GetColumn/SetColumn`；`ResolveColumns(columns)` 把不可见子元素记为 -1，其余取 `Math.Min(Math.Max(声明值, 0), columns - 1)`。`MeasureOverride` 按列累加 `高度 + 列内间距`，`ArrangeOverride` 只在「该列已有卡」时加 `Gap`，所以列首不会多出一段空白。退化到单列时列号全部夹到 0，顺序就是声明顺序（列优先），和效果图从左上读下来的顺序一致。
- `MinColumnWidth` 默认值 560 → 500：两列门槛降到 1016，全屏稳定两列、每列约 560 DIP；默认 1040 窗口可用宽 820，仍是单列。
- 列归属照 `publish/ui-redesign/fullscreen/plan-a.html` 逐卡写进 XAML：`Pages/DocumentPage.xaml` 的翻译成卡、`ProgressCard`、`ResultCard`、`ErrorCard` 加 `local:CardBoard.Column="1"`；`Pages/PlanPage.xaml` 的包括什么、激活码、额度怎么重置加 `Column="1"`；`Pages/SettingsPage.xaml` 的「服务」「系统」两个分组加 `Column="1"`。语音页与账号页仍是单列。
- 换页顺序：`MainWindow.ShowPage` 先 `SyncSidebar()` 改选中态，`PageHost.Content` 推到 `DispatcherPriority.Background` 再挂，回调里判 `_current == page` 防连点闪回。这样点下去当帧只重画侧栏那一条，内容晚一帧到。

**验证**

- `dotnet build FastTranslation.slnx -c Release` → 0 警告 0 错误；`dotnet test FastTranslation.slnx -c Release` → Core 302 / Platform.Windows 107 / QwenMt 95 / Server 210 = **714 全绿**。
- UIA 矩形（`publish/probe-uia.ps1 <exe> 2520 1600 <页名> <控件名…>`，DPI 175%，窗口 2520×1600 物理 = 1440×914 DIP）：设置页 翻译与外观 / 取词与写回 / 排除应用 L=467 W=971 同列，服务 / 系统 L=1470；套餐页右列 L=1498；文档页「拖到这里」L=813、`开始翻译` L=1834。列归属与效果图逐项对上了。
- 截图 `tools/qa/shot-fullscreen.ps1 -ExePath src/FastTranslation.App/bin/Release/net10.0-windows/FastTranslation.exe -Theme light -Width 2560 -Height 1600 -OutDir publish/shots/planA2` → 2548×1600 五张（`publish/` 不进仓库）。设置页左 翻译与外观 / 取词与写回 / 排除应用、右 服务 / 系统、底栏钉在窗口底部，与效果图结构一致。
- 文档页空状态只有「拖放」「翻译成」两张卡，效果图展示的是「已经译完一批」的密集状态，这是数据状态差异，不是样式差异。

**残留风险**

- 分列现在完全靠 XAML 里手写的 `Column`：以后往 `CardBoard` 里加卡片忘了写，它会落进左列并排在那列末尾，不会再自动找空位。
- 文档页「失败」卡与 `ErrorCard` 同属左/右列的不同语义，译完态与失败态同时出现时右列会比左列长一截（列高不齐是列式布局的固有表现）。
- 换页首帧耗时在本机（虚拟显示驱动、2548×1600 物理）是 40–95ms，拆帧后点击反馈先出、内容晚一帧；真实显卡机器上没复现这组数字，收益只在「反馈不再等整块重画」这一层。
- `CardBoard.Gap` 仍是 `public const`，本可以是 `private`。

## v89 · 去掉全局软件渲染，画刷一律冻结替换（1.5.0 性能线）

**症状**：点侧栏要等一会儿才跳页，页内上下滚动明显掉帧。

**根因一：整个进程被强制成软件渲染。** `src/FastTranslation.App/SoftwareRender.cs` 用 `[ModuleInitializer]` 把 `RenderOptions.ProcessRenderMode` 设成 `SoftwareOnly`——这是 M0 时期为压住 D3D 路径约 77MB 私有工作集做的取舍，注释里写着「在 App.OnStartup 里设太晚，D3D 在那之前就已初始化」。代价是整窗每一帧都由 CPU 光栅化：在 2548×1600 的窗口上，一次换页或一格滚动就是 400 万像素的重新绘制。文件已删除，回到 WPF 默认渲染路径。

**根因二：设计 token 是可变画刷。** `src/FastTranslation.App/App.xaml` 的 19 个画刷带 `po:Freeze="False"`，`ThemeBridge.Set` 走「就地改 `Color`」分支，于是这些画刷永远不会被冻结。冻结的 `Freezable` 才能被渲染线程直接复用，可变画刷每帧都要重新序列化一遍。现在改成整支替换新的冻结画刷，`po:Freeze="False"` 与 `xmlns:po` 一并删掉；所有引用本来就是 `{DynamicResource …Brush}`（全仓只剩 `Pages/MainPage.xaml:26` 一处 `StaticResource PillDot`，它的填充也是 DynamicResource），换条目后元素自动重新解析。

**本机测量（`FT_UITEST=nav`，窗口 2548×1600 物理）**

- 渲染层级 `tier=2 mode=Default`，即硬件路径已生效；换页帧时 pass=0 仍是 40–124ms、pass=1 22–40ms，与软件渲染时同一量级 ⇒ 这台机器的会话挂在 Oray/Idd 虚拟显示适配器上，present 本身是瓶颈，**这组数字不能代表用户本地直连显示器的观感**。
- 内存（不同时刻的粗测）：软件渲染实例 Private 121MB / WS 210MB；硬件渲染实例 289MB / 363MB。D3D 工作集确实会回来，这是拿内存换帧率。
- 对比实验过两处非瓶颈：卡片的 `CardShadowEffect` 加 `FT_NO_SHADOW` 开关后帧时几乎不变（v86 已测）；把画刷冻结后帧时也在噪声内（本机 present 掩盖了一切）。两处实验代码都没留下。

**验证**

- `dotnet build FastTranslation.slnx -c Release` → 0 警告 0 错误；`dotnet test FastTranslation.slnx -c Release` → Core 302 / Platform.Windows 107 / QwenMt 95 / Server 210 = **714 全绿**。
- 主题切换没有因为「不再就地改色」而失效：`tools/qa/shot-main-theme.ps1` 两张 2548×1600，深色直方图 `#1F242C`(Surface) + `#16191F`(WindowBg)，浅色 `#FFFFFF` + `#F7F9FC`。
- 八页整窗截图 `tools/qa/shot-audit.ps1 -Theme Light -OutDir publish/shots/v89`（2548×1600）。

**本地联调环境（登录态）**

- 服务端：`ASPNETCORE_URLS=http://127.0.0.1:5199 dotnet run --project server/FastTranslation.Server -c Release`，Development 下 `RequireEmailVerification: false`，注册 `liuyongwei@example.com` 即可登录。
- 客户端会话落在 `%LOCALAPPDATA%\FastTranslation\account-session.bin`（DPAPI，entropy `ft-account-session`，内容是 `StoredSession` JSON）。联调时直接写入该文件可以免去手敲登录表单，重启应用即恢复登录。

**残留风险**

- 常驻内存上升（D3D 工作集），原来是刻意为省内存关掉的；如果以后内存优先级反超流畅度，`SoftwareOnly` 那一行要按「只对低端机生效」的方式加回来，而不是全局强制。
- 远程桌面 / 虚拟显示适配器下硬件路径会退化成 WARP，帧时与软件渲染几乎一样；真正的体感结论只能来自用户本机直连显示器。
- 换页首帧仍然包含整块内容区重绘，拆帧只保证「点击反馈先出」，不缩短重绘本身。

**发布记录 v1.5.0（2026-11-02）**

- 提交 `149e32dcd79c6edd6036e33289901237f0579031`（v89 性能线）；版本号五处同步为 1.5.0：`Directory.Build.props`、`installer/FastTranslation.iss`、`docs/portable-readme.txt`、`README.md`、`site/index.html`。
- 构建与测试：`dotnet build FastTranslation.slnx -c Release` 0 警告 0 错误；`dotnet test` 714 全绿（Core 302 / Platform.Windows 107 / Providers.QwenMt 95 / Server 210）。
- 主仓库 Release `v1.5.0`（id 431568056，`target_commitish` 为完整 40 位 sha）：`FastTranslation-1.5.0-Setup.exe` 47175967B / sha256 `c7423789b52c8fd2be03b33265409f21ea7c484de6dde1fb86e20a98d6402663`；`FastTranslation-1.5.0-portable.zip`（263 条目）62378635B / sha256 `a41a1abebda07ba76a39ce2c5ecffed3a006ddebe5500e06240b54a8dab4b9c0`。两个资产均 `state: uploaded`，digest 与本地一致。
- 更新通道仓库 Release `v1.5.0`（id 431573150）上传同一份 Setup.exe，`state: uploaded`，digest 一致。
- `version.json` 已更新（旧 blob `e37f9d578d68cd742ff19233ad4e22417d7cd4d3` → 新 blob `19554e176f9cbf2a6cac76a8d8c9e4541521189c`，commit `ca407f15bc10bdb78e2b84ce95581bdcd03a4d45`）：version 1.5.0 / url 指向通道资产 / sha256 小写 / size 47175967 / releasePage 指主仓库 tag。
- 清单轮询（22:43–22:44）：Pages 22:43:29 仍是 1.3.7、22:44:24 已刷成 1.5.0（600 秒缓存窗口内即生效）；gh-proxy raw 立即是 1.5.0；`raw.githubusercontent.com` 直连被 `Connection was reset`（与既往一致）。
- 本机验收：同一份 Setup.exe 静默安装到 `%LOCALAPPDATA%\Programs\FastTranslation`，264 文件 / 140.2MB，注册表 `DisplayVersion` 1.5.0，窗口正常打开、`update.log` 记 `up to date (1.5.0)`。
- 发布前清理了 1.2.2 残留：僵尸注册表键（`InstallLocation` 指向已不存在的 `D:\SoftwareDownload\FastTranslation\`，导出 `.reg` 后删除）、开始菜单两个失效快捷方式、`%LOCALAPPDATA%\Programs\FastTranslation` 旧副本 139MB；用户数据 `%LOCALAPPDATA%\FastTranslation` 未动。

## v90 · 文档 / 套餐 / 设置三页按选定效果图重排（1.5.0）

用户从三版 HTML 效果图里选定 A「留白焦点」，并要求设置页取消左侧分区导航、让「系统」与「取词与写回」两张卡对齐。WPF 的设置页本来就没有分区导航（那是 mockup 加的），所以真正落地的只有三处版式。

- **文档页** `src/FastTranslation.App/Pages/DocumentPage.xaml`：投放台内层 `Grid MinHeight` 128 → 360，标题 13 → 17 SemiBold 并换成 `TextPrimaryBrush`，说明 11 → 12，按钮 `Padding` 20,6 → 24,8、`Margin` 12 → 18。判据是 128 高只装得下一行字，看着像提示条而不是「把文件丢进来」的地方；效果图里它是整页的主体。
- **套餐页** `src/FastTranslation.App/Pages/PlanPage.xaml`：把原来并排的「订阅」与「用量」两张卡并成一张 hero —— 外层 `Border` 里一个两列 `Grid`，左列 `*` 放 `SubscribeTitle` 与本月 / 今日额度，右列 360 放年付 / 月付与订阅按钮；「包括什么」提到 `CardBoard` 外面整宽；剩下的激活码（列 0）与额度怎么重置（列 1）仍交给 `CardBoard`。原来两张卡并排时右列只有一张矮卡、左列空一大片。
- **设置页** `src/FastTranslation.App/Pages/SettingsPage.xaml`：`CardBoard` 换成三段式 —— 翻译与外观、服务各自整宽，最后一段用 `CardBoard Columns="2"` 承载「取词与写回（含排除应用）」与「系统」两组。这里刻意用 `CardBoard` 而不是固定两列的 `Grid`：卡片的 `MinColumnWidth` 是 500，窗口窄到分不出两列时会自动竖排，固定 `Grid` 不会——默认 1040 窗口内容区只有约 700 逻辑像素，固定两列会把左列挤到三百多像素。

验证：

- `dotnet build FastTranslation.slnx -c Release` → 0 警告 0 错误；`dotnet test` → Core 302 / Platform.Windows 107 / QwenMt 95 / Server 210 = **714 全绿**。
- `tools/qa/shot-audit.ps1` 双主题各 9 页截图（顺手给 `$pages` 补了漏掉的「文档」页，并加了可选的 `-Width/-Height`）：`publish/ui-v90/`（默认 1040×660 窗口，物理 1820×1155）与 `publish/ui-v90-wide/`（2548×1500）。
- UIA 实测（`publish/measure-v90.ps1`，物理像素）：默认窗口下三页都是单列 —— 设置页「翻译与外观」y=389、「服务」y=881、「取词与写回」y=1479、「排除应用」y=1715、「系统」y=2127；窗口拉到 2600×1500（内容区 1144 逻辑像素，过了 1016 的分列门槛）后「取词与写回」与「系统」同为 y=1258、宽 985 并排，「套餐」页激活码与额度重置同为 y=874、宽 926，「文档」页投放台在左列而「翻译成」移到右列顶部 y=200。
- 深色直方图（`audit-Dark-plan.png`）：`#1F242C` 173749、`#16191F` 5614、`#22304D` 4367，卡片与窗口底色仍是 `ThemePalette` 的令牌，没有引入裸色值。

残留风险：套餐 hero 的右列写死 360，窗口内容区窄于约 700 逻辑像素时左列的额度文字会被压扁；文档页投放台 360 高在单列布局下会让整页需要滚动。两处都只在极端窄窗口出现，没有为此加自适应。

## v91 · 文档 / 套餐 / 设置三页真的按效果图重做：虚线投放台、42px 套餐 + 用量环、行式设置（1.5.0）

v90 只把三页的版式换了顺序，视觉仍是 v85 雾光那一套（13px 卡片标题、标签在上控件在下），用户看过截图后直接说「实际和html不符」。这一轮把效果图 A 里真正决定观感的三件事搬进 WPF：24 圆角的虚线投放台、42px 的套餐名 + 118px 用量环、左标题右控件的行式设置；顺带把默认窗口从 1040x660 撑到 1320x860，否则三页在默认窗口下只能是单列，效果图的设计意图看不出来。

改动：

- `src/FastTranslation.App/App.xaml`：新增三支装饰画刷——`StageWashBrush`（投放台中心微亮的径向渐变）、`TileGradientBrush`（74px 图标砖 160° 线性渐变）、`HeroPanelBrush`（套餐页右列的冷暖渐变），以及页面级标题样式 `PageTitle`（21px）与 `PageSub`（13px 次要色）。全部走 DynamicResource，由 ThemeBridge 按主题重新注入。
- `src/FastTranslation.App/ThemeBridge.cs`：`Apply` 里补 `SetRadial` / `SetLinear` 三处赋值。深色下「微亮的白」要换成「微亮的 Surface」，否则深色投放台中心会出现一块发灰的亮斑。
- `src/FastTranslation.App/Pages/DocumentPage.xaml`：整页重写。投放台 420 高、24 圆角、2px 虚线——Border 自己不会画虚线，虚线是叠在 Border 上的一层 `Rectangle`（`StrokeDashArray="4 3"`）；中心是 74px 图标砖 + 21px 标题 + 主次两个按钮，「翻译成 / 目标语言」压成台下一行。
- `src/FastTranslation.App/Pages/PlanPage.xaml` 与 `PlanPage.xaml.cs`：hero 拆成 1.12 / 0.88 两列。左列 42px 套餐名 + 118px 用量环，右列两张价格卡（年付默认选中，因为点进来的人多半想买一年）与满宽订阅按钮。环是 `UpdateRing` 画的：WPF 没有 conic-gradient，用一条 12px 圆头圆弧代替，`RingRadius = 53`（118/2 再让出描边半宽），从 -90° 顺时针扫 360×比例，比例过半才置大弧标志；月计量条仍由 `ProgressBar` 旧路走，只在 `ValueChanged` 里顺手把环刷一遍，两条计量不会各算一次。
- `src/FastTranslation.App/Pages/SettingsPage.xaml`：分区小标题整个去掉，改成行式——`SRow`（上边一道发丝线、上下 10px）、`SKey`（13px 主文本）、`SDesc`（11.5px 灰说明）。「取词与写回」和「系统」用 Grid `1.1* / 16 / 0.9*` 并排，两张卡默认 Stretch，高的那张决定行高、短的自动拉齐。原来的 `GroupLabel` 样式随之删除（已经没有引用）。
- `src/FastTranslation.App/MainWindow.xaml`：`Width/Height` 1040x660 → 1320x860，`MinWidth/MinHeight` 900x560 → 1000x620。

验证：

- `dotnet build FastTranslation.slnx -c Release` → 0 警告 0 错误。
- `dotnet test FastTranslation.slnx -c Release` → Core 302 / Platform.Windows 107 / QwenMt 95 / Server 210 = **714 全绿**。
- `tools/qa/shot-audit.ps1 -ExePath ... -Theme Dark|Light -OutDir publish/ui-v91` 跑出九页截图，尺寸 2310x1505（本机 175% 缩放 × 1320x860 逻辑）。
- 本模型读不了图片，所以布局是用 UIA 几何核对而不是肉眼看图（`publish/v91-geom.ps1`，输出落 `publish/v91-geom.txt`）：设置页「取词与写回」x=504 y=1428、「系统」x=1171 y=1428 —— 同一行左右两列；「目标语言」标签 x=504 与它自己的下拉框 x=1333 同一行（y 422 / 408），行式成立。套餐页「免费版」42px 在 (516,330)、环内「本月已用」(580,649)、右列 ¥144 (1211,464) 与 ¥18 (1433,464) 并排、订阅按钮 428x70。

残留风险：

- 本机 175% 缩放，而 `SetWindowPos` 收的是物理像素：传 1320x860 会被 `MinWidth/MinHeight` 夹到 1000x620，截图尺寸跟着变。这是夹取不是布局错——第一版就差点把它当成设置页被压扁的证据。
- 套餐页底部「激活码」与「额度怎么重置」在 1320 宽的默认窗口下仍是竖排（实测两张卡 x 都是 502、宽 1691），CardBoard 要更宽才并排；效果图是 1720 画布。
- 未登录或用量为 0 时用量环是空的，hero 左列会显得偏空。那是数据状态，不是样式缺陷。
- 文档页投放台只验证了结构与配色，拖放交互（DragOver / Drop）没有新的自动化覆盖，仍靠手点。

## v92 · 胶囊形框统一成圆角方框，套餐页按效果图分行

起因是用户对着效果图 A 提的两句：「将胶囊型框混成统一的圆角方框」和「套餐页面如图，激活码独占一行」。第一句说的是全局形状语言，第二句说的是套餐页的阅读顺序——两者都是 v91 没跟效果图对齐的地方。

**胶囊从哪来**：只有两处 token 是 20 圆角，`src/FastTranslation.App/App.xaml:582` 的 `TopPill`（主页面、文档页、套餐页、设置页顶栏那几枚白标签）和 `App.xaml:616` 的 `ChipButton`（结果卡的「复制译文」「存到单词本」）。框高只有 26–28，20 的圆角已经收成胶囊。统一改成 **10** —— 和按钮（`App.xaml:162` 的 10/9）、套餐页价格卡（`PlanPage.xaml:141`）、下拉框（9–10）同一档，读起来是一个族的圆角方框。侧栏计数徽标 `SidebarBadge`（`App.xaml:533`）9 → **6**：18 高的框上 9 恰好是半个高度，也是胶囊；6 跟「当前套餐」那枚小标签同级。

**套餐页**：原来三列要点挤在一张「包括什么」卡里用圆点列举，下面两张卡又被 `CardBoard Columns="2"` 各拆一半，所以整页看起来比效果图碎。现在按效果图的顺序铺：`Grid` 三等分的 facts 卡（单词本 / 四档复习 / 额度）→ 「激活码」独占一行 → 「额度怎么重置」独占一行。激活码行左边标题、右边 `TextBox Width="240"` + 「兑换」，说明行 `RedeemHint` 通栏留在下面——它同时是兑换结果和报错的出口，跟标题一起挤在左边会让报错跑出视野。重置规则从「加粗小标题 + 下面一行说明」改成左键右值行（`RuleRow` 上边一道发丝线，`RuleValue` 右对齐）。

新样式都在 `src/FastTranslation.App/Pages/PlanPage.xaml` 的资源里（`FactTitle` / `FactBody` / `RuleRow` / `RuleKey` / `RuleValue`），颜色仍走 `DynamicResource`，没有新增裸色值。

验证：

- `dotnet build FastTranslation.slnx -c Release` → 0 警告 0 错误。
- `dotnet test FastTranslation.slnx -c Release` → Core 302 / Platform.Windows 107 / QwenMt 95 / Server 210 = **714 全绿**。
- UIA 几何（`publish/v92-geom.ps1`，输出落 `publish/v92-geom.txt`）：facts 三卡标题「单词本」(519,527)、「四档复习」(905,527)、「额度」(1309,527) —— y 相同，确实并排；「激活码」标题 (523,757)、`Edit` (1093,743) 420x58、「兑换」(1531,743)；说明行 x=523 宽 1089 通栏；「额度怎么重置」(523,929)，三行 (516,1060) / (516,1127) / (516,1194)，行距 67 物理 = 38 逻辑，跟 `RuleRow` 的 10+10 内边距对得上。
- 圆角像素证据（`publish/pillcorner.ps1` 找「当前 免费版」那枚白框的左上角）：v91 在角内 inset 8 才出现 `#FFFFFF`，v92 提前到 inset 6；两版 bbox 分别 160x46 / 161x46（本机 175% 缩放）。同一坐标下白底出现得更早 = 圆角变小。
- 截图 `publish/ui-v92/`（双主题九页，2310x1505）。

残留风险：

- 圆角改成 10 以后，`TopPill` 里嵌的 `FtComboBox` 自身是 9，比外壳小 1px；175% 缩放下看不出来，100% 缩放贴近看会有一点不同心。
- facts 三卡等高靠 Grid 行默认 Stretch，任何一张的文案变长会把三张一起撑高。
- 未登录或用量为 0 时 hero 左列的用量环是空的，那仍是数据状态不是样式问题。

踩坑：用 bash 的 `head` / `tail` 拼接替换 XAML 时，把每张卡的文案写成 `'单词本|单词、短语…'` 这种字符串再用 `([t, d], i) =>` 解构，拿到的是首两个字——面板上真的只剩「单」「词」；三张卡都中招，已改回成对数组。
