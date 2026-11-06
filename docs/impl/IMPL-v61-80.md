# Fast Translation 实施日志 · v61-80

> 从 v61 起。全文索引与「新条目该写进哪一册」的规则见 [../IMPL.md](../IMPL.md)。

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
