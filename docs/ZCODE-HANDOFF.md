# Fast Translation — 交接文档

- 更新时间:2026-10-25 02:45(夜间自主执行后)
- 当前阶段:**M0 完成 + v1.0 完成 + 真实 Key 联调通过 + 豆包式两段交互改造完成(工具条→翻译→结果卡,✕/插入/替换全部实测)**
- 项目目录:`D:\FTranslation`
- Git:仍未初始化、零提交(用户偏好:留工作区供审查)

---

## 0. 现在的状态(一段话)

2026-10-25 凌晨,用户授权夜间自主执行。已完成:①M0 探针全部测量(取词成功率/内存/写回/A-B,结论 **PASS_WITH_SCOPE**,数据在 `tools/Probe/RESULTS.md`);②`docs/PLAN.md`(执行计划,9 条 M0 实测决策 D1–D9)+ `docs/IMPL.md`(逐任务实施日志);③按计划实施 T1–T16 全部任务:四项目解决方案 + 两个测试项目,30/30 测试绿,`publish/win-x64/` 自包含发布,启动冒烟通过,**空载 35.2MB**。

## 1. 必读文件(按序)

1. 本文档
2. `tools/Probe/RESULTS.md` — M0 全部数据与 9 条产品级发现
3. `docs/PLAN.md` — 执行计划与决策依据
4. `docs/IMPL.md` — 逐任务实施日志(变更文件/验证/未验证项)
5. 原始蓝图(仅参考,已被 PLAN.md 修订):`docs/superpowers/plans/2026-10-23-fast-translation-v1.md`

## 2. 代码地图

```
FastTranslation.slnx
├─ src/FastTranslation.Core            Primitives/TextNormalization/LanguageCatalog(76 语言,待核92)/
│                                      Settings/TranslationService(LRU+单飞取消)/SelectionCoordinator
├─ src/FastTranslation.Platform.Windows NativeMethods/LowLevelMouseHook/WindowsSelectionCapture(门控)/
│                                      UiaSelectionReader(快照)/ClipboardService/OverlayWindows(pill+卡片)/
│                                      HotkeyService/TrayIconService/WriteBackService/DpapiSecretStore
├─ src/FastTranslation.Providers.QwenMt QwenMtProvider(协议/重试/错误分类)
├─ src/FastTranslation.App            SoftwareRender(ModuleInitializer!)/App(组合根)/SettingsWindow/OnboardingWindow
├─ tests/…                            30 用例:Core 24 + Provider 6(假 HttpListener)
└─ publish/win-x64/                   自包含发布产物(冒烟过)
```

## 3. 关键决策速查(M0 实测,详见 RESULTS.md §2)

1. **软件渲染 ModuleInitializer**(D3D 路径 92MB→软件 30MB;OnStartup 设置无效)
2. **触发门控语义化**(Chromium 拖选/Word 的光标句柄都不可靠;UIA FromPoint Document/Edit 兜底,UI 线程执行,hook 只投递)
3. **UIA 快照式读取**(Chromium 树重建,二次读取变空)
4. 取词默认 Hybrid;onClick 是设置项
5. 写回 v1.0:替换+复制;插入暂缓;三重校验(前台+RuntimeId+选区文本)
6. .NET 10 ProtectedData 内置 → **零 NuGet 依赖**
7. 剪贴板兜底默认开;终端永不兜底(Ctrl+C=SIGINT)

## 4. 下一步(按优先级)

1. ~~真实 Key 联调~~ **已完成**(2026-10-25 上午,Key 在环境变量 QIANWEN_API_KEY,已存入本机 DPAPI):Provider 级 609/235/2250ms 全通、应用级端到端真实翻译 688ms、复制按钮 ✓
2. 人工冒烟清单(15 分钟):真机拖选翻译(Notepad/Edge/Word/Chrome)、替换写回、热键、托盘菜单、深浅色(应用已可直接 `publish/win-x64/FastTranslation.exe` 启动,引导已跳过)
3. 92 语言清单与官方页 diff
4. 插入写回实测(算法在,入口未开)
5. 100 次翻译后内存复测(带真实 API)+ 8 小时稳定性
6. Inno Setup 安装包 + THIRD_PARTY_NOTICES(当前零第三方代码;许可 2026-11-02 起为 AGPL-3.0,此前为 MIT)
7. 决定后 `git init` + 首次提交(用户点头才做)

## 5. 遗留/风险

- VSCode 取词:自动化验证未成(键盘注入焦点问题),产品走剪贴板兜底,待人工复核
- 语言目录 76/92:差 16 个待补
- Word 首拖边界(i0 NOSEL)未根因,写回整体 8/9
- App 的 Onboarding"测试连接"调真实端点(无 Key 时预期失败并提示)
- TrayIcon 的 ContextMenu 在无激活上下文中的行为未实机验证

## 6. 红线(仍然有效)

不静默放宽 50MB;不向终端兜底/写回;三重校验;不记录选中文本/Key/剪贴板;零遥测;GPL 不抄;不自行 git;系统级变更先问(夜间已获一次性授权的除外,均已恢复)。

## 2026-10-25 晚:UI 重构 + v1.0.0 发布物(坷拉)
- 设计系统:App.xaml 品牌令牌(#2562FF 品牌蓝/#F7F9FC 奶白底)+ 全套控件模板,ThemeMode=Light Fluent
- 四界面(主窗/设置/引导/取词条/结果卡)全部重构,视觉 QA 9/10 定稿;UIA Name 兼容 E2E
- 打包:publish/dist/ 下单文件 exe(64MB)+ MSI 安装包(60MB,WiX v6,per-user)+ README;安装/卸载闭环实测
- 用户回归验收点:整体手感、划词工具条/结果卡实机观感、托盘图标新 logo
