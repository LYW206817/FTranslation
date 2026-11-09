# Fast Translation — Windows v1.0 设计规格

- 状态：待审阅
- 日期：2026-10-22
- 范围：Windows v1.0（公开 BYO beta）
- 关联：v1.1 免费额度代理（另立 spec）、macOS v2（另立 spec）
- 许可：**2026-11-02 起改为 AGPL-3.0**（成文时为 MIT；下文出现的「MIT」是当时的决策记录，以仓库 `LICENSE` 为准）

---

## 0. 摘要

Fast Translation 是一个 Windows 桌面划词翻译工具：鼠标选中文本后，在选区旁弹出“翻译”按钮，点击后自动判断中英方向并显示译文。结果卡支持复制、替换、插入；替换/插入只对可写控件生效，且必须先通过 M0 写回门槛。v1.0 只做 Windows、只支持用户自备阿里云百炼 Qwen-MT API Key（BYO），不接后端、不做免费额度。产品以低内存（空载 ≤50MB）和“不抢焦点”的划词体验为核心差异。

v1.0 的发布形态是**未签名公开 beta**：便携版 + 按用户安装器，面向开发者/技术用户；免费额度、代码签名、自动更新、英文 UI、macOS 都放到后续版本，各自单独写 spec。

---

## 1. 背景与目标

### 1.1 要解决的问题

- 豆包等 AI 助手已经提供划词翻译，但常驻内存高；开发者机器上常驻工具的内存成本敏感。
- 现有轻量工具在“选中即弹、不抢焦点、覆盖常用应用”上的体验参差。
- v1.0 不试图复制豆包的商业模式，只解决一个窄问题：**低内存、快速、可靠的划词翻译客户端**。

### 1.2 产品定位

- 面向开发者/技术用户的 Windows 桌面工具。
- 用户自备 Qwen-MT Key，文本由客户端直接发往阿里云百炼，不经过开发者服务器。
- 公开 beta，源码公开（MIT），二进制发布；未签名，用户需自行确认 SmartScreen 提示。

### 1.3 v1.0 目标

1. 在官方支持应用里，鼠标拖选/双击/三击后按钮出现 p50 ≤300ms，且来源窗口不失去焦点。
2. 通过全局热键支持键盘选词，并为 UIA 失败提供兜底。
3. 通过 UIA + 剪贴板兜底覆盖尽量多的 Windows 文本应用；明确支持等级。
4. BYO Qwen-MT 直连，自动中英双向，结果卡可切换目标语言、复制、重试。
5. 在可写控件中支持“替换/插入”写回；只读页面/PDF 仅复制；写回通过 M0 门槛后才随 v1.0 发布。
6. 空载私有工作集 ≤50MB；翻译 100 次后 ≤60MB 且不持续增长。
7. 不记录选中文本、不做遥测；密码框和排除应用不触发。
8. 可安装、可卸载、可便携运行；未签名 beta 的安装体验可接受。

### 1.4 非目标（v1.0 明确不做）

- 免费额度、代理后端、账号体系、计费。
- 代码签名、自动更新、崩溃上报、遥测。
- macOS、Linux。
- OCR、图片翻译、扫描 PDF 识别、整页翻译、浏览器扩展。
- 全应用写回；写回只覆盖可写控件，只读页面/PDF/终端默认不写回。
- 多翻译提供方选择界面（接口保留，但只实现 Qwen-MT）。
- 术语表、翻译记忆、领域提示、流式输出、长文本分段。
- 英文 UI；v1.0 仅中文 UI。
- 管理员权限窗口、UAC 安全桌面、游戏/全屏独占应用。
- 键盘选词以外的无障碍支持（屏幕阅读器等）。

---

## 2. 用户与场景

### 2.1 目标用户

- 主要：能自行开通阿里云百炼、创建 API Key 的开发者/技术用户。
- 次要：愿意按引导配置 Key 的普通用户；v1.0 不为“零配置”优化。
- 使用环境：Windows 10 22H2 / Windows 11，x64；多显示器与 100%–200% DPI 常见。

### 2.2 核心场景

1. **浏览器读英文文档**：拖选一段，点“翻译”，看中文；可复制译文。
2. **Word/PDF 阅读**：双击单词或拖选句子；按钮贴近选区，不打断阅读。
3. **IDE/Electron 应用**：UIA 不可用时走剪贴板兜底；终端默认只走 UIA（Ctrl+C 是 SIGINT），用户可按应用显式开启兜底。
4. **键盘选词**：Shift+方向键/Ctrl+A 选中后按全局热键，直接出结果卡。
5. **二次操作**：切换目标语言、复制译文、重试失败请求。
6. **就地改写**：在可写输入框/编辑器/文档中，用译文替换原选区，或在选区末尾插入译文；只读页面/PDF 只提供复制。

### 2.3 首次使用旅程

1. 安装/解压 → 启动 → 托盘出现图标，无主窗口。
2. 首次引导（3 步）：
   - 隐私、剪贴板与写回说明：选中文本会发送到阿里云百炼；UIA 失败时可能模拟 Ctrl+C，仅处理纯文本剪贴板；替换/插入只在可写控件中可用，会临时使用剪贴板并尽量恢复。
   - API Key：打开百炼控制台链接，粘贴 Key，点击“验证”。验证失败给出 401/权限/网络分类提示。
   - 热键与完成：默认 `Ctrl+Alt+T`，可改；完成后最小化到托盘。
3. 在浏览器/Word 中拖选文本 → 按钮出现 → 点击 → 结果卡。
4. 托盘菜单可再次打开设置、暂停划词、检查更新、退出。

### 2.4 支持等级

| 等级 | 应用 | 承诺 |
|---|---|---|
| 官方支持 | Chrome、Edge、Microsoft Word、Edge PDF 为 v1.0 首发目标；Firefox、Acrobat 通过 M0（12.4 门槛）后列入，否则降为尽力而为 | 每个 release 回归；文本成功率 ≥95%；不抢焦点 |
| 尽力而为 | VSCode、JetBrains IDE、Windows Terminal、Slack/Discord/Notion 等 Electron 应用、微信/QQ、记事本 | 通过 UIA 或剪贴板兜底；终端默认只走 UIA（见 6.5）；不承诺 100%，出问题按普通 issue 处理 |
| 不支持 | 管理员权限窗口、UAC 安全桌面、游戏/全屏独占、DRM 内容、密码字段（按设计跳过） | 不承诺；文档说明限制 |

支持等级写入 README 和应用内“支持的应用”页面；用户可在设置中查看/反馈。

写回能力单独分级：只有在 M0 证明“可写检测 + 选区保持 + 粘贴校验”可靠的应用才启用替换/插入；其余只读应用、终端、管理员窗口仅复制。

---

## 3. 范围与验收

### 3.1 功能范围

- 托盘常驻、单实例、无主窗口。
- 鼠标拖选/双击/三击触发；全局热键触发。
- UIA L1/L2 + 剪贴板兜底。
- 浮窗“翻译”按钮、结果卡（译文/原文折叠/复制/替换/插入/切换目标语言/重试/关闭）。
- Qwen-MT BYO 直连；默认 `qwen-mt-flash`，可切 `qwen-mt-plus`。
- 设置：API Key、模型、目标语言、热键、剪贴板兜底、写回开关、排除应用、开机启动、主题、检查更新。
- 首次引导、隐私说明、Key 验证。
- 中文 UI；跟随系统浅/深色。
- 便携版 + Inno Setup 按用户安装器；未签名；手动检查更新。

### 3.2 非功能指标

| 指标 | 目标 | 口径 |
|---|---|---|
| 空载内存 | ≤50MB | 启动后静置 5 分钟，任务管理器“内存（专用工作集）” |
| 使用后内存 | ≤60MB，且不持续增长 | 连续翻译 100 次后测量 |
| 按钮延迟 | p50 ≤300ms，最迟 ≤500ms | 从 mouse-up 到按钮可见 |
| 翻译延迟 | 200 字技术文本 p50 ≤2s | 从点击按钮到结果卡出现；受网络与 API 影响，作为目标而非承诺 |
| 捕获成功率 | 官方应用 ≥95% | 探针阶段 ≥90%，MVP 验收 ≥95% |
| 写回成功率 | 替换 ≥95%，插入 ≥90% | 仅统计可写且校验通过的目标；M0/验收 |
| 写回安全 | 0 次误粘到只读/错误窗口 | 手工矩阵；任何一次都算失败 |
| 焦点 | 100% 不抢焦点 | 官方支持应用全量手工验证 |
| 稳定性 | 8 小时空闲 + 100 次划词无崩溃 | 句柄/GDI 不持续上涨 |
| 启动 | ≤3s 到托盘可用 | 冷启动，self-contained |

### 3.3 明确不做

见 1.4；任何新增功能先进入 v1.1/v2 候选，不进入 v1.0。

---

## 4. 关键设计决策

| 决策 | 选择 | 理由 | 替代方案 |
|---|---|---|---|
| 平台 | Windows v1.0，macOS v2 | 无 Mac 真机；Windows 捕获链路与 macOS 完全不同，无法共享核心难点 | 双端首发（工期/验证不可控） |
| 技术栈 | C# WPF + .NET 10 LTS，self-contained | UIA/Win32 接入成熟、开发效率高；.NET 8 于 2026-11-10 EOL | WinForms（更轻、UI 弱）；Rust（更省内存、开发慢） |
| 进程模型 | 单进程 | 结构简单、响应快；UIA/HTTP 用后台线程+超时隔离 | 宿主+工作进程（首击延迟、IPC 复杂） |
| 翻译 | BYO Qwen-MT 直连 | 零后端成本、文本不经过开发者；v1.0 可快速公开 | 代理免费额度（v1.1，需后端/合规/60 RPM 提额） |
| 覆盖 | UIA + 剪贴板兜底，分层支持 | UIA 无法覆盖所有应用；剪贴板是通用网 | 只支持 UIA（覆盖窄）；OCR（v2） |
| 剪贴板兜底 | 默认开，仅 UIA 失败时触发；终端默认排除 | 覆盖最广；纯文本备份/恢复、密码/排除应用跳过、终端 SIGINT 防护、首次告知 | 默认关（大量“不可用”）；按兼容列表（维护成本） |
| 写回 | 替换+插入，仅可写控件；M0 写回门槛 | 复制是只读操作；写回有误粘风险，需焦点/选区校验 | 全应用写回（不可靠）；仅复制（体验弱） |
| 取词 | Hybrid：UIA 快速路径 + 点击时剪贴板兜底；M0 A/B | STranslate 证明点击取词可行；UIA 减少副作用 | 纯 UIA（覆盖窄）；纯剪贴板（副作用大） |
| 写回方法 | 粘贴优先 + Unicode 输入兜底 | 兼顾可靠性与非文本剪贴板安全 | 只粘贴（非文本时不可用）；只输入（慢/受 IME 影响） |
| 触发 | 鼠标 + 全局热键 | 公开用户需要键盘选词和失败兜底 | 仅鼠标（覆盖窄） |
| 分发 | 未签名 beta；便携+安装器；手动更新 | v1.0 面向开发者；签名/自动更新放 v1.1 | 首发签名（成本/审核）；Store（周期长） |
| 源码 | 公开 MIT，GitHub Releases | 未签名 beta 需要可审计性；开发者向 | 闭源+自建下载页（信任/运维成本） |
| UI | 中文 v1.0 | 目标用户与 Qwen-MT 账号以中文环境为主 | 中英双语（v1.1） |
| 长文本 | 单次 ≤5,000 字符 | 避免超过 8,192 token；v1.0 不做分段 | 分段翻译（v1.1） |
| 流式 | 不做 | 短文本非流式足够；减少状态复杂度 | flash 增量流式（v1.1） |

---

## 5. 总体架构

### 5.1 进程与分层

单进程 WPF 应用，分层如下：

```
FastTranslation.exe
├─ App（WPF 壳）
│  ├─ App.xaml / App.xaml.cs
│  ├─ CompositionRoot
│  ├─ Tray / Settings / Onboarding
│  └─ app.manifest（PerMonitorV2，非管理员）
├─ Platform.Windows（可替换为 macOS 壳）
│  ├─ Input/GlobalMouseMonitor
│  ├─ Uia/UiaSelectionReader
│  ├─ Clipboard/ClipboardService / ClipboardFallback
│  ├─ WriteBack/ClipboardWriteBack
│  ├─ Overlay/ButtonWindow / ResultWindow / OverlayPresenter
│  ├─ Tray/ShellTrayIcon
│  └─ Security/DpapiSecretStore
├─ Core（不引用 WPF/Win32）
│  ├─ Selection/SelectionSnapshot / TextNormalizer / SelectionCoordinator
│  ├─ WriteBack/WriteBackResult / WriteBackStatus
│  ├─ Language/LanguagePolicy / LanguageCatalog
│  ├─ Translation/ITranslationProvider / TranslationService / LruCache / ErrorMapper
│  ├─ Settings/SettingsStore / SettingsModel
│  └─ Abstractions/ISelectionProvider / IOverlayPresenter / IWriteBackService / ISecretStore
└─ Providers.QwenMt（实现 ITranslationProvider）
```

规则：

- `Core` 不引用 `System.Windows`、`System.Windows.Forms`、Win32 P/Invoke。
- `Platform.Windows` 实现接口，负责所有操作系统交互。
- macOS v2 替换 Platform 层；若用 Swift/AppKit 写壳，Core 需改写（量小）；不承诺 C# Core 直接复用。
- 不为 macOS 提前引入跨平台 UI 框架（Avalonia/Slint）；两个平台真正难的部分本来就不共享。

### 5.2 目录结构

```
D:\FTranslation
├─ FastTranslation.sln
├─ src/
│  ├─ FastTranslation.App/
│  ├─ FastTranslation.Platform.Windows/
│  ├─ FastTranslation.Core/
│  └─ FastTranslation.Providers.QwenMt/
├─ tests/
│  ├─ FastTranslation.Core.Tests/
│  └─ FastTranslation.Providers.QwenMt.Tests/
├─ tools/
│  └─ Probe/                    一次性探针，不并入产品
├─ docs/
│  └─ superpowers/specs/
└─ README.md
```

### 5.3 核心接口

```csharp
public interface ISelectionProvider
{
    Task<SelectionSnapshot?> ReadAtPointAsync(ScreenPoint screenPoint, CancellationToken ct);
    Task<SelectionSnapshot?> ReadFromFocusAsync(CancellationToken ct);
}

public readonly record struct ScreenPoint(int X, int Y);
public readonly record struct ScreenRect(int X, int Y, int Width, int Height);

public sealed record SelectionSnapshot(
    string Text,
    IReadOnlyList<ScreenRect> BoundingRects,
    string? ProcessName,
    bool IsPassword,
    SelectionSource Source,
    nint SourceWindowHandle,
    string? SourceElementRuntimeId,
    bool IsWritable);

public interface IOverlayPresenter
{
    void ShowTranslateButton(ScreenPoint anchor);
    void ShowLoading(ScreenPoint anchor);
    void ShowResult(TranslationView view);
    void ShowError(TranslationError error, ScreenPoint anchor);
    void HideAll();
}

public interface ITranslationProvider
{
    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct);
}

public enum WriteBackStatus
{
    Success,
    Unsupported,
    SourceChanged,
    SelectionChanged,
    ClipboardBusy,
    ClipboardNonText,
    Unverified,
    Failed
}

public sealed record WriteBackResult(WriteBackStatus Status, string? Message);

public interface IWriteBackService
{
    bool CanWriteBack(SelectionSnapshot snapshot);
    Task<WriteBackResult> ReplaceAsync(SelectionSnapshot snapshot, string translation, CancellationToken ct);
    Task<WriteBackResult> InsertAfterAsync(SelectionSnapshot snapshot, string translation, CancellationToken ct);
}

public interface ILanguagePolicy
{
    DetectedLanguage Detect(string text);
    string ResolveTargetLanguage(string text, string? userOverride);
}

public interface ISettingsStore
{
    SettingsModel Load();
    void Save(SettingsModel settings);
}

public interface ISecretStore
{
    string? ReadApiKey();
    void WriteApiKey(string apiKey);
    void DeleteApiKey();
}
```

`ScreenPoint` / `ScreenRect` 是 Core 的物理像素坐标类型，不依赖 WPF；Platform 层在边界处完成屏幕坐标与 WPF DIP 的转换。`SelectionSnapshot.SourceWindowHandle` 是平台不透明句柄（Windows 为 HWND）；`SourceElementRuntimeId` 是 UIA RuntimeId 的字符串形式。写回前必须重新校验窗口、选区与 RuntimeId；RuntimeId 缺失则写回不可用。

`SelectionCoordinator` 只依赖这些接口；单测使用 fake provider/presenter。

### 5.4 线程模型

- **鼠标钩子线程**：专用线程 + 消息循环，安装 `WH_MOUSE_LL`；回调只入队，5ms 内返回，严禁 UIA/HTTP/磁盘 IO。
- **UIA 工作线程**：专用线程，同一时刻只允许一个读取；250ms 超时看门狗。UIA 是跨进程 COM，调用不可取消；超时后放弃本次，不排队。若线程永久卡死，暂停 UIA 并提示重启，不阻塞 UI。
- **HTTP**：`HttpClient` 异步，15s 超时；每次请求带 `CancellationToken`。
- **UI**：所有窗口操作在 WPF Dispatcher 线程；后台结果通过 `Dispatcher.InvokeAsync` 回传。
- **状态机**：`SelectionCoordinator` 在 UI 线程串行处理事件；新选区/关闭会取消在途请求。

### 5.5 依赖纪律

- 只使用 .NET 自带：WPF、`UIAutomationClient`、`UIAutomationTypes`、`HttpClient`、`System.Text.Json`、`System.Security.Cryptography.ProtectedData`。
- 不引入 MVVM 框架、DI 容器、日志框架、自动更新框架。
- 托盘用 `Shell_NotifyIcon` 原生封装，不引 WinForms。
- 目标框架 `net10.0-windows`；发布 `win-x64`、self-contained、非 single-file、非 trimming（WPF trimming 支持不完整）。
- 版本：SemVer，v1.0 beta 使用 `1.0.0-beta.N`。

---

## 6. 选区捕获

### 6.1 触发判定（鼠标）

- 全局 `WH_MOUSE_LL`，记录 `WM_LBUTTONDOWN/UP` 的点、时间、点击次数。
- 拖选判定用系统阈值 `SM_CXDRAG` / `SM_CYDRAG`，不硬编码 4 DIP；双击/三击用 `SM_CXDOUBLECLK` / `SM_CYDOUBLECLK` / `GetDoubleClickTime`。
- 光标过滤：mouse-up 时用 `GetCursorInfo` 与 `IDC_IBEAM` 比较；非 I-beam 的拖动（窗口/文件/滚动条）不进入划词流程。参考 STranslate 的 `MouseHookService`。
- 捕获模式（M0 A/B，默认候选 Hybrid）：
  - **Hybrid**：mouse-up 后先做 UIA L1/L2；拿到文本和矩形 → 按钮使用该快照。UIA 为空 → 仍按 mouse-up 位置显示按钮，**不立即执行剪贴板**；点击按钮时再做 UIA 重试 → L3 剪贴板取词。这样避免每次拖选都 Ctrl+C，同时保留 UIA 快速路径。
  - **onClick（STranslate 式）**：mouse-up 只显示按钮；点击时统一走 UIA → L3 取词。实现更简单、覆盖更广，但每次点击才取词。
  - M0 同时测两条路径，按官方应用成功率、按钮延迟、剪贴板副作用、UIA 调用次数决定默认。
- UIA 首次读取在 mouse-up 后 50ms 启动；为空时 150ms 重试一次；仍为空则进入“待取词”状态（Hybrid）或等待点击（onClick）。
- 按钮出现目标 p50 ≤300ms；UIA 读取可异步，未返回时按钮先出现，取词结果到达后再决定是否可翻译。
- 忽略本进程窗口上的操作；忽略前台进程在排除列表中。
- 同一时刻只保留一个快照/待取词锚点；新的捕获取消旧的读取/翻译。

### 6.2 降级链

**L1：UIA FromPoint**

1. `AutomationElement.FromPoint(point)`。
2. 从该元素向上最多 10 层，查找支持 `TextPattern`（优先 `TextPattern2`）的元素。
3. `GetSelection()` 取选区范围；每个范围 `GetText(-1)` 拼接；`GetBoundingRectangles()` 取逐行矩形。
4. 读取 `AutomationElement.IsPasswordProperty`；为 true 直接放弃。
5. 250ms 超时；超时放弃，不排队。

**L2：UIA FocusedElement**

1. `AutomationElement.FocusedElement`。
2. 同样向上查找 `TextPattern`，取选区。
3. 用于 L1 打到非文本子控件、或热键路径。
4. 与 L1 共用工作线程与超时。
5. 参考 Text-Grab 的 `UIAutomationUtilities`：Fast/Balanced/Thorough 遍历深度（2/6/12）、点/区域采样、去重、排除本进程窗口；`ValuePattern` 只用于可写检测，`Name` 回退只用于非编辑文本控件，不能替代选区文本。

**L3：剪贴板兜底（默认开）**

仅在 L1/L2 都没有拿到文本时执行：

1. 若前台进程在 `clipboardFallbackExcludedApps`（默认含终端与密码管理器）或 `excludedApps`、`IsPassword` 为 true、或剪贴板含非文本格式（图片/富文本/文件），直接跳过。终端默认排除是为了避免把 Ctrl+C 当复制而误发 SIGINT。
2. 记录 `GetClipboardSequenceNumber()`。
3. 备份当前纯文本剪贴板内容；读取支持 `CF_UNICODETEXT` / `CF_TEXT` / `CF_OEMTEXT`，备份只保存可安全恢复的文本。
4. 先释放可能卡住的 Ctrl/Alt/Win/Shift，再 `SendInput` 发送 Ctrl+C。参考 STranslate `ClipboardHelper.SendCtrlCV()` 的按键清理。
5. 以 10ms 间隔轮询序列号，最多 `clipboardFetchTimeoutMs`（默认 300ms，范围 50–5000ms）；序列号变化后等待 30ms 再读。
6. 读取剪贴板文本；恢复备份文本；恢复失败只记元数据日志，不弹窗。
7. 超时或序列号未变化 → 失败，**不把超时时的旧剪贴板内容当选区文本**；恢复备份后静默结束。
8. 若应用把 Ctrl+C 绑定为其他行为（IDE 等；终端已在默认排除列表中），以序列号与文本变化为准；无法区分时按失败处理。
9. 首次运行时明确告知该行为；设置中可按应用关闭；密码管理器默认排除。

**L4：静默失败**

- 不弹错误框、不反复打扰。
- 首次失败可在托盘气泡提示一次“该应用暂不支持，可在设置中开启/检查兼容模式”；同类提示 24 小时内最多一次。

### 6.3 文本归一化与长度

- 去除零宽字符（U+200B/U+200C/U+200D/U+FEFF）、将 NBSP 归一为空格。
- 合并 3 个以上连续换行为 2 个；保留段落结构；不裁剪 Markdown 标记。
- trim 首尾空白。
- 有效文本要求：至少包含 1 个字母、汉字、假名或谚文；纯数字/标点/空白不弹按钮。
- 单次上限 **5,000 字符**；超过则不调用 API，结果卡提示“文本过长（v1.0 最多 5,000 字）”，提供复制原文。v1.1 再做分段。
- 下限 1 字符；单词双击可翻译。

### 6.4 语言判定与目标语言

- 本地脚本启发式（不额外调用检测 API）：
  - 含假名 → 视为日语；含谚文 → 视为韩语。
  - 汉字比例 ≥25% 且不含假名/谚文 → 视为中文。
  - 其他 → 视为非中文。
- 目标语言：
  - 用户在设置中指定了目标语言 → 始终使用该语言。
  - 否则自动双向：源为中文 → 英语；源为非中文 → 简体中文。
  - 结果卡可切换目标语言，并记住上次选择（写入设置）。
- 语言目录内置 Qwen-MT 官方 92 种语言（英文名 + 本地显示名）；设置里的完整列表按官方表；结果卡常用语言优先。
- Qwen-MT 请求的 `source_lang` 固定传 `"auto"`；`target_lang` 传英文语言名（如 `"Chinese"`、`"English"`、`"Japanese"`）。
- 若本地判定目标语言与源语言相同：不调用 API，显示“原文已是目标语言”，提供切换目标语言/复制原文；允许用户强制翻译（仍以 `auto` 源语言调用）。

### 6.5 剪贴板兜底的安全规则

- 只处理纯文本剪贴板；含图片、文件、HTML/RTF 等格式时跳过（不做不可靠的全格式备份）。
- 密码框、密码管理器、排除应用不执行；终端默认排除（Ctrl+C 在终端是 SIGINT，可能中断构建/进程），用户可按应用显式开启。
- 不记录剪贴板内容；日志只记“成功/失败/序列号变化”。
- 首次运行告知；设置中提供总开关和按应用排除。
- 恢复失败时，保留用户剪贴板现状，不二次覆盖。
- 不针对管理员窗口执行（UIPI 下 SendInput 无效）。

### 6.6 失败与静默策略

- Hybrid：L1/L2 空 → 仍按 mouse-up 位置显示按钮；点击时再做 L1/L2 重试 → L3（若开启）→ 仍空则静默。
- onClick：mouse-up 只显示按钮；点击时统一 L1/L2 → L3 → 仍空则静默。
- UIA 超时/卡死 → 跳过本次；连续 3 次超时暂停 UIA 读 30 秒，期间只走 L3（若开启）。
- 排除应用/密码框 → 连 L3 也不执行。
- 前台窗口属于本进程 → 不触发。

---

## 7. 浮窗与交互

### 7.1 翻译按钮

- 无边框、置顶、不抢焦点：`WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`；`ShowActivated=false`；`ShowWindow(SW_SHOWNOACTIVATE)`；处理 `WM_MOUSEACTIVATE` 返回 `MA_NOACTIVATE`。
- 尺寸约 28–32 DIP 高，圆角胶囊，文字“翻译”，带轻阴影；跟随系统浅/深色。
- 不显示在任务栏；不进入 Alt+Tab。
- 锚点：选区**最后一行矩形**的右下角；无矩形时用 mouse-up 点。偏移 8 DIP；越界翻到上方；夹到当前显示器工作区。
- 隐藏条件：外部鼠标按下、滚轮、Esc/任意按键、前台窗口切换、点击按钮、新选区开始、屏幕/分辨率变化。
- 不自动超时；阅读时按钮不自己消失。
- 点击按钮后立即隐藏按钮，显示结果卡 loading；Hybrid 模式下若 mouse-up 已取得快照则直接翻译，若快照为空则先在点击时执行 UIA 重试 → L3 剪贴板；onClick 模式下统一在点击时取词。点击时不再对外发送额外鼠标事件，避免打断来源应用。

### 7.2 结果卡

- 宽度 380 DIP；最大高度为工作区 60%；内容超出滚动。
- 初始显示不抢焦点；点击卡片内部才激活，允许选中译文。
- 内容：
  - 顶部：目标语言下拉、关闭；错误时显示重试。
  - 正文：译文正文（首选）。
  - 原文折叠区（默认折叠）。
  - 底部操作：替换、插入、复制。
  - 状态：loading（spinner + “翻译中…”）、success、error。
- 目标语言切换：重新翻译（取消在途请求）；记住上次选择。
- 复制：有译文时始终可用；复制译文到剪贴板；按钮短暂变为“已复制”。
- 替换/插入：仅在翻译成功且 `snapshot.IsWritable`、`SourceElementRuntimeId` 非空、来源窗口/选区校验通过时可用；否则置灰并用 tooltip 说明原因（只读/终端/管理员/无 RuntimeId/选区已变化）。tooltip 同时说明“替换=覆盖原选区，纯文本”“插入=在选区末尾插入”。
- 写回方法自动选择：剪贴板粘贴优先；剪贴板含非文本/被占用或粘贴失败时，改用 Unicode 输入；两者都不行则只留复制。
- 替换/插入成功：显示“已替换/已插入（可在原应用 Ctrl+Z 撤销）”，约 1.2s 后关闭卡片；失败：复制译文并说明原因。
- 写回按钮本身不抢焦点；若结果卡已被点击激活，写回前会先把前台切回来源窗口并重新校验选区。
- 关闭：Esc、点击卡片外部、关闭按钮、新选区。
- 结果卡与按钮窗复用实例；隐藏时释放文本引用。

### 7.3 定位与 DPI

- 应用清单声明 PerMonitorV2。
- UIA 矩形是物理像素；按所在显示器 DPI 转为 WPF DIP；使用 `MonitorFromPoint`/`GetDpiForWindow`。
- 多显示器混合 DPI：以锚点所在显示器为准；跨屏时重新计算。
- 工作区使用 `SystemParameters.WorkArea` 的显示器版本（`MonitorFromPoint` + `GetMonitorInfo`），不使用主屏工作区。
- 卡片/按钮不覆盖选区本身；空间不足时放到选区上方；仍不足则夹到工作区并允许覆盖。

### 7.4 焦点规则

- 按钮窗：完全不激活；点击不改变前台窗口。
- 结果卡：初始不激活；点击内部后激活，便于选中/滚动；关闭后不强制把焦点还给来源应用（避免副作用）。
- 写回是例外：仅当前台是结果卡或来源窗口时，用户点击替换/插入才允许把前台切回来源窗口；切换前校验句柄，切换后重新校验选区；前台是第三个应用时中止，失败则复制。
- 除写回外不调用 `SetForegroundWindow` 抢焦点；需要激活结果卡时由用户点击触发。
- 不用 `Topmost` 覆盖 UAC 安全桌面；在安全桌面不显示。

### 7.5 取消与竞态

- 每次捕获生成递增 `selectionId`；读取/翻译结果只接受当前 id。
- Hybrid 下按钮可先出现、取词未完成；点击时若快照为空，先捕获再翻译，仍受同一 `selectionId` 约束。
- 点击时若 UIA 读取仍在途，先等待其完成（同一 250ms 超时），不并发发起第二次读取；超时则改走 L3。
- 新选区、关闭卡片、切换目标语言、退出应用都会取消旧请求。
- HTTP 取消不保证服务端停止计费，但客户端不再渲染旧结果。
- 按钮隐藏时不清空“当前快照”，直到下一次捕获或退出；避免点击瞬间丢文本。

### 7.6 全局热键

- 默认 `Ctrl+Alt+T`；设置中可改；使用 `RegisterHotKey`。
- 进入前台全屏时可暂停全局热键（可配置），退出全屏恢复；恢复时若冲突，有限退避重试，只有最终失败才提示。参考 STranslate 的 `ForegroundFullscreenMonitor` / `HotkeySettings`。
- 禁止把 Ctrl+C 注册为全局热键；它是系统复制保留键。
- 注册失败（被占用）→ 设置页提示并允许重新选择；不静默失败。
- 触发路径：
  1. 取前台窗口的 `FocusedElement`，走 L2 读取选区。
  2. 有文本 → 结果卡锚定到选区矩形/光标矩形；无矩形则锚定到焦点窗口中心。
  3. 无文本且剪贴板兜底开启 → 执行 L3（同样跳过密码/排除应用）。
  4. 仍无文本 → 托盘气泡提示一次（24 小时内最多一次）。
- 热键不打开按钮，直接进入结果卡；这是键盘选词的默认交互。
- 热键与鼠标路径共用 `SelectionCoordinator` 和 `ITranslationProvider`。

### 7.7 写回：替换与插入

**定义**

- 替换：用译文覆盖原选区。
- 插入：在选区末尾插入译文，保留原文与选区内容。
- 复制：只写剪贴板，不接触来源控件。

**可用前提（全部满足）**

1. `enableWriteBack` 开启；来源进程不在 `excludedApps` / `writeBackExcludedApps`；`IsPassword=false`。
2. `snapshot.IsWritable=true`，`SourceElementRuntimeId` 非空，且该应用/控件类型已在 M0 写回矩阵中通过。
3. 来源窗口句柄有效，能切回前台（`SetForegroundWindow` 成功）。
4. 写回方法可用：剪贴板为空/纯文本时用粘贴；剪贴板含非文本/被占用时改用 Unicode 输入；两者都不可用才只留复制。
5. 不是管理员窗口（UIPI 限制）、不是终端（默认排除）、不是只读控件。
6. 当前没有进行中的剪贴板兜底/写回操作（串行化）。

**时序**

1. 用户点击“替换/插入”。若结果卡当前是前台窗口，先 `SetForegroundWindow(SourceWindowHandle)`，等待 100ms，确认前台窗口等于来源窗口；若前台既不是结果卡也不是来源窗口（用户已切到第三个应用）→ 中止，避免抢焦点；切换失败 → 中止。
2. 用 UIA L2/L1 重新读取当前焦点元素与选区：RuntimeId 必须与快照一致，归一化文本也必须与 `snapshot.Text` 一致；任一不一致 → 中止，复制译文，提示“原选区已变化，未写入”。
3. 插入：取 `TextPattern` 选区范围 → `Clone()` → 把 Start 端点折叠到 End（得到选区末尾的退化范围）→ `Select()` 设置光标；provider 不支持时，发送一次 Right 键并重新校验；仍不可靠 → 中止。
4. 优先剪贴板：备份纯文本；写入译文；`SendInput` Ctrl+V；等待 200–300ms（按文本长度）；恢复备份。若剪贴板含非文本/被占用，或粘贴失败，改用原生 `SendInput` Unicode 输入译文（多行按 STranslate `InputHelper` 的思路处理换行）。
5. Best-effort 校验：重新读取控件文本/选区，确认译文出现；无法确认时提示“已发送，请检查原应用”，并把译文留在剪贴板。
6. 成功：显示“已替换/已插入（可用 Ctrl+Z 撤销）”，约 1.2s 后关闭卡片。
7. 任何失败：复制译文，说明原因，保留卡片；**不自动重试**，避免重复插入。

**安全规则**

- 写回前必须校验来源窗口、RuntimeId 与选区；任何不一致立即中止。
- 不向终端写回（粘贴可能执行命令）；不向管理员窗口写回（UIPI）；不向只读控件写回。
- 不破坏非文本剪贴板：含图片/文件/HTML/RTF 时不用剪贴板，改用 Unicode 输入；输入也不可用则复制。
- 写回与剪贴板兜底共用一个剪贴板锁，串行执行；剪贴板被占用时重试 3 次（每次 50ms），仍失败则中止。
- 写回是显式破坏性操作：不自动执行、不自动重试、不自动撤销；只提示 Ctrl+Z。
- 写回只写入纯文本，不保留原选区的富文本格式；换行/缩进由目标应用按自身规则处理。
- 仅靠 L3 剪贴板取到的文本没有 UIA RuntimeId，写回不可用，只提供复制。
- 日志只记状态码、应用类别、耗时；不记文本、不记剪贴板内容。

---

## 8. 翻译

### 8.1 Qwen-MT 接入

- 提供方：阿里云百炼 Qwen-MT。
- 默认 base URL：`https://dashscope.aliyuncs.com/compatible-mode/v1`（OpenAI 兼容接口）。
- 高级设置允许覆盖 base URL；官方推荐使用业务空间专属域名 `https://{WorkspaceId}.cn-beijing.maas.aliyuncs.com/compatible-mode/v1`。发布前用真实账号验证默认端点；若默认端点不可用，改用专属域名并在引导中收集 WorkspaceId。自定义地址会把 API Key 发往该地址，默认隐藏，修改时二次确认。
- 鉴权：`Authorization: Bearer <API Key>`。
- 模型：
  - 默认 `qwen-mt-flash`：通用首选，92 语言，较快，支持增量流式（v1.0 不用流式）。
  - 可选 `qwen-mt-plus`：质量优先，92 语言，标准速度，非增量流式。
  - 不使用 `qwen-mt-lite`（语言数较少）和 `qwen-mt-turbo`（官方建议由 flash 替代）。
- 直连：客户端 → 阿里云；不经过开发者服务器；无代理、无账号。

### 8.2 请求与响应

请求（HTTP JSON，`translation_options` 为顶层字段）：

```json
{
  "model": "qwen-mt-flash",
  "messages": [
    { "role": "user", "content": "待翻译文本" }
  ],
  "translation_options": {
    "source_lang": "auto",
    "target_lang": "English"
  },
  "stream": false
}
```

- 只允许一条 user message；不发送 system message。
- v1.0 不使用 `terms`、`tm_list`、`domains`。
- 不设置 `temperature`/`top_p`/`max_tokens`，使用服务默认；如遇输出截断，v1.1 再调。
- 响应取 `choices[0].message.content`；`usage` 仅用于本地统计（不展示账单）。
- 最大输入 8,192 token；应用层 5,000 字符上限先行拦截。
- 非流式；短文本一次性返回。
- 限流（官方模型页，北京）：flash 60 RPM / 35,000 TPM；plus 60 RPM / 25,000 TPM。BYO 时按用户账号限流；客户端只做 429 处理，不自建复杂限流器。

### 8.3 超时、重试、取消、缓存

- 超时：15s（含连接与响应）。
- 重试：网络错误/5xx 自动重试 1 次，间隔 300ms；401/403/400/429 不自动重试。
- 取消：每次请求带 `CancellationToken`；新选区/关闭/切换目标语言取消旧请求。
- 缓存：内存 LRU，最多 200 条；键为 `model + sourceLang + targetLang + SHA256(text)`；只缓存成功结果；退出即清空；不落盘。
- 相同文本重复翻译直接命中缓存，不发请求。
- 缓存不跨模型/目标语言复用。

### 8.4 错误矩阵

| 情况 | 用户看到 | 行为 |
|---|---|---|
| 401/403 | “API Key 无效或未开通百炼” + “打开设置” | 不重试；引导检查 Key/权限/实名/额度 |
| 429 | “请求过多或额度不足，请稍后重试” | 不自动重试；提供重试按钮；提示检查百炼控制台 |
| 400 | “请求无效：文本或语言不被支持” | 不重试；保留原文；允许复制 |
| 5xx | “服务暂时不可用，请重试” | 自动重试 1 次，仍失败则显示重试按钮 |
| 网络/超时 | “网络不可用或超时，请检查网络/代理” | 自动重试 1 次；提供重试 |
| 空译文 | “服务未返回译文” | 按错误处理，不显示空白卡 |
| 文本过长 | “文本过长（v1.0 最多 5,000 字）” | 不调用 API；提供复制原文 |
| 源语言=目标语言 | “原文已是目标语言” | 不调用 API；提供切换目标语言/强制翻译 |
| 缓存命中 | 正常结果 | 不发请求；标记“缓存”可选（v1.0 不显示） |

错误信息不包含选中文本；日志只记错误码、HTTP 状态、耗时。

### 8.5 隐私边界

- 选中文本仅存在于内存与发往阿里云的 HTTPS 请求中；不写磁盘、不写日志、不做遥测。
- API Key 只存 DPAPI 加密的本地文件；不写日志、不进崩溃报告。
- 不做任何“改进模型/分析用量”的数据回传。
- 用户可在设置中查看隐私说明和阿里云百炼的数据处理条款链接。

---

## 9. 设置、引导与托盘

### 9.1 设置模型

`%APPDATA%\FastTranslation\settings.json`，`schemaVersion = 1`：

```json
{
  "schemaVersion": 1,
  "provider": {
    "model": "qwen-mt-flash",
    "baseUrl": null
  },
  "language": {
    "mode": "auto",
    "targetLanguage": null
  },
  "hotkey": "Ctrl+Alt+T",
  "clipboardFetchTimeoutMs": 300,
  "writeBackMethod": "auto",
  "clipboardFallback": true,
  "excludedApps": [
    "KeePass.exe",
    "1Password.exe",
    "Bitwarden.exe",
    "LastPass.exe"
  ],
  "clipboardFallbackExcludedApps": [
    "WindowsTerminal.exe",
    "cmd.exe",
    "powershell.exe",
    "pwsh.exe",
    "conhost.exe"
  ],
  "enableWriteBack": true,
  "writeBackExcludedApps": [
    "WindowsTerminal.exe",
    "cmd.exe",
    "powershell.exe",
    "pwsh.exe",
    "conhost.exe"
  ],
  "startWithWindows": false,
  "theme": "system",
  "softwareRendering": true,
  "uiLanguage": "zh-CN"
}
```

- 写入使用临时文件 + 原子替换；损坏时回退默认值并备份坏文件。
- 未知字段保留（向前兼容）；缺失字段取默认值。
- `excludedApps` 按进程名不区分大小写匹配，作用于所有捕获（UIA + 兜底）；`clipboardFallbackExcludedApps` 只排除剪贴板兜底，UIA 仍然可用。用户可在设置中添加/删除。
- `enableWriteBack`：替换/插入的总开关；`writeBackExcludedApps` 只排除写回，不影响读取、翻译与复制。终端默认排除。
- `clipboardFetchTimeoutMs`：模拟 Ctrl+C 后的序列号轮询上限，默认 300ms，范围 50–5000ms；超时不采用旧剪贴板内容。
- `writeBackMethod`：`auto` / `paste` / `type`；默认 `auto`（粘贴优先，失败或剪贴板非文本时用 Unicode 输入）。高级设置才显示。
- `theme`: `system` / `light` / `dark`；v1.0 支持跟随系统与手动切换。
- `softwareRendering`: 低内存模式，默认 `true`。`true` 强制 WPF 软件渲染；`false` 交回 WPF 选择（通常是 GPU 硬件加速）。设置页改动下次启动生效。软件渲染在同一台机器（1440×960、当前带主窗口与全部页面）实测空闲私有工作集约 65MB、走完全部页面约 70MB；硬件渲染对应约 220MB / 270MB。代价是首窗约 +0.5s、重绘帧时间约 1.5–2 倍。
- `uiLanguage`: v1.0 固定 `zh-CN`；保留字段供 v1.1。

### 9.2 密钥存储

- `%APPDATA%\FastTranslation\secret.dat`。
- `ProtectedData.Protect`，`DataProtectionScope.CurrentUser`。
- 只存 API Key；不存账号密码。
- 设置页显示“已配置/未配置”，不回显完整 Key；支持替换/删除。
- Key 验证：保存时发一次短文本翻译；401/403 拒绝保存并提示；网络错误允许保存但标记“未验证”。

### 9.3 首次引导

- 3 步向导，可跳过（跳过则托盘提示配置 Key）。
- 第 1 步：隐私 + 剪贴板兜底 + 写回说明 + 同意/退出（写回只在可写控件、临时使用剪贴板、失败即复制）。
- 第 2 步：Key 配置（链接、粘贴、验证、错误分类）。
- 第 3 步：热键选择/测试、是否开机启动、完成。
- 引导完成后不自动弹出主窗口；驻留托盘。

### 9.4 托盘与单实例

- 原生 `Shell_NotifyIcon`；隐藏顶层窗口接收回调；处理 `TaskbarCreated` 重新注册。
- 菜单：打开设置、暂停/恢复划词、检查更新、退出。
- 双击托盘打开设置。
- 单实例：命名 Mutex；第二次启动唤起已有实例并打开设置。
- 暂停状态：卸载/禁用鼠标钩子与热键，保留托盘；重启后恢复上次状态（不持久化暂停，启动默认启用）。

### 9.5 日志

- `%LOCALAPPDATA%\FastTranslation\logs\app-YYYYMMDD.log`，保留 7 天。
- 只记录：时间、级别、模块、事件码、HTTP 状态、耗时、字符数（数量，不记内容）、应用类别（不记进程名/窗口标题）。
- 不记录选中文本、剪贴板内容、API Key、完整 URL 查询参数。
- 默认级别 Information；调试级别需用户手动开启，仍不记录正文。
- 崩溃：捕获未处理异常，写元数据日志；不自动上传。

### 9.6 更新与卸载

- v1.0 手动检查更新：托盘/设置中打开 GitHub Releases 页面；不自动下载、不后台联网。
- 版本号显示在设置“关于”。
- 安装器：Inno Setup，按用户安装，`PrivilegesRequired=lowest`，安装到 `%LOCALAPPDATA%\Programs\FastTranslation`；开始菜单快捷方式；可选桌面图标；卸载器。
- 便携版：zip，解压即用；配置仍写 `%APPDATA%`（可便携化放 v1.1）。
- 卸载默认保留设置/密钥，询问是否删除；便携版删除目录不自动删 `%APPDATA%`，README 说明。

---

## 10. 安全、隐私与合规

### 10.1 数据流

1. 选中文本 → 本进程内存。
2. 点击翻译 → HTTPS 直连阿里云百炼（用户自己的 Key）。
3. 结果 → 本进程内存 → 结果卡。
4. 替换/插入：译文从本进程内存写入来源控件；只在本机完成，不联网；失败则复制。
5. 不经过开发者服务器；不写磁盘；不记录正文。

### 10.2 密钥

- DPAPI CurrentUser；文件权限继承用户配置目录。
- 不把 Key 放在命令行、环境变量、URL、日志。
- 不在内存中长期保留明文超过请求生命周期（读取后尽快使用；.NET string 无法可靠清零，接受该限制并避免复制扩散）。

### 10.3 剪贴板

- 默认开；首次告知；纯文本备份/恢复；序列号校验；密码/排除应用跳过。
- 不读取剪贴板历史；不写入非文本内容；恢复失败不二次覆盖。
- 用户可按应用关闭；关闭后只走 UIA。
- 写回（替换/插入）优先使用剪贴板：只允许纯文本或空剪贴板；含图片/文件/富文本时改用 Unicode 输入，不覆盖非文本剪贴板。
- 写回前校验来源窗口、RuntimeId 与选区；任何不一致立即中止并复制译文；前台是第三个应用时中止，不抢焦点；不自动重试。
- 终端默认禁用写回（粘贴可能执行命令）；管理员窗口不支持。
- 写回与兜底共用一个剪贴板锁，串行执行。

### 10.4 密码与排除应用

- UIA `IsPassword=true` → 不显示按钮、不执行兜底。
- 默认排除密码管理器：KeePass、1Password、Bitwarden、LastPass（按进程名）。
- 剪贴板兜底额外默认排除终端（WindowsTerminal/cmd/powershell/pwsh/conhost），避免 Ctrl+C 被当作复制而触发 SIGINT；用户可为单个终端应用显式开启。
- 写回同样默认排除终端，且只对可写控件启用；终端/只读/管理员窗口只提供复制。
- 用户可添加排除应用；设置页提供“从当前前台进程添加”快捷操作。
- 管理员窗口：UIPI 限制，预期不支持；文档说明。

### 10.5 日志与遥测

- 无遥测、无崩溃上报、无使用统计。
- 本地日志只记元数据；用户可一键打开日志目录、清空日志。
- 发布包不含分析 SDK。

### 10.6 分发与签名

- v1.0 未签名；README 说明 SmartScreen“更多信息 → 仍要运行”，提供 SHA-256。
- 源码公开（MIT），用户可自行构建；这是未签名 beta 的信任补偿。
- v1.1 面向大众/免费额度前必须完成代码签名（OV/EV，具体资格与价格实施前核实）；签名是 v1.1 的发布门槛。
- 自动更新在 v1.1 签名后实现；v1.0 手动检查。

### 10.7 第三方条款

- BYO 模式下，用户直接与阿里云百炼缔约；应用只是客户端。用户需遵守百炼服务条款。
- 应用内提供百炼服务条款/隐私政策链接。
- ~~v1.1 若做免费额度代理，必须先取得阿里云对“代理/转售/代付”的书面确认；未确认前不实现代理。~~ **2026-10-28 用户决策作废**：不再要求书面确认，代理按 D2/D5 直接实施（见 `2026-10-28-account-and-billing-design.md`）。条款层面的代付风险由用户自行承担，缓解措施见该文档 §7.1（额度耗尽自动回落 BYO Key）。
- v1.1 代理涉及处理用户文本，需单独的隐私政策、数据保留策略、PIPL/GDPR 评估；不在 v1.0 范围。

---

## 11. 性能与内存

### 11.1 测量口径

- 主口径：任务管理器“内存（专用工作集）”，启动后静置 5 分钟。
- 辅助：`PROCESS_MEMORY_COUNTERS_EX2.PrivateWorkingSetSize`、`WorkingSet64`、句柄数、GDI 对象数。
- 场景：
  - 冷启动空载 5 分钟。
  - 连续 100 次划词/翻译后。
  - 8 小时空闲（含一次显示器休眠/唤醒）。
- 记录 p50/p95 延迟、失败次数、UIA 超时次数。

### 11.2 目标

- 空载 ≤50MB；100 次翻译后 ≤60MB；不持续增长。
- 按钮 p50 ≤300ms；结果 p50 ≤2s（200 字，网络相关）。
- 空闲 CPU 平均 <1%；启动 ≤3s 到托盘可用。
- 无句柄/GDI 泄漏。

### 11.3 优化手段

- 不引入 MVVM/DI/日志框架；减少程序集加载。
- 托盘用原生 P/Invoke；不加载 WinForms。
- 按钮窗预创建；结果卡首次使用时创建并复用。
- 隐藏时清空大文本引用；缓存上限 200 条。
- 不把 UIA 程序集提前预热到启动路径；首次划词时加载，冷启动内存按空载口径测量。
- self-contained、ReadyToRun 可选；不使用 trimming（WPF 支持不完整）。
- 发布前用真实数据测量，不以估算替代。

### 11.4 失败处理

M0 探针若空载 >50MB：

1. 先排查是否加载了非必要程序集/窗口。
2. 仍超标 → 换 WinForms 实现浮窗（保留 Core/Provider），目标 ≤50MB。
3. 仍超标 → 评估 Rust（egui/Slint）重写 Platform/UI；Core 逻辑可移植。
4. 不允许静默放宽 50MB；若用户决定放宽，必须更新本 spec 并记录理由。

---

## 12. M0 探针

### 12.1 目的

在写产品代码前，用一次性 WPF 程序验证五件事：

1. UIA 在目标应用里到底能不能拿到选区文本和矩形。
2. no-activate 浮窗是否真的不抢焦点。
3. WPF + UIA 的空载/使用后内存是否满足 50MB 门槛。
4. 写回：在可写控件里替换/插入能否准确落到原选区；只读/终端/管理员窗口是否能可靠拒绝。
5. 对比 Hybrid/onClick 两种取词路径，以及粘贴/Unicode 输入两种写回方法。

### 12.2 最小实现

- 独立 `tools/Probe` WPF 项目（.NET 10），不并入产品。
- 全局鼠标钩子 + UIA L1/L2 + 一个 no-activate 按钮窗 + 内存读数。
- 可选：热键、剪贴板兜底，用于验证第二触发路径。
- 写回探针：可写检测、选区校验、Ctrl+V 替换、UIA 折叠后插入、剪贴板恢复；不做真实翻译。
- A/B 开关：取词模式 Hybrid/onClick；写回方法 paste/type；记录成功率、延迟、剪贴板副作用。
- 不做真实翻译；用固定文本替代，避免 API 变量。
- 输出每个应用的测试结果到控制台/日志。

### 12.3 测试应用

- 必测：Chrome、Edge、Firefox、Microsoft Word、Edge PDF、Acrobat（若安装）、记事本。
- 写回必测：Chrome/Edge 的 input/textarea/contenteditable、Word、记事本、VSCode 编辑器、Slack/Discord 输入框；只读对照：普通网页、Edge PDF；终端只验证“被拒绝”，不实际写入。
- 记录但不作为捕获门槛：VSCode、JetBrains IDE、Windows Terminal、Slack/Discord/Notion、微信/QQ。
- 每个应用测试：拖选单词、拖选句子、跨行选择、双击、三击、反向拖选、屏幕边缘、100%/150%/200% DPI、混合 DPI 双屏。
- 取词 A/B：同一批应用分别跑 Hybrid/onClick；记录 UIA 调用次数、Ctrl+C 次数、按钮延迟、误触发（非 I-beam 拖拽）。
- 写回 A/B：同一批可写目标分别跑粘贴/Unicode 输入；记录替换/插入成功率、耗时、多行/IME/撤销表现。

### 12.4 门槛

- 官方应用文本成功率 ≥90%（探针）；MVP 验收 ≥95%。
- 选区矩形成功率 ≥80%（官方应用）；无矩形时按钮可用 mouse-up 锚点。
- 所有测试应用不抢焦点。
- 按钮 p50 ≤300ms。
- 空载 5 分钟 ≤50MB；100 次读取后 ≤60MB；无持续增长。
- UIA 无永久卡死；超时能跳过。
- 写回：可写目标替换成功率 ≥90%（探针）/≥95%（验收），插入 ≥85%（探针）/≥90%（验收）；只读/终端/管理员窗口 0 次误写；剪贴板文本恢复成功；来源窗口/RuntimeId/选区校验生效；同窗口切标签页/文档时 0 次误写。
- 取词 A/B：官方应用成功率相差 ≤5 个百分点时默认 Hybrid；onClick 明显更高则改为 onClick；非 I-beam 拖拽误触发 0 次。
- 写回 A/B：粘贴成功率应 ≥ Unicode 输入；若粘贴失败率高，`auto` 的 paste→type 兜底必须验证；两者都不可靠时收窄为 paste-only + 复制。

### 12.5 结论分支

- **通过**：进入 M1，按本 spec 实施。
- **内存超标**：先换 WinForms，再评估 Rust；更新 spec。
- **UIA 覆盖不足**：扩大剪贴板兜底/调整官方支持清单；必要时提前做热键/兼容列表。
- **UIA 卡死严重**：评估“宿主 + 工作进程”架构（B），但只在证据充分时引入。
- **写回门槛未过**：v1.0 移除替换/插入，仅保留复制；写回顺延 v1.1，另立设计。
- **插入门槛未过但替换通过**：v1.0 只保留替换，插入顺延 v1.1。
- **取词 A/B 结论**：写入 `tools/Probe/RESULTS.md`；默认候选 Hybrid，若 onClick 明显更稳则切 onClick。
- **写回 A/B 结论**：验证 `auto` 的 paste→type 兜底；不可靠则收窄为 paste-only + 复制。
- 探针结果写入 `tools/Probe/RESULTS.md`，并回填本 spec 的测试结论。

---

## 13. 测试计划

### 13.1 单元测试（Core）

- `LanguagePolicy`：中文、英文、中英混合、日语（含假名）、韩语、代码、纯数字、空串、零宽字符。
- `TextNormalizer`：空白、换行、NBSP、零宽、首尾 trim、长度上限。
- `LruCache`：命中/淘汰/键隔离/清空。
- `ErrorMapper`：401/403/429/400/5xx/超时/空译文。
- `SelectionCoordinator`：捕获→按钮→翻译→结果；新选区取消；关闭取消；缓存命中；错误状态；快照竞态；Hybrid 的 pending 快照/点击取词；onClick 的点击取词。
- `WriteBackDecision`：只读/终端/管理员/无 RuntimeId/选区变化/窗口变化 → 禁用或中止；剪贴板非文本 → type 兜底；成功路径；取消。
- `SettingsStore`：默认值、损坏回退、未知字段保留、排除列表匹配。

### 13.2 Provider 契约测试

- 用 `HttpMessageHandler` mock：
  - 请求 JSON 包含 `translation_options` 顶层、`source_lang=auto`、`target_lang` 英文名。
  - `Authorization: Bearer` 正确。
  - 解析 `choices[0].message.content`。
  - 401/403/429/400/5xx/超时/畸形 JSON/空 content。
  - 重试只发生在网络/5xx；4xx 不重试。
  - 取消令牌生效。
- 不做真实 API 调用的自动化测试；真实 Key 手工验证。

### 13.3 手工 UI 矩阵

- 应用：官方支持清单 + 尽力而为清单。
- 选择：拖选单词/句子/跨行/反向/全选；双击/三击；热键选词；空选区。
- 取词 A/B：Hybrid/onClick；I-beam 与非 I-beam 拖拽；点击取词；UIA 失败时按钮仍出现；Ctrl+C 次数/剪贴板副作用。
- DPI/显示：100%/150%/200%；混合 DPI 双屏；显示器热插拔；分辨率变化。
- 焦点：来源窗口保持焦点；按钮点击不激活；结果卡点击激活；Esc/外部点击/滚轮/切窗口。
- 剪贴板：UIA 成功时剪贴板不变；UIA 失败时兜底开启/关闭；剪贴板含图片/文件时跳过；恢复成功/失败；密码管理器排除；终端默认不执行兜底，按 Ctrl+C 不会中断进程。
- 密码：浏览器密码框、Windows 凭据框、密码管理器。
- 热键：默认/自定义/冲突/暂停/恢复。
- 翻译：正常、429、401、断网、超时、超长、同语言、切换目标语言、重试、复制。
- 写回：可写输入框/编辑器替换、插入；只读网页/PDF 按钮置灰；终端按钮置灰；管理员窗口；剪贴板含图片/文件时走 type 兜底；剪贴板被占用；来源窗口/选区变化中止；同窗口切标签页/文档时 RuntimeId 不匹配并中止；卡片激活后写回；Ctrl+Z 撤销；替换后内容/光标正确。
- 写回方法：粘贴与 Unicode 输入两条路径分别验证；多行文本；IME/自动补全；粘贴失败时切换 type；两者都失败时复制。
- 托盘/单实例/开机启动/退出/卸载。

### 13.4 内存与句柄

- 冷启动 5 分钟；100 次划词；8 小时空闲；显示器休眠/唤醒。
- 记录专用工作集、句柄、GDI；无单调上升。
- 结果卡/按钮窗反复显示隐藏 500 次，无泄漏。

### 13.5 安装/卸载/升级

- 按用户安装（无管理员）；安装到 LocalAppData；开始菜单快捷方式。
- 卸载保留/删除设置两条路径；便携版不写注册表（除可选开机启动）。
- 未签名 SmartScreen 提示按 README 可绕过；SHA-256 校验。
- v1.0 手动更新：下载新版本覆盖安装；设置/密钥保留。

### 13.6 验收清单

- [ ] 官方应用捕获成功率 ≥95%，不抢焦点。
- [ ] 空载 ≤50MB；100 次翻译后 ≤60MB 且不增长。
- [ ] 按钮 p50 ≤300ms；结果 p50 ≤2s（参考）。
- [ ] 热键、剪贴板兜底、排除应用、密码跳过全部按设计工作。
- [ ] 替换/插入在可写目标成功率达标；只读/终端/错误窗口 0 次误写。
- [ ] 写回失败时复制译文，不重复写入。
- [ ] 取词 A/B 结论已回填；Hybrid/onClick 默认路径有数据支撑。
- [ ] 写回 `auto` 的 paste→type 兜底已验证；不可靠时已收窄。
- [ ] 401/429/断网/超长/同语言错误可理解、可恢复。
- [ ] 日志无正文、无 Key；无遥测。
- [ ] 安装/卸载/便携运行正常。
- [ ] 8 小时空闲无崩溃/泄漏。

---

## 14. 里程碑与工期

### 14.1 阶段

| 阶段 | 内容 | 估算（工作日） |
|---|---|---|
| M0 | 探针：UIA、浮窗、内存、热键/剪贴板、写回、取词/写回 A/B | 3–4 |
| M1 | WPF 壳、托盘、单实例、钩子、UIA L1/L2、按钮窗 | 3–5 |
| M2 | 热键、剪贴板兜底、排除应用、广泛兼容测试 | 3–5 |
| M2b | 写回：可写检测、替换/插入、焦点/选区校验、剪贴板恢复 | 3–5 |
| M3 | Qwen-MT Provider、结果卡、设置、首次引导、错误处理 | 2–3 |
| M4 | 安装器、便携版、README/隐私说明、手动更新 | 2–3 |
| M5 | 测试矩阵、内存/句柄、视觉打磨、发布 | 3–5 |
| **合计** | | **19–30** |

按全职、熟悉 C#/WPF 估算，约 **4–6 周**；新手 6–9 周。不含证书审核、macOS、v1.1 后端。50MB 门槛若迫使换栈，加 1–2 周；M0 写回门槛若未过，退回仅复制，工期回到约 3–5 周。

### 14.2 估算依据

- 单人开发；Windows-only；无后端；无 OCR；无自动更新；无代码签名。
- 包含安装器、首次引导、广泛兼容测试、隐私文档、写回验证、取词/写回 A/B。
- 最大变量：UIA 覆盖率、WPF 内存、剪贴板兜底兼容性、写回可写检测/选区保持、取词 A/B 结论。
- 日历工期取决于每天投入时长。

### 14.3 依赖

- 安装 .NET 10 SDK（当前机器只有 8.0.32 运行时）。
- 用户提供可用的 Qwen-MT API Key（用于真实联调）。
- 测试应用：Chrome/Edge/Firefox、Word、PDF 阅读器、IDE/终端/聊天应用。
- Windows 10 22H2 / Windows 11 测试环境；多显示器/不同 DPI 条件。
- 公开 GitHub 仓库（MIT）与 Releases（发布时）。

### 14.4 风险

| 风险 | 影响 | 概率 | 缓解 |
|---|---|---|---|
| UIA 在 Chrome/PDF 覆盖低于预期 | 高 | 中 | M0 先证伪；扩大兜底/调整官方清单 |
| UIA COM 卡死 | 高 | 中 | 专用线程+看门狗；超时跳过；必要时工作进程 |
| WPF 空载 >50MB | 高 | 中 | M0 测量；换 WinForms/Rust；不静默放宽 |
| 剪贴板兜底副作用（含终端 SIGINT） | 高 | 中 | 纯文本、序列号校验、终端默认排除、按应用关闭、首次告知 |
| 写回误粘到只读/错误窗口 | 高 | 中 | 写回前校验来源窗口、RuntimeId、选区；只对可写控件启用；失败即复制；M0 门槛 |
| 同窗口切标签页/文档，选区已变但 HWND 不变 | 高 | 中 | RuntimeId + 选区文本双重校验；不一致立即中止 |
| 插入时光标落点不准 | 高 | 中 | UIA 折叠选区；不支持则按失败处理；M0 单独门槛 |
| 写回剪贴板含非文本/被占用 | 中 | 中 | 剪贴板优先、Unicode 输入兜底；剪贴板锁+重试；不破坏非文本内容 |
| Unicode 输入兜底在 IME/自动补全下不稳定 | 中 | 中 | 输入仅兜底；M0 实测；失败即复制 |
| 取词 A/B 结论不稳定 | 中 | 中 | 以官方应用成功率为准；保留 Hybrid 默认；结果写入 RESULTS.md |
| I-beam 过滤漏掉自绘光标应用 | 中 | 低 | 系统拖拽阈值兜底；记录失败应用，必要时按应用放宽 |
| 焦点/DPI 边缘问题 | 中 | 中 | no-activate 规则+手工矩阵 |
| Qwen-MT 端点/模型变化 | 中 | 中 | 接口抽象、base URL 可覆盖、官方文档核实 |
| 未签名 SmartScreen/杀软误报 | 高 | 高 | 开源、SHA-256、README；v1.1 签名 |
| 范围蔓延 | 高 | 高 | 非目标清单；v1.1/v2 另立 spec |
| .NET 10 SDK 未安装 | 低 | 高 | 开发前安装；写入 README |

---

## 15. 发布

### 15.1 构建

- `dotnet publish src/FastTranslation.App -c Release -r win-x64 --self-contained true`。
- 不使用 single-file/trimming；输出目录交给 Inno Setup。
- 可选 ReadyToRun；以启动/内存实测决定。
- 生成 SHA-256 校验文件。

### 15.2 安装器

- Inno Setup 6；按用户安装；`PrivilegesRequired=lowest`。
- 安装目录 `%LOCALAPPDATA%\Programs\FastTranslation`。
- 开始菜单快捷方式；可选桌面图标；卸载器。
- 卸载询问是否删除 `%APPDATA%\FastTranslation` 配置与密钥。
- 安装器不写系统级注册表；开机启动用 HKCU Run（应用内开关）。

### 15.3 发布渠道与源码

- GitHub 公开仓库，MIT License；Releases 提供安装器、便携 zip、SHA-256。
- README：功能、支持等级、SmartScreen 说明、Key 获取步骤、隐私说明、构建步骤。
- 不做自动更新；设置页“检查更新”打开 Releases 页面。
- 若复用 MIT/Apache 代码，发布包附 `THIRD_PARTY_NOTICES`，保留版权声明并注明修改。
- v1.0 只提供 GitHub Releases；不承诺国内镜像。若发布后访问不可用，再单独决定镜像方案。

### 15.4 未签名说明

- 明确告知未签名、可能触发 SmartScreen/杀软；提供源码与校验值。
- 不诱导用户关闭杀软；只说明“更多信息 → 仍要运行”的系统流程。
- v1.1 面向大众前完成签名；签名后更新 README 与发布流程。

### 15.5 版本号

- SemVer；v1.0 beta：`1.0.0-beta.N`。
- 设置页/关于显示版本、构建时间、提交哈希（若有）。
- 破坏性配置变更提升 `schemaVersion` 并提供迁移。

---

## 16. 后续版本

### 16.1 v1.1 / v1.2（另立 spec）

- 代码签名（OV/EV，资格/价格实施前核实）。
- 自动更新（签名后）。
- 英文 UI。
- 若 v1.0 的 M0 写回门槛未过：替换/插入顺延至 v1.1，先发复制版；替换通过但插入未过则只补插入。
- 邀请制免费额度：桌面客户端 → 配额代理 → Qwen-MT。
- 代理前置条件：
  - 阿里云工单确认允许代理/代付/转售。
  - 隐私政策、数据保留、PIPL/GDPR 评估。
  - 60 RPM/25k–100k TPM 容量方案：提额、多 Workspace 或队列；免费额度总量封顶与 kill switch。
  - 防滥用：设备/账号配额、IP 限制、全局预算。
- 免费额度建议起点：每设备每月 50,000 字符，总量封顶；按 flash 上限成本约 0.13 元/设备/月，1,000 设备约 130 元/月（1 字符≈1 token、输出≈输入的上限口径）。
- v1.2 在邀请制数据、配额提额和成本模型成立后，再开放免费额度；不提前承诺。
- 长文本分段、流式输出、术语表/翻译记忆/领域提示。
- 兼容列表自动兜底、按应用排除的更好 UI。

### 16.2 v2

- macOS：Accessibility API（AXUIElement）+ NSPanel；需要 Mac 真机、辅助功能权限引导、签名/公证。
- OCR：Windows OCR/WinRT 或本地模型；注意内存与语言包。
- 浏览器扩展（可选）：浏览器内精确捕获，作为原生应用的补充。
- 多翻译提供方（Azure/DeepL/本地模型）。

### 16.3 候选但不承诺

- 翻译历史、生词本、朗读、截图翻译、划词搜索、快捷键面板。
- Ctrl+C+C 双击复制触发、按住键增量翻译（STranslate 已有）。
- 账号体系、付费订阅、团队策略。

---

## 17. 决策记录

| 日期 | 决策 | 理由 |
|---|---|---|
| 2026-10-22 | Windows v1.0，macOS v2 | 无 Mac 真机；两端捕获/浮窗 API 不共享 |
| 2026-10-22 | C# WPF + .NET 10 LTS，self-contained | UIA/Win32 接入成熟；.NET 8 于 2026-11-10 EOL |
| 2026-10-22 | 单进程 | 简单、低延迟；UIA/HTTP 后台线程隔离 |
| 2026-10-22 | 空载 ≤50MB 为硬门槛 | 产品存在的核心理由；超标换栈，不静默放宽 |
| 2026-10-22 | BYO Qwen-MT 直连，v1.0 无后端 | 零 API 成本、文本不经过开发者、可快速公开 |
| 2026-10-22 | 公开开发者 beta，未签名 | v1.0 面向能配置 Key 的技术用户；签名放 v1.1 |
| 2026-10-22 | 源码公开 MIT，GitHub Releases | 未签名 beta 的信任补偿；开发者向 |
| 2026-10-22 | 剪贴板兜底默认开，终端默认排除 | 覆盖面；纯文本/密码/排除应用/终端 SIGINT 防护/首次告知控制副作用 |
| 2026-10-22 | v1.0 支持替换/插入，仅可写控件，带 M0 门槛 | 写回是核心使用闭环；误粘风险高，必须校验窗口/选区；不达标仅复制 |
| 2026-10-22 | 取词采用 Hybrid + M0 A/B | STranslate 证明点击取词可行；UIA 快速路径减少剪贴板副作用，数据决定默认 |
| 2026-10-22 | 写回粘贴优先，Unicode 输入兜底 | 参考 STranslate InputHelper；避免非文本剪贴板导致写回不可用 |
| 2026-10-22 | 参考 STranslate / Text-Grab 的 MIT 模块 | 复用已验证的 Hook/剪贴板/UIA 细节，但保留我们的 RuntimeId/选区校验与 50MB 依赖纪律 |
| 2026-10-22 | 热键进入 v1.0 | 公开用户需要键盘选词与失败兜底 |
| 2026-10-22 | 目标语言自动双向 + 可切换 92 种 | 默认简洁，保留多语言能力 |
| 2026-10-22 | 单次 ≤5,000 字符，不做分段/流式 | 控制复杂度；v1.1 再扩展 |
| 2026-10-22 | 中文 UI v1.0 | 目标用户与 Qwen-MT 账号环境 |
| 2026-10-22 | 免费额度 v1.1 邀请制，另立 spec | 后端、合规、60 RPM、成本需单独设计 |

---

## 18. 参考

- [Qwen-MT API 参考（阿里云百炼）](https://help.aliyun.com/zh/model-studio/qwen-mt-api)
- [翻译能力（Qwen-MT）](https://help.aliyun.com/zh/model-studio/machine-translation)
- [qwen-mt-flash 模型页](https://help.aliyun.com/zh/model-studio/qwen-mt-flash)
- [qwen-mt-plus 模型页](https://help.aliyun.com/zh/model-studio/qwen-mt-plus)
- [qwen-mt-lite 模型页](https://help.aliyun.com/zh/model-studio/qwen-mt-lite)
- [阿里云百炼模型价格](https://help.aliyun.com/zh/model-studio/model-pricing)
- [.NET 支持政策](https://dotnet.microsoft.com/zh-cn/platform/support/policy)
- [UI Automation TextPattern 概述](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-textpattern-overview)
- [Shell_NotifyIcon](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyicona)
- [High DPI Desktop Application Development](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows)
- [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)
- [SetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setforegroundwindow)
- [ProtectedData（DPAPI）](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata)
- [Inno Setup](https://jrsoftware.org/isinfo.php)

---

## 19. 开源参考与复用

### 19.1 参考项目（2026-10-23 检索）

| 项目 | 栈 / License | 借鉴范围 |
|---|---|---|
| [STranslate](https://github.com/STranslate/STranslate) | C# / WPF / .NET 10 / MIT | 首选参考：鼠标 Hook、剪贴板取词、点击取词、热键/全屏、写回输出、并发取消 |
| [Text-Grab](https://github.com/TheJoeFin/Text-Grab) | C# / WPF / MIT | UIA 取词工具类：TextPattern/ValuePattern/Name、遍历、采样、去重、排除窗口 |
| [Translumo](https://github.com/ramjke/Translumo) | C# / Apache-2.0 | v2 的屏幕捕获、OCR、覆盖层 |
| [Pot](https://github.com/pot-app/pot-desktop) | Tauri / JS / GPL-3.0 / 已归档 | 仅借鉴插件化/跨平台 UX；不复制代码 |
| [Crow Translate](https://github.com/crow-translate/crow-translate) | C++ / Qt / GPL-3.0 / 已归档 | 仅借鉴轻量原生划词 UX；不复制代码 |
| [CopyTranslator](https://github.com/CopyTranslator/CopyTranslator) | TS / GPL-2.0 | 仅借鉴剪贴板优先 UX；不复制代码 |
| [TTime](https://github.com/InkTimeRecord/TTime) | Electron / Vue 3 | 划词/截图/OCR 交互参考；Electron 内存路线不符；复用前核实 License |
| [Easydict](https://github.com/tisfeng/Easydict) | Swift / macOS | macOS v2 的结果卡/词典/OCR 参考；复用前核实 License |

### 19.2 可复用模块

- **STranslate `ClipboardHelper`**：[源码](https://github.com/STranslate/STranslate/blob/main/src/STranslate/Helpers/ClipboardHelper.cs)
  - 序列号轮询、发送 Ctrl+C 前释放卡住的修饰键、多文本格式、剪贴板打开重试。
  - 适配时保留我们的备份/恢复、终端排除、密码跳过，并修正它“超时返回当前剪贴板”的旧内容风险。
- **STranslate `InputHelper`**：[源码](https://github.com/STranslate/STranslate/blob/main/src/STranslate/Helpers/InputHelper.cs)
  - 键盘输入与剪贴板粘贴两种输出；多行处理。用于写回 `auto` 的 type 兜底。
- **STranslate `MouseHookService`**：[源码](https://github.com/STranslate/STranslate/blob/main/src/STranslate/Services/MouseHookService.cs)
  - 专用 Hook 线程、事件串行投递、`SM_CXDRAG`/`SM_CYDRAG`、I-beam 光标过滤。
- **STranslate `MouseSelectionService`**：[源码](https://github.com/STranslate/STranslate/blob/main/src/STranslate/Services/MouseSelectionService.cs)
  - 图标模式与直接翻译模式的优先级、点击时取词；用于 M0 的 onClick 分支。
- **STranslate 输入触发文档**：[flow-input-trigger.md](https://github.com/STranslate/STranslate/blob/main/src/docs/flow-input-trigger.md)
  - 全屏热键暂停、冲突退避重试、取词失败回退、前台激活策略。
- **Text-Grab `UIAutomationUtilities`**：[源码](https://github.com/TheJoeFin/Text-Grab/blob/main/Text-Grab/Utilities/UIAutomationUtilities.cs)
  - Fast/Balanced/Thorough 遍历（2/6/12 层）、点/区域采样、去重、排除句柄、ValuePattern/Name 回退。
  - 仅借用遍历与取词细节；TextPattern 选区仍是主路径。
- **Translumo**：v2 OCR/覆盖层参考，v1.0 不引入。

以上只借逻辑/结构，不引入它们的 CommunityToolkit/DI/Serilog/SQLite/WindowsInput 等依赖；Unicode 输入用原生 `SendInput` 实现，保持 50MB 依赖纪律。

### 19.3 License 规则

- **MIT**（STranslate、Text-Grab）：可复用；保留版权与许可声明，附 `THIRD_PARTY_NOTICES`，注明修改。
- **Apache-2.0**（Translumo）：可复用；保留 NOTICE/许可，注明修改。
- **GPL-2.0 / GPL-3.0**（Pot、Crow、CopyTranslator）：只学思路，不复制代码；否则本项目会被 GPL 传染。
- **License 不明**（TTime、Easydict）：复用前先确认；未确认不复制。

### 19.4 不照搬的部分

- 不 fork 全量 STranslate：它的 MVVM/DI/Serilog/SQLite/OCR/插件依赖树与 50MB 目标冲突；只借具体模块并重写壳。
- 不照搬 STranslate 的“超时返回当前剪贴板”：可能把旧内容当选区；我们坚持序列号变化 + 失败恢复。
- 不照搬终端的 Ctrl+C 取词：终端默认排除，避免 SIGINT/命令注入。
- 不照搬无 RuntimeId 的写回：写回必须校验来源窗口、RuntimeId、选区文本三层。
