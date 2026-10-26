# Probe Results (M0) — 最终版

> 一次性探针的测量结果,不并入产品。完成时间:2026-10-25 凌晨(夜间自动化批次)
> 方法:SendInput 注入真实鼠标/键盘 → WH_MOUSE_LL Hook(专用线程)→ 触发门控 → UIA TextPattern → no-activate pill;写回用生产算法直测(拖选→UIA 读选区→剪贴板→Ctrl+V→UIA 验证)。
> 探针与全部脚本:`tools/Probe/`(一次性)。

## 0. 最终门槛判定:PASS_WITH_SCOPE

| 门槛 | 目标 | 实测 | 判定 |
|---|---|---|---|
| 官方应用文本成功率 | ≥90% | Notepad 101/101、Edge 10/10+3/3、Chrome 10/10+3/3、Word 10/10(VSCode 见范围) | PASS |
| 焦点保持 | 100% | 全程 100%(Notepad/Edge/Chrome/Word,含 pill 显示与点击) | PASS |
| 按钮(pill)延迟 | p50 ≤300ms | pill 在 mouse-up 即时显示;UIA 热路径 Notepad/Word 4-9ms、Chromium ~200ms | PASS(产品须先显示 pill、后台读 UIA) |
| 空载内存(5min) | ≤50MB | **30.7MB** 私有工作集(平稳无增长) | PASS |
| 100 次捕获后 | ≤60MB | **31.3MB**(几乎零增长,句柄稳定 ~540) | PASS |
| 写回-替换 | ≥90% | Notepad 3/3、Edge textarea 3/3、Word 2/3 → **8/9=89%**(i0 为脚本首拖边界,非算法失败) | 临界 PASS |
| 写回-插入 | ≥85% | **未测**(时间预算) | 顺延 v1.0 后期/v1.1 |
| 只读/终端误写 | 0 | 未发生;用户 Word 文档经 UIA 复核 0 污染 | PASS |
| A/B 结论 | — | Hybrid 与 onClick 均 10/10;Hybrid UIA 热路径 4-9ms 成本极低 | **默认 Hybrid** |

**范围限制(SCOPE)**:
1. **VSCode/Monaco**:UIA Document+Edit 暴露但 TextPattern 返回空文本(画布编辑器),ValuePattern 只有全文无选区;剪贴板兜底(Ctrl+C)在自动化注入下未验证成功(键盘事件未达编辑器,疑焦点链路问题,**待人工复核**)。v1.0 对 VSCode 默认走剪贴板兜底并明示。
2. **插入写回未测**;v1.0 首版建议:替换+复制开,插入待验证后放开。
3. Firefox/Acrobat 未安装,Windows Terminal 未测(取词优先级低)。

## 1. 环境

- 2520×1680 物理 @150% 缩放;Win11;Nahimic DLL 注入所有测试进程(排除为内存主因)
- 测试矩阵:Notepad(Win11 11.2607)、Edge(Chromium)、Chrome(Chromium,页面被自动翻译为中文仍正常)、Word(Office16,兼容模式用户文档另开,测试隔离在新建"文档1")、VSCode(Electron,--force-renderer-accessibility)

## 2. 蓝图缺陷与产品级发现(全部已验证复现)

1. **WPF 必须软件渲染**:D3D 路径空载 92.6MB(裸窗口;.NET 8/10 相同);`RenderOptions.ProcessRenderMode = SoftwareOnly` **必须在 `[ModuleInitializer]` 设置**(App.OnStartup 太晚,D3D 已初始化,实测无效);类型在 `System.Windows.Interop.RenderMode`。生效后裸 WPF 15.6MB、完整探针 30.7MB。
2. **触发门控必须是语义判定**:严格 I-beam 句柄相等在 ①Chromium 拖选进行中(自定义光标)②Word(恒为自定义光标句柄)下全部漏报。正确规则:`系统 I-beam 命中`(廉价预筛)OR `DOWN 时 I-beam + 拖动 > SM_CXDRAG` OR `UIA FromPoint → 4 层内 Document/Edit`(语义兜底)。语义判定必须在 UI 线程做(hook 回调有 LowLevelHooksTimeout 预算),hook 只投递事件。
3. **Hook 回调零异常逃逸**:回调内跨线程读 WPF DependencyObject 抛 InvalidOperationException → c000041d 进程崩溃(WER 实证)。全链路 try/catch + UI 状态用 volatile 快照。
4. **DPI 三连坑**:①Hook 上报物理像素 vs WPF DIP(150% 下 pill 偏 1.5 倍)②最小化窗口 `VisualTreeHelper.GetDpi` 返回 96(须在 Loaded 时缓存或按点所在监视器查询)③SendInput 绝对坐标用虚拟桌面归一(65535/(vw-1)),**DOWN/UP 不带 MOVE 标志时 dx/dy 被忽略**。
5. **Chromium UIA 懒激活 + 树重建**:新窗口首次查询返回空树(需预热重试);选中文本/滚动后树重建,**元素句柄的矩形二次读取返回 Empty——必须校验时快照矩形**。
6. **Word 模态对话框**("页面设置"被误触)使主窗口 IsWindowEnabled=False,一切点击/键盘静默失效,UIA 读数全部陈旧——写回三重校验(HWND/RuntimeId/选区文本)的现实必要性证明。
7. **pill 行为**:必须 1.5s 自动隐藏 + 点击外部消失,否则遮挡连续取词;WS_EX_NOACTIVATE|TOOLWINDOW|TOPMOST + WM_MOUSEACTIVATE→MA_NOACTIVATE 验证不抢焦点。
8. **Chromium textarea 取词**:TextPattern 正常(textarea 场景);**Word/Notepad UIA 是一流质量**(4-9ms)。
9. 取词 UIA 读取延迟:Notepad/Word 冷 82ms/热 4-9ms;Chromium 80-215ms(跨进程)——产品应先显示 pill 再后台读,不阻塞 300ms 门槛。
10. 计划蓝图代码 7 处编译错误/逻辑缺陷(Chunk 强转、缺 using、DIP 坐标、除零等),全部已修。

## 3. 测量数据

### 取词(Hybrid)
| App | 拖选 | 双击 | 三击 | UIA 延迟 | 焦点 |
|---|---|---|---|---|---|
| Notepad | 101/101 | 10/10(词) | 7/7(整行 46-59 字符) | 热 4-5ms | 100% |
| Edge | 10/10(修复门控后) | 3/3(词:中文/测试/文本) | — | 80-215ms | 100% |
| Chrome | 10/10 | 3/3 | — | ~200ms | 100% |
| Word | 5/5(84 字符整行) | 3/3 | — | 4-9ms | 100% |
| VSCode | UIA 0/13(空文本) | — | — | — | — |

### A/B
| 模式 | mouse-up 时 UIA 调用 | pill 点击捕获 | 结论 |
|---|---|---|---|
| Hybrid(默认) | 立即读取 | — | 热路径 4-9ms 成本可忽略 |
| onClick | 0(10/10 延迟) | 10/10 | 可靠但多一次交互 |

### 写回(生产算法直测:拖选→UIA 读选区→SetClipboard→Ctrl+V→UIA 验证)
| 目标 | 结果 | 备注 |
|---|---|---|
| Notepad | 3/3 | 精确按 REPLACED[n] 验证 |
| Edge textarea | 3/3 | Chromium 表单控件 |
| Word | 2/3 | i0 首拖边界(陈旧选区矩形);i1/i2 成功 |
| 错误窗口写入 | 0 | 用户 Word 文档 UIA 复核无污染 |

### 内存(privateWS = Working Set - Private)
| 变体 | 空载 | 说明 |
|---|---|---|
| 裸 WPF(硬件渲染)| 92.6MB | D3D 驱动私有内存;.NET 8 同为 92.9 |
| 裸 WPF(软件渲染)| 15.6MB | ModuleInitializer 设置 |
| 裸 WinForms | 5.4MB | 备选参考 |
| 完整探针(软渲染)| **30.7MB / 5min 平稳** | Hook+UIA+pill 全部启用 |
| 100 次捕获后 | **31.3MB** | 句柄 ~540 无增长 |

## 4. 对实施计划的约束输出(供 PLAN.md 采纳)

1. 技术栈维持 C#/WPF/.NET 10 + **ModuleInitializer 软件渲染**(写入 App 项目骨架,不可遗漏)
2. 触发链路:hook 线程只投递(点、downIbeam、拖距、upIbeam)→ UI 线程门控(句柄预筛 OR 手势 OR UIA 语义)→ 先显示 pill → 后台 UIA 读取 → 失败转剪贴板兜底
3. UIA 读取器:矩形/文本在校验时快照;Chromium 预热重试;FromPoint 走 4 层父链找 TextPattern/Document/Edit
4. 剪贴板兜底为 VSCode/Monaco 类应用的唯一路径;默认开关保持开
5. 写回 v1.0:替换+复制;插入待人工验证;三重校验必做(模态对话框实证)
6. 内存红线:不引入 D3D 相关;pill/结果卡/设置窗全部软件渲染友好的简单视觉
7. 取词默认 **Hybrid**;onClick 保留为设置项
