# Fast Translation v1.0 执行计划(PLAN)

- 版本:2026-10-25(基于 M0 实测修订;原始蓝图见 `superpowers/plans/2026-10-23-fast-translation-v1.md`,以其为底本,M0 结论覆盖冲突项)
- M0 结论:**PASS_WITH_SCOPE**(详见 `tools/Probe/RESULTS.md`)
- 状态基线:产品代码 0 行;本文档是唯一执行依据;实施日志见 `IMPL.md`

## 1. 产品定义(不变)

Windows 划词翻译,单进程,空载 ≤50MB 私有工作集,BYO Qwen-MT Key 直连,拖选/双击/三击+全局热键,no-activate 浮窗,可写控件替换写回(v1.0:替换+复制;插入暂缓)。中文 UI,跟随系统深浅色。单次 ≤5,000 字符,不分段不流式。

## 2. M0 实测覆盖的设计决策(与蓝图的差异)

| # | 决策 | 依据(M0 实测) |
|---|---|---|
| D1 | **WPF + ModuleInitializer 软件渲染**;禁用任何 D3D 路径 | 硬件渲染空载 92.6MB→软渲染 15.6MB;OnStartup 设置无效(太晚) |
| D2 | 触发门控 = 廉价句柄预筛 ∨(DOWN I-beam ∧ 拖距>SM_CXDRAG)∨ UIA 语义(FromPoint 4 层内 Document/Edit);**语义判定在 UI 线程,hook 只投递** | Chromium 拖选中光标句柄变体;Word 恒自定义句柄;hook 有超时预算 |
| D3 | ~~Hybrid~~ **改为两段式(用户 2026-10-25 指定,豆包式)**:选词只弹工具条(复制/翻译),点击"翻译"才翻译;热键仍直达 | 用户反馈:自动翻译不可控;工具条+卡片均 ✕/点外可关 |
| D4 | UIA 读取:校验时**快照**矩形与文本;Chromium 预热重试(≤6×600ms);失败转剪贴板兜底 | Chromium 树懒激活+重建,二次读取返回 Empty |
| D5 | 剪贴板兜底默认开(序列号轮询 300ms,纯文本备份恢复);终端/密码框/排除应用跳过 | VSCode/Monaco UIA 空文本,兜底是唯一路径 |
| D6 | 写回:仅替换+复制;三重校验(HWND+RuntimeId+归一化选区);前台非来源窗口即中止 | Word 模态框实证静默失效风险;替换 8/9 |
| D7 | pill:1.5s 自动隐藏+点外消失;WS_EX_NOACTIVATE\|TOOLWINDOW\|TOPMOST;DPI 用每监视器缩放换算(禁用最小化时读 Dpi) | 连续取词遮挡;150% 缩放偏移 |
| D8 | Hook:专用 STA 线程,回调只投递,零异常逃逸;系统阈值 SM_CXDRAG/双击 | c000041d 崩溃实证 |
| D9 | 无 MVVM/DI/日志框架;NuGet 仅 DPAPI(ProtectedData);Core 零 Win32/WPF 引用 | 50MB 红线 |

## 3. 架构(不变)

```
FastTranslation.sln
├─ src/FastTranslation.Core            net10.0;选词协调、语言、翻译服务、缓存、设置
├─ src/FastTranslation.Platform.Windows net10.0-windows;Hook/UIA/剪贴板/浮窗/热键/托盘/写回
├─ src/FastTranslation.Providers.QwenMt net10.0;HTTP 适配器
├─ src/FastTranslation.App             net10.0-windows;WPF 壳、组合根、软件渲染初始化
├─ tests/FastTranslation.Core.Tests
├─ tests/FastTranslation.Platform.Windows.Tests
└─ tests/FastTranslation.Providers.QwenMt.Tests(本地 HttpListener 假服务器,无真实 Key)
```

## 4. 任务清单与验收(每任务:构建 0 警告 0 错误 + 测试绿 + IMPL.md 记录)

- **T1 解决方案与基元**:sln、项目、Directory.Build.props(Nullable/TreatWarningsAsErrors)、ScreenPoint/ScreenRect/Core 类型、翻译结果/错误类型。验收:`dotnet build` 通过。
- **T2 文本规范化与语言策略**:Unicode 类别清理(Cf/Zs/Pc 合并)、首尾裁剪、长度上限 5,000、语言检测(CJK 比例→中,否则→英;目标语言映射 Qwen 英文名)。验收:单测覆盖混合文本/边界。
- **T2b 语言目录**:92 语言(code→英文名→中文显示名),目标语言枚举+默认 auto 双向。验收:目录单测(Qwen 要求英文名)。
- **T3 设置与 DPAPI**:SettingsService(JSON,%LOCALAPPDATA%),DPAPI(CurrentUser)存 Key,迁移容错(损坏重建默认)。验收:往返/损坏单测。
- **T4 Qwen-MT Provider**:`HttpClient` 单例,`messages` 单条 user、顶层 `translation_options{source_lang,target_lang}`,Bearer 鉴权,超时/重试(429/5xx 1 次退避)、错误分类(网络/鉴权/限流/负载)。验收:假服务器单测(成功/401/429/超时/大负载)。
- **T5 翻译服务**:协调 Provider+缓存(LRU 500 条,纯内存)+取消(新请求取消旧请求)+空输入短路。验收:并发/缓存命中/取消单测。
- **T6 选词协调器**(Core):状态机 idle→capturing→translating→showing→(writing);输入事件流→动作;失败路径(捕获空→静默,翻译错→卡片显示错误);**平台接口以事件/委托注入,Core 不引用 Win32**。验收:假时钟/假事件单测。
- **T7 手势检测器**(Platform):LL hook 线程封装、拖选/双击/三击判定(系统阈值)、I-beam 快照(DOWN 时)、防重入。验收:注入事件序列的单测(手动/半自动)。
- **T8 全局鼠标监视器**:窗体间转发、来源 HWND 记录(GetAncestor)、排除列表(自身/系统窗口)。验收:构建+冒烟。
- **T9 剪贴板服务**:序列号轮询(GetClipboardSequenceNumber,300ms 窗口)、CF_UNICODETEXT、修饰键释放(SendInput)、纯文本备份/恢复、终端/密码类(UA IsPassword)跳过。验收:序列号变化单测(半自动)。
- **T10 UIA 选词读取器**:FromPoint→4 层父链→TextPattern.GetSelection(快照矩形+文本)→ValuePattern 回退;Chromium 预热;超时 400ms 后转兜底。验收:构建+实测冒烟(Notepad/Edge 手动)。
- **T11 浮窗与呈现**:pill(no-activate,1.5s 自动隐藏,点外消失,点击取词)、结果卡(近鼠标、可拖、复制/替换/插入按钮、Esc/点外关、深浅色)、DPI 每监视器换算。验收:冒烟(不抢焦点)。
- **T12 热键与托盘**:RegisterHotKey(默认 Ctrl+Alt+T 可配)、原生 Shell_NotifyIcon 托盘(设置/退出/开关)、无 WinForms。验收:热键注册单测+冒烟。
- **T13 写回服务**:三重校验(HWND+RuntimeId+归一化选区)、粘贴优先(Ctrl+V via SendInput,MOVE 前置!)、Unicode SendInput 兜底、失败→复制译文+提示;插入走 TextPattern 折叠(v1.0 隐藏入口);管理员窗口/只读/终端拒绝。验收:单测(校验逻辑)+手动 Notepad/Edge/Word。
- **T14 App 壳与设置 UI**:单实例(Mutex)、托盘常驻、首次引导(输入 Key→测试连接→完成)、设置窗(语言/模型/热键/兜底开关/排除应用/高级 BaseURL)、软件渲染 ModuleInitializer、深浅色。验收:端到端冒烟(假端点)。
- **T15 发布**:self-contained win-x64 publish + 便携 zip + Inno Setup 脚本(不签名)。验收:publish 产物启动。
- **T16 全量验证**:全部测试绿、内存复测(空载≤50/百次≤60)、验收清单跑查、THIRD_PARTY_NOTICES。

## 5. 红线(继承交接文档,仍然有效)

不静默放宽 50MB;不向终端做兜底/写回;写回三重校验;不记录选中文本/Key/剪贴板(遥测为零);GPL 不抄;不自行 git;M0 缺陷清单(RESULTS.md §2)逐条在对应任务落实。

## 6. 本次会话执行说明(用户已授权)

用户 2026-10-25 凌晨授权:夜间自主跑完 M0 → 写文档 → 按文档实施;每任务 IMPL.md 留证;不提交 git;真实 Key 联调顺延(用户提供后做);安装 SDK 已授权并完成;电源设置临时修改(standby-ac 0,原值 0x5dc=1500s 已备份,结束时恢复)。
