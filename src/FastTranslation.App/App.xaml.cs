using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Windows;
using FastTranslation.App.Pages;
using FastTranslation.Core;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using FastTranslation.Core.Audio;
using FastTranslation.Core.Speech;
using FastTranslation.App.Speech;
using FastTranslation.Core.Account;
using FastTranslation.Core.Documents;
using FastTranslation.Core.Lexicon;
using FastTranslation.Core.Notes;
using FastTranslation.Platform.Windows;
using FastTranslation.Providers.QwenMt;

namespace FastTranslation.App;

public partial class App : Application, IResultPresenter
{
    private const string SingleInstanceMutexName = "FastTranslation.SingleInstance";

    private const string ActivateEventName = "Local/FastTranslation.Activate";

    private Mutex? _mutex;
    private MainWindow? _mainWindow;
    private bool _reallyExiting;
    private TrayIconService? _tray;
    private HotkeyService? _hotkey;
    private HotkeyService? _imageHotkey;
    private HotkeyService? _speechHotkey;
    private SpeechTranslationCoordinator? _speechCoordinator;
    private GatewaySpeechTransport? _speechTransport;
    private SpeechTranscriptService? _speechSubtitles;
    private WasapiLoopbackCapture? _speechCapture;
    private AudioResampler? _speechResampler;
    private SpeechWindow? _speechWindow;
    private WindowsSelectionCapture? _capture;
    private SelectionCoordinator? _coordinator;
    private SelectionToolbar? _toolbar;
    private ResultCardWindow? _card;
    private ImageResultWindow? _imageWindow;
    private ImageTranslationCoordinator? _imageCoordinator;
    private IImageTranslationProvider? _imageProvider;
    private IImageOcrProvider? _ocrProvider;
    private WriteBackService? _writeBack;
    private TranslationService? _translation;
    private SettingsService _settingsService = new();
    private AppSettings _settings = new();
    private readonly DpapiSecretStore _secrets = new();
    private bool _captureEnabled = true;
    private UpdateCoordinator? _updates;
    private AccountCoordinator? _account;
    private AccountApiClient? _accountClient;
    private NotesApiClient? _notesClient;
    private HttpClient? _gatewayHttp;
    private DocumentTranslationCoordinator? _documents;
    private GatewayTranslationProvider? _documentProvider;

    // v67: 网关能不能用只由后台探测决定（见 GatewayHealth）。原来的做法是「60 秒内别碰网关」，
    // 窗口一过，下一次翻译就当场去连一个没人监听的端口——在带过滤驱动/代理的机器上这类连接要
    // 两秒才拿到 RST，而它发生在 provider 计时之外，用户看到「100 多毫秒」却实际等了三秒。
    private readonly GatewayHealth _gatewayHealth = new();

    /// <summary>后台探测循环的取消源；退出时取消，别让探测把进程拖住。</summary>
    private readonly CancellationTokenSource _gatewayProbeCts = new();

    private CapturedSelection _lastSelection = CapturedSelection.Empty(default);
    private TranslationResult? _lastResult;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, SingleInstanceMutexName, out var isNew);
        if (!isNew)
        {
            // 第二次启动：唤醒已存在实例的窗口，而不是静默退出
            try
            {
                using var activate = EventWaitHandle.OpenExisting(ActivateEventName);
                activate.Set();
            }
            catch { }
            Shutdown();
            return;
        }
        var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        var activateListener = new Thread(() =>
        {
            while (! _reallyExiting)
            {
                if (!activateEvent.WaitOne(TimeSpan.FromMilliseconds(500))) continue;
                Dispatcher.BeginInvoke(ShowMainWindow);
            }
        })
        { IsBackground = true, Name = "FT.ActivateListener" };
        activateListener.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            System.Diagnostics.Debug.WriteLine($"UI unhandled: {args.Exception}");
            if (Environment.GetEnvironmentVariable("FT_UITEST") is not null)
            {
                try { System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "ft-uitest-errors.log", $"{DateTime.Now:HH:mm:ss.fff} {args.Exception}\n"); } catch { }
            }
            args.Handled = true; // 托盘应用绝不能因为一个坏事件就静默死掉
        };

        _settings = _settingsService.Load();

        // 调色板要在任何窗口存在之前就位：XAML 画刷是可变的（po:Freeze="False"），
        // ThemeBridge 就地给它们换色，于是每个样式和模板都能拿到主题（v24）
        ApplyAppearance(_settings.Theme, _settings.Accent);

        // 账号服务：整个进程共用一个 HttpClient，超时 20 秒，这样主机挂了会表现为
        // 网络错误，而不是一个永远回不来的按钮
        var accountBase = string.IsNullOrWhiteSpace(_settings.AccountBaseUrl)
            ? AccountApiClient.DefaultBaseUrl
            : _settings.AccountBaseUrl;
        _accountClient = new AccountApiClient(
            CreateApiHttpClient(TimeSpan.FromSeconds(20), accountBase),
            accountBase);
        _notesClient = new NotesApiClient(
            CreateApiHttpClient(TimeSpan.FromSeconds(20), accountBase),
            accountBase);
        _account = new AccountCoordinator(_accountClient, _secrets, notes: _notesClient);
        _account.Changed += OnAccountChanged;

        // 用户自己的 Key：既是不登录时的唯一链路，也是网关额度用完后的回落目标
        var ownProvider = new QwenMtProvider(
            () => _secrets.LoadSecret(QwenMtProvider.ApiKeySecretName),
            new QwenMtOptions
            {
                BaseUrl = string.IsNullOrWhiteSpace(_settings.OverrideBaseUrl)
                    ? new QwenMtOptions().BaseUrl
                    : _settings.OverrideBaseUrl,
            });

        // 网关与账号共用主机；单独一个 HttpClient，因为它的超时要盖住服务端自己的上游超时
        _gatewayHttp = CreateApiHttpClient(TimeSpan.FromSeconds(45), GatewayBaseUrl());

        // 本地词库缺失（旧安装包、手工删文件）不是错误：只是单英文词回到在线翻译，
        // 所以这里按可选依赖处理，静默降级。索引约 5.8 万条，读取在启动阶段完成，
        // 免得第一次划词时把索引加载时间算进用户等待里。
        var lexicon = EcdictLexicon.TryLoad(Path.Combine(AppContext.BaseDirectory, "dictionary", "ecdict.ftd"));
        if (lexicon is null) Trace("dictionary: ecdict.ftd not found, word lookup falls back to online translation");
        _translation = new TranslationService(new GatewayTranslationProvider(GatewayTranslateAsync, ownProvider), lexicon);

        // v72: 文档翻译单独一条 provider 链，**不经过 TranslationService**——那个服务每次新请求会
        // 取消上一次在途请求（划词时"最后一次输入赢"），而文档是一份文档几十段并发在飞，
        // 走它就会变成每段互相打断、只有最后一段能译出来。这里直接用组合 provider：
        // 网关优先、失败回落用户自己的 Key，和划词是同一条策略，只是少了缓存与单飞。
        _documentProvider = new GatewayTranslationProvider(GatewayTranslateAsync, ownProvider);

        // 作业的三个回调转给主窗口再转给文档页：窗口可能正收在托盘里，作业照样要能跑完。
        _documents = new DocumentTranslationCoordinator(
            format => format switch
            {
                DocumentFormat.Docx => new DocxDocumentTranslator(),
                _ => new TextDocumentTranslator(format),
            },
            TranslateDocumentSegmentsAsync,
            // 输出目录给 null = 落在源文件旁边，由 coordinator 自己取目录
            outputDirectory: () => null,
            present: result => _mainWindow?.PresentDocument(result),
            reportError: message => _mainWindow?.ReportDocumentError(message),
            progress: (fraction, done, total) => _mainWindow?.ReportDocumentProgress(fraction, done, total));

        // 第一阶段工具栏（复制/翻译）+ 第二阶段结果卡（复制/替换/插入/✕）
        _toolbar = new SelectionToolbar();
        _toolbar.CopyClicked += OnToolbarCopy;
        _toolbar.TranslateClicked += OnToolbarTranslate;
        _toolbar.ScreenshotClicked += _ => RunScreenshotTranslation();
        _card = new ResultCardWindow();
        _card.CopyRequested += OnCopyRequested;
        _card.ReplaceRequested += OnReplaceRequested;
        _card.InsertRequested += OnInsertRequested;
        _card.SaveRequested += OnSaveRequested;

        _capture = new WindowsSelectionCapture(
            Dispatcher,
            fallbackEnabled: () => _settings.ClipboardFallbackEnabled && _captureEnabled,
            log: m => System.Diagnostics.Debug.WriteLine(m),
            isExcluded: processName => _settings.ExcludedProcesses
                .Any(n => string.Equals(n, processName, StringComparison.OrdinalIgnoreCase)));
        _writeBack = new WriteBackService(_capture.Reader);
        // 日志落到 %TEMP%\ft-uitest-errors.log：卡片上的「翻译失败,请重试」是兜底文案，
        // 没有这行日志就查不出到底哪一步抛了异常
        _coordinator = new SelectionCoordinator(_capture, this, _translation, () => _settings, Trace);
        _capture.SelectionToolbarRequested += captured =>
        {
            if (!_captureEnabled) return;
            _card?.Hide();
            _toolbar?.ShowAt(captured);
        };
        _capture.RawMouseDown += OnRawMouseDown; // 点击外部时收起工具栏与结果卡
        try
        {
            _capture.Start();
            FastTranslation.Platform.Windows.WindowsSelectionCapture.Diag("hook started");
        }
        catch (InvalidOperationException)
        {
            _tray?.ShowBalloon("Fast Translation", "全局鼠标钩子安装失败,取词不可用。");
        }

        _hotkey = new HotkeyService();
        _hotkey.Pressed += OnHotkey;
        if (!_hotkey.Register(_settings.Hotkey))
        {
            _settings.Hotkey = "Ctrl+Alt+T";
            // 真正把兜底热键注册上——以前这里只改了标签，
            // 结果一个热键都没生效（bugbot P2）
            _hotkey.Register(_settings.Hotkey);
        }

        _tray = new TrayIconService(LoadIconBytes());
        _tray.SettingsRequested += ShowSettings;
        _tray.CaptureToggled += enabled =>
        {
            _captureEnabled = enabled;
            if (!enabled) { _toolbar?.Hide(); _card?.Hide(); }
            _tray!.SetTooltip(enabled ? "Fast Translation — 取词已开启" : "Fast Translation — 已暂停");
            _mainWindow?.RefreshCaptureState();
        };
        _tray.MainWindowRequested += ShowMainWindow;
        _tray.UpdateRequested += () => _updates?.CheckAsync(interactive: true);
        _tray.AccountRequested += ShowAccount;
        _tray.NotesRequested += ShowNotes;
        _tray.ExitRequested += ExitApp;
        _tray.Show("Fast Translation — 取词已开启");

        // 截屏翻译走独立的图片通道：端点、模型、返回（译图）都和文本翻译不同，
        // 所以不复用 _translation；key 仍然读同一个 secret，和文本 provider 完全一致。
        var ownImageProvider = new QwenMtImageTranslator(
            () => _secrets.LoadSecret(QwenMtProvider.ApiKeySecretName));
        _imageProvider = new GatewayImageTranslationProvider(GatewayTranslateImageAsync, ownImageProvider);

        // OCR 是 v48 新增的第二条图片链路：同一次框选要同时拿到「译图」和「原文」，
        // 所以它是独立 provider，不塞进图片翻译的返回值里（两条路各有各的重试与计费）。
        var ownOcr = new QwenOcrClient(() => _secrets.LoadSecret(QwenMtProvider.ApiKeySecretName));
        _ocrProvider = new GatewayOcrProvider(GatewayRecognizeImageAsync, ownOcr);

        _imageWindow = new ImageResultWindow();
        _imageWindow.SaveRequested += draft => SaveScreenshotText(draft);
        // 协调器在 Task.WhenAll(...).ConfigureAwait(false) 之后继续在线程池线程上跑，
        // 直接 ShowResult 会抛「调用线程无法访问此对象」然后被 catch 吞成托盘气泡，
        // 用户看到的就是「截图翻译没反应」。WPF 的边界只在这里，所以在这里切回 UI 线程。
        _imageCoordinator = new ImageTranslationCoordinator(
            _imageProvider,
            _ocrProvider,
            _translation,
            () => _settings,
            result => OnUiThread(() => _imageWindow?.ShowResult(
                result.JpegBytes,
                CaptureCaption(result),
                result.SourceText.Length > 0 ? result.SourceText : null,
                showActions: result.JpegBytes is not null,
                new ImageNoteContext(
                    result.TargetText ?? string.Empty,
                    result.SourceLanguage,
                    result.TargetLanguage,
                    result.Model),
                result.PicturePending)),
            message => OnUiThread(() => _tray?.ShowBalloon("截图翻译失败", message)),
            // 框选一结束就把浮窗开成等待态：译图 + OCR + 文字翻译三段串起来要好几秒
            () => OnUiThread(() => _imageWindow?.ShowLoading("截图翻译 · 正在翻译")));
        _tray.ScreenshotTranslateRequested += RunScreenshotTranslation;
        _tray.SpeechRequested += RunSpeechTranslation;

        // 第二个热键服务：HotkeyService 每个实例有自己的 message-only 窗口，
        // 取词热键(Register/重注册)完全不受影响。
        // 失败提示必须排在 _tray.Show() 之后：ShowBalloon 在图标还没添加时直接返回。
        _imageHotkey = new HotkeyService();
        _imageHotkey.Pressed += RunScreenshotTranslation;
        if (!_imageHotkey.Register(_settings.ImageHotkey))
        {
            _tray.ShowBalloon("Fast Translation", $"截图翻译热键 {_settings.ImageHotkey} 被占用,请到设置里换一个");
        }

        // 第三个热键实例：HotkeyService 每个实例自建 message-only 窗口，(hwnd,id) 互不冲突
        _speechHotkey = new HotkeyService();
        _speechHotkey.Pressed += RunSpeechTranslation;
        if (!_speechHotkey.Register(_settings.SpeechHotkey))
        {
            _tray.ShowBalloon("Fast Translation", $"语音翻译热键 {_settings.SpeechHotkey} 被占用,请到设置里换一个");
        }

        _mainWindow = new MainWindow(
            _translation,
            _account!,
            () => _settings,
            _settingsService,
            _secrets,
            _hotkey!,
            _documents!,
            enabled =>
            {
                _captureEnabled = enabled;
                _tray?.SetTooltip(enabled ? "Fast Translation — 取词已开启" : "Fast Translation — 已暂停");
            },
            () => _captureEnabled,
            () => _updates?.CheckAsync(interactive: true));
        _mainWindow.SettingsRequested += () => ShowPage(MainPageId.Settings);
        _mainWindow.ScreenshotRequested += RunScreenshotTranslation;
        _mainWindow.SpeechRequested += RunSpeechTranslation;
        _mainWindow.Closing += (_, args) =>
        {
            if (_reallyExiting) return;
            args.Cancel = true;      // 关闭按钮是收进托盘；真正退出在 托盘 → 退出
            _mainWindow.Hide();
        };
        _mainWindow.Show();

        if (!_settings.FirstRunCompleted)
        {
            // 引导流程的意义就是收集 API Key；一旦有了 Key，就绝不能再打扰用户
            var hasKey = false;
            try { hasKey = !string.IsNullOrWhiteSpace(_secrets.LoadSecret(Providers.QwenMt.QwenMtProvider.ApiKeySecretName)); }
            catch { }
            if (hasKey)
            {
                _settings.FirstRunCompleted = true;
                _settingsService.Save(_settings);
            }
            else
            {
                ShowOnboarding();
            }
        }

        // 在后台恢复已存储的会话：网络不通不能拖慢启动，
        // 屏幕上也没有任何东西会等它
        _ = RestoreAccountAsync();
        _ = Task.Run(RunGatewayProbeLoopAsync);

        // 更新器放最后：它只调度一个定时器，而在 UI 存在之前跑的检查，
        // 弹窗会无处可放
        _updates = new UpdateCoordinator(
            Dispatcher,
            PresentUpdate,
            // 手动检查必须看得见：Win11 的通知设置会吞掉托盘气泡。
            // 用主题化的窗口而不是 MessageBox——原生那个不认调色板（v38）
            ShowNotice,
            ExitApp);
        _updates.Start();

        // 启动预热（用户反馈：应用可用之前有一段呆滞期）：
        // 1）后台线程做 UIA 客户端冷初始化  2）预先创建浮层 HWND
        System.Threading.Tasks.Task.Run(() =>
        {
            try { UiaSelectionReader.Warmup(); } catch { }
        });
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            try { _toolbar?.Prewarm(); _card?.Prewarm(); } catch { }
        });

        // QA 钩子：FT_UITEST=dropdown -> 启动后自动展开语言下拉框
        //（由环境变量控制，桌面繁忙时不需要注入输入事件）
        var uitest = Environment.GetEnvironmentVariable("FT_UITEST");
        if (uitest == "caret")
        {
            var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            uiTimer.Tick += (_, _) =>
            {
                uiTimer.Stop();
                try
                {
                    var box = _mainWindow?.PageHome.InputBox;
                    if (box is null) return;
                    box.Focus();
                    System.Windows.Input.Keyboard.Focus(box);
                    var rect = box.GetRectFromCharacterIndex(0);
                    var hint = _mainWindow?.PageHome.FindName("InputHint") as System.Windows.Controls.TextBlock;
                    var hx = hint is null ? -1 : hint.PointToScreen(new Point(0, 0)).X;
                    var hy = hint is null ? -1 : hint.PointToScreen(new Point(0, 0)).Y;
                    var bx = box.PointToScreen(new Point(0, 0));
                    var log = string.Format("caret idx0: x={0:F1} y={1:F1} h={2:F1} fontsize={3} padding={4} | boxAbs={5:F0},{6:F0} | hintAbs={7:F0},{8:F0} hintFs={9} margin={10}",
                        rect.X, rect.Y, rect.Height, box.FontSize, box.Padding, bx.X, bx.Y, hx, hy,
                        hint is null ? -1 : hint.FontSize, hint is null ? "null" : hint.Margin.ToString());
                    System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "ft-uitest-errors.log", log);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"uitest: {ex}"); }
            };
            uiTimer.Start();
        }
        else if (uitest == "hittest")
        {
            // 确定性的点击路径探针：真正的鼠标会命中哪个元素？
            var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            uiTimer.Tick += (_, _) =>
            {
                uiTimer.Stop();
                try
                {
                    var combo = _mainWindow?.PageHome.TargetCombo;
                    if (combo is null) return;
                    combo.UpdateLayout();
                    var pt = new Point(combo.ActualWidth * 0.5, combo.ActualHeight * 0.5);
                    var hit = combo.InputHitTest(pt) as System.Windows.Media.Visual;
                    var desc = hit?.ToString() ?? "(null)";
                    var depth = 0;
                    var walker = hit;
                    while (walker is not null && depth < 6)
                    {
                        desc += " <- " + walker.GetType().Name;
                        walker = System.Windows.Media.VisualTreeHelper.GetParent(walker) as System.Windows.Media.Visual;
                        depth++;
                    }
                    System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "ft-uitest-errors.log",
                        $"InputHitTest({pt}): {desc}\n");

                    var toggle = combo.Template?.FindName("PART_Toggle", combo) as System.Windows.Controls.Primitives.ToggleButton;
                    var tlog = toggle is null
                        ? "toggle NOT FOUND in template"
                        : $"toggle size={toggle.ActualWidth}x{toggle.ActualHeight} visible={toggle.IsVisible} hittest={toggle.IsHitTestVisible}";
                    System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "ft-uitest-errors.log", tlog + "\n");
                    if (toggle?.Parent is System.Windows.Controls.Grid g)
                    {
                        foreach (dynamic c in g.Children)
                        {
                            System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "ft-uitest-errors.log",
                                $"  gridchild {c.GetType().Name}: {c.ActualWidth}x{c.ActualHeight} hit={c.IsHitTestVisible}\n");
                        }
                    }
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"uitest: {ex}"); }
            };
            uiTimer.Start();
        }
        else if (uitest == "dropdown")
        {
            var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            uiTimer.Tick += (_, _) =>
            {
                uiTimer.Stop();
                try { _mainWindow?.PageHome.TargetCombo.IsDropDownOpen = true; }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"uitest: {ex}"); }
            };
            uiTimer.Start();
        }
        else if (uitest == "overlays")
        {
            var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            uiTimer.Tick += (_, _) =>
            {
                uiTimer.Stop();
                try
                {
                    _toolbar?.ShowAt(CapturedSelection.Empty(new ScreenPoint(200, 180)));
                    // 走 presenter，而不是直接调卡片：保存处理读的是
                    // _lastResult/_lastSelection，只有 ShowCard() 才会记录它们
                    ShowCard(new Core.TranslationResult(
                        "the quick brown fox jumps over the lazy dog and keeps running",
                        "那只敏捷的棕色狐狸跳过了那只懒惰的狗,然后继续在草地上奔跑。",
                        "auto", "Chinese", "qwen-mt-flash", 531, false),
                        CapturedSelection.Empty(new ScreenPoint(200, 260)));
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"uitest: {ex}"); }
            };
            uiTimer.Start();
        }
        else if (uitest == "overlays-fallback")
        {
            // FT_UITEST=overlays-fallback -> 结果卡的状态行要能同时容纳 token 用量和「回落」说明，
            // 420 宽度下会换行；这张卡是设置里没有截图的唯一形态，靠 UIA 文本核对。
            var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            uiTimer.Tick += (_, _) =>
            {
                uiTimer.Stop();
                try
                {
                    ShowCard(new Core.TranslationResult(
                        "the quick brown fox jumps over the lazy dog",
                        "那只敏捷的棕色狐狸跳过了那只懒惰的狗。",
                        "auto", "Chinese", "qwen-mt-flash", 486, false)
                    {
                        Usage = new Core.TokenUsage(56, 9),
                        Notice = "本月额度已用完,这次用了你自己的 Key",
                    },
                        CapturedSelection.Empty(new ScreenPoint(200, 260)));
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"uitest: {ex}"); }
            };
            uiTimer.Start();
        }
        else if (uitest is "notice-confirm")
        {
            // 双按钮通知的形态（更新流程已经无法再走到 Confirm()）
            var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            uiTimer.Tick += (_, _) =>
            {
                uiTimer.Stop();
                _ = Dispatcher.BeginInvoke(() => NoticeWindow.Confirm(_mainWindow,
                    "确认操作", "这是一个带「取消」与「继续」的提示窗口,只用来核对按钮形态与归属窗口。"));
            };
            uiTimer.Start();
        }
        else if (uitest is "notice-settings")
        {
            // FT_UITEST=notice-settings -> 打开设置页，然后按手动检查的方式
            // 触发通知：它必须落在发起检查的那个页面上，而不是下面的窗口
            var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
            uiTimer.Tick += (_, _) =>
            {
                uiTimer.Stop();
                var notice = UpdateCheckNotice.UpToDate(new Core.ReleaseVersion(1, 2, 0, 0));
                _ = Dispatcher.BeginInvoke(() =>
                {
                    try { ShowNotice(notice); }
                    catch (Exception ex) { Trace($"notice show: {ex}"); }
                });
            };
            uiTimer.Start();
            _ = Dispatcher.BeginInvoke(new Action(ShowSettings));
        }
        else if (uitest is "notice" or "notice-link")
        {
            // FT_UITEST=notice[-link] -> 打开更新通知，好让截图证明对话框跟随
            // 调色板；在 QA 机器上没人会真的点一次更新检查
            var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            uiTimer.Tick += (_, _) =>
            {
                uiTimer.Stop();
                try
                {
                    var notice = uitest == "notice"
                        ? UpdateCheckNotice.UpToDate(new Core.ReleaseVersion(1, 2, 0, 0))
                        : UpdateCheckNotice.Failed(UpdateChannel.ReleasesPageUrl);
                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        try { ShowNotice(notice); }
                        catch (Exception ex) { Trace($"notice show: {ex}"); }
                    });
                }
                catch (Exception ex) { Trace($"notice branch: {ex}"); }
            };
            uiTimer.Start();
        }
        else if (uitest == "live-dark")
        {
            // QA：浅色启动后在同一个进程里切深色 —— 验证主题与原生标题栏都不用重启（v84）
            var switchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
            switchTimer.Tick += (_, _) =>
            {
                switchTimer.Stop();
                ApplyAppearance(ThemePalette.DarkSetting, ThemePalette.ElectroSetting);
            };
            switchTimer.Start();
        }
        else if (uitest is "wordcard")
        {
            // FT_UITEST=wordcard -> 词典卡的截图入口。走真实的 TranslateAsync，
            // 顺带验证「单英文词命中本地词库」这条链路；手搓 TranslationResult 证明不了它。
            var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            uiTimer.Tick += async (_, _) =>
            {
                uiTimer.Stop();
                try
                {
                    var origin = CapturedSelection.Empty(
                        FastTranslation.Platform.Windows.WindowsSelectionCapture.CurrentCursorPoint());
                    ShowLoading("charge", origin);
                    var result = await _translation.TranslateAsync("charge", _settings);
                    Trace($"wordcard: ok={result.Succeeded} model={result.Model} dictionary={result.Dictionary is not null}");
                    ShowCard(result, origin);
                }
                catch (Exception ex) { Trace($"wordcard: {ex}"); }
            };
            uiTimer.Start();
        }
        else if (uitest is "translate")
        {
            // FT_UITEST=translate -> 连续跑三次真实链路（先 ShowLoading 再 TranslateAsync 再
            // ShowCard，和生产路径同序），每次用不同文本绕开 LRU 缓存。第一次与后面几次的
            // 差值就是启动代价（TLS 握手、JIT、JSON 首次序列化、DPAPI 解密都算在里面）。
            // FT_UITEST_DELAY 可以把这次测量推迟到任意毫秒之后，用来复现「空闲一段时间后再翻译」
            // 的场景（网关可用性判断只有在隔了几十秒之后才会走错）。
            var delayMs = int.TryParse(Environment.GetEnvironmentVariable("FT_UITEST_DELAY"), out var parsedDelay) && parsedDelay > 0
                ? parsedDelay
                : 1800;
            var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
            uiTimer.Tick += async (_, _) =>
            {
                uiTimer.Stop();
                string[] samples =
                [
                    "The quick brown fox jumps over the lazy dog.",
                    "Translation latency is what this probe measures.",
                    "Warm connections are why the second call is quicker.",
                ];
                for (var i = 0; i < samples.Length; i++)
                {
                    var origin = CapturedSelection.Empty(
                        FastTranslation.Platform.Windows.WindowsSelectionCapture.CurrentCursorPoint());
                    var wall = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var loadStart = wall.ElapsedMilliseconds;
                        ShowLoading(samples[i], origin);
                        var loadEnd = wall.ElapsedMilliseconds;
                        var result = await _translation.TranslateAsync(samples[i], _settings);
                        var callEnd = wall.ElapsedMilliseconds;
                        Trace($"translate hook [{i}]: loading={loadEnd - loadStart}ms call={callEnd - loadEnd}ms provider={result.ElapsedMs}ms ok={result.Succeeded} cache={result.FromCache} err={result.Error?.Code}/{result.Error?.Message}");
                        ShowCard(result, origin);
                        wall.Stop();
                        Trace($"translate hook [{i}]: card={wall.ElapsedMilliseconds - callEnd}ms total={wall.ElapsedMilliseconds}ms");
                    }
                    catch (Exception ex)
                    {
                        wall.Stop();
                        Trace($"translate hook [{i}] threw after {wall.ElapsedMilliseconds}ms: {ex}");
                    }
                }
            };
            uiTimer.Start();
        }
        else if (uitest == "nav")
        {
            // FT_UITEST=nav -> 量「点侧栏换页」的耗时。每一页跑两遍：第一遍要构造页面并预热模板，
            // 第二遍走 _pages 缓存。call 是 ShowPage 本身花的时间，frame 是它之后第一帧画完的时间。
            // v86 改性能前后各跑一次，数字进 docs/IMPL.md。
            var navTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
            navTimer.Tick += async (_, _) =>
            {
                navTimer.Stop();

                // FT_UITEST_SIZE=1456x914 -> 先把窗口改成目标尺寸再量：默认 1040 窗口可用宽只够单列，
                // 全屏时是两列，布局成本不同，不先调过去量出来的数偏低。
                var sizeSetting = Environment.GetEnvironmentVariable("FT_UITEST_SIZE");
                if (!string.IsNullOrEmpty(sizeSetting))
                {
                    var parts = sizeSetting.Split('x');
                    if (parts.Length == 2
                        && double.TryParse(parts[0], out var windowWidth)
                        && double.TryParse(parts[1], out var windowHeight))
                    {
                        _mainWindow!.WindowState = System.Windows.WindowState.Normal;
                        _mainWindow.Width = windowWidth;
                        _mainWindow.Height = windowHeight;
                        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                    }
                }

                MainPageId[] order =
                [
                    MainPageId.Home, MainPageId.Word, MainPageId.Phrase, MainPageId.Sentence, MainPageId.Review,
                    MainPageId.Document, MainPageId.Speech, MainPageId.Plan, MainPageId.Account, MainPageId.Settings,
                ];
                for (var pass = 0; pass < 2; pass++)
                {
                    foreach (var id in order)
                    {
                        var wall = System.Diagnostics.Stopwatch.StartNew();
                        _mainWindow!.ShowPage(id);
                        var call = wall.ElapsedMilliseconds;
                        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                        Trace($"nav pass={pass} page={id} call={call}ms frame={wall.ElapsedMilliseconds - call}ms");
                        await Task.Delay(150);
                    }
                }
            };
            navTimer.Start();
        }
        else if (uitest is "image-result")
        {
            // FT_UITEST=image-result -> 用一张合成截图打开截屏翻译卡片。
            // 真实路径从全屏框选浮层开始，那是一个分层窗口：PrintWindow 只能拍到
            // 一片黑，所以 QA 机器永远没法透过它给卡拍照。真正需要证据的是卡片本身
            //（四个胶囊 + 可编辑的 OCR 文本）。
            var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            uiTimer.Tick += (_, _) =>
            {
                uiTimer.Stop();
                try
                {
                    _imageWindow?.ShowResult(
                        BuildSampleCapture(),
                        "截图翻译 · 3 行 · qwen-mt-image",
                        "快速的棕色狐狸跳过了懒狗。\nThe quick brown fox jumps over the lazy dog.\n请把这句话翻译成英文,谢谢。",
                        true,
                        new ImageNoteContext("The quick brown fox jumped over the lazy dog.", "Chinese", "English", "qwen-mt-flash"));
                }
                catch (Exception ex) { Trace($"image-result: {ex}"); }
            };
            uiTimer.Start();
        }
        else if (uitest is "image-saved")
        {
            // FT_UITEST=image-saved -> 保存成功后的窗内回执（按钮变「已保存」+ 右侧一行状态）
            var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            uiTimer.Tick += (_, _) =>
            {
                uiTimer.Stop();
                try
                {
                    _imageWindow?.ShowResult(
                        BuildSampleCapture(),
                        "截图翻译 · 3 行 · qwen-mt-image",
                        "快速的棕色狐狸跳过了懒狗。\nThe quick brown fox jumps over the lazy dog.\n请把这句话翻译成英文,谢谢。",
                        true,
                        new ImageNoteContext("The quick brown fox jumped over the lazy dog.", "Chinese", "English", "qwen-mt-flash"));
                    _imageWindow?.MarkSaved("单词");
                }
                catch (Exception ex) { Trace($"image-saved: {ex}"); }
            };
            uiTimer.Start();
        }
        else if (uitest is "image-loading")
        {
            // FT_UITEST=image-loading -> 只开等待态，用来核对「转圈 + 正在快马加鞭翻译~」
            var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            uiTimer.Tick += (_, _) =>
            {
                uiTimer.Stop();
                try { _imageWindow?.ShowLoading("截图翻译 · 正在翻译"); }
                catch (Exception ex) { Trace($"image-loading: {ex}"); }
            };
            uiTimer.Start();
        }
        else if (uitest is "image-partial")
        {
            // FT_UITEST=image-partial -> 文字先到、译图还没回来的中间态：
            // 图区继续转圈占位，文本区已经能改能存。用来核对「先出文字不抖版面」。
            var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            uiTimer.Tick += (_, _) =>
            {
                uiTimer.Stop();
                try
                {
                    _imageWindow?.ShowResult(
                        null,
                        "截图翻译 · 正在生成译图",
                        "快速的棕色狐狸跳过了懒狗。\nThe quick brown fox jumps over the lazy dog.",
                        showActions: false,
                        new ImageNoteContext("The quick brown fox jumped over the lazy dog.", "Chinese", "English", "qwen-mt-flash"),
                        picturePending: true);
                }
                catch (Exception ex) { Trace($"image-partial: {ex}"); }
            };
            uiTimer.Start();
        }
        else if (uitest is "document" or "document-progress" or "document-done" or "document-failed")
        {
            // FT_UITEST=document* -> 文档页的四种形态，只为了拍对比截图。空态走真实页面，
            // 另外三种直接把回调喂给页面：截图不该等一次真实的文档翻译，也不该消耗额度。
            var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            uiTimer.Tick += (_, _) =>
            {
                uiTimer.Stop();
                try
                {
                    ShowDocument();
                    if (uitest == "document") return;

                    _mainWindow?.ReportDocumentProgress(0.35, 42, 120);
                    if (uitest == "document-progress") return;

                    List<DocumentSegment> failed = uitest == "document-failed"
                        ?
                        [
                            new DocumentSegment(17, "/word/document.xml#p18",
                                "这一段里有 ⟦1⟧ 之类的受保护片段，模型没能原样还回来。", null, SegmentStatus.Failed),
                            new DocumentSegment(64, "/word/document.xml#p65",
                                "To be translated — the gateway refused this segment three times.", null, SegmentStatus.Failed),
                        ]
                        : [];
                    _mainWindow?.PresentDocument(new DocumentJobResult(
                        @"C:\Users\Public\Documents\季度报告.en.docx", 120, failed.Count, 8_432)
                    { Failed = failed });
                }
                catch (Exception ex) { Trace($"uitest document: {ex}"); }
            };
            uiTimer.Start();
              }
        else if (uitest is "speech" or "speech-window" or "speech-window-failed" or "speech-window-paused")
        {
            // FT_UITEST=speech* -> 语音页与字幕浮窗的对比形态。不采集、不联网、不烧额度：
            // 浮窗直接喂演示行，failed/paused 形态只换头部状态。
            var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            uiTimer.Tick += (_, _) =>
            {
                uiTimer.Stop();
                try
                {
                    if (uitest == "speech")
                    {
                        ShowPage(MainPageId.Speech);
                        return;
                    }

                    _speechWindow ??= new SpeechWindow();
                    _speechWindow.Closed += (_, _) => { };
                    _speechWindow.BeginSession();
                    if (uitest == "speech-window-failed")
                    {
                        _speechWindow.EndSession("无法连接语音服务,请稍后再试");
                        return;
                    }
                    var show = _settings.SpeechShowSource;
                    var max = _settings.SpeechMaxSentences;
                    _speechWindow.ShowLine(new SpeechTranscriptLine(1, "And that brings us to the quarterly numbers.",
                        "这就把我们带到了本季度的数字。", true, DateTimeOffset.Now), show, max);
                    _speechWindow.ShowLine(new SpeechTranscriptLine(2, "Revenue is up twelve percent year over year.",
                        "营收同比增长了百分之十二。", true, DateTimeOffset.Now), show, max);
                    _speechWindow.ShowLine(new SpeechTranscriptLine(3, "We're seeing strong demand in the enterprise segment.",
                        "企业级市场的需求非常强劲。", false, DateTimeOffset.Now), show, max);
                    _speechWindow.SetUsage(840);
                    if (uitest == "speech-window-paused") _speechWindow.PauseSession();
                }
                catch (Exception ex) { Trace($"uitest speech: {ex}"); }
            };
            uiTimer.Start();
        }
        else if (uitest is not null && uitest.StartsWith("page-", StringComparison.OrdinalIgnoreCase))
        {
            // FT_UITEST=page-<枚举名> -> 停在指定页给截图核对（不动数据、不联网）。
            // 必须像其他分支一样延时：OnStartup 里同步导航赶在窗口首帧布局之前，内容区会是空的。
            var target = Enum.TryParse<MainPageId>(uitest[5..], ignoreCase: true, out var id) ? id : MainPageId.Home;
            var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            uiTimer.Tick += (_, _) =>
            {
                uiTimer.Stop();
                ShowPage(target);
            };
            uiTimer.Start();
        }
        else if (uitest is "toast" or "toast-action")
        {
            // FT_UITEST=toast* -> 提示条的两种形态（纯提示 / 带动作按钮）
            var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            uiTimer.Tick += (_, _) =>
            {
                uiTimer.Stop();
                try
                {
                    if (uitest == "toast")
                    {
                        ToastWindow.Show("注册成功,已自动登录,免费额度立刻可用。");
                    }
                    else
                    {
                        ToastWindow.Show("未登录:免费额度、生词本同步与语音翻译都用不了。", "去登录",
                            () => ShowPage(MainPageId.Account));
                    }
                }
                catch (Exception ex) { Trace($"uitest toast: {ex}"); }
            };
            uiTimer.Start();
        }
    }

    /// <summary>
    /// 画一张截屏区域的替身，好让 QA 钩子不必走框选浮层也能给截屏翻译卡拍照。
    /// </summary>
    private static byte[] BuildSampleCapture()
    {
        var visual = new System.Windows.Controls.Border
        {
            Width = 760,
            Height = 300,
            Background = System.Windows.Media.Brushes.White,
            Padding = new System.Windows.Thickness(26),
            Child = new System.Windows.Controls.TextBlock
            {
                Text = "快速的棕色狐狸跳过了懒狗。\nThe quick brown fox jumps over the lazy dog.\n请把这句话翻译成英文,谢谢。",
                FontSize = 20,
                LineHeight = 44,
                Foreground = System.Windows.Media.Brushes.Black,
                TextWrapping = System.Windows.TextWrapping.Wrap,
            },
        };
        var render = new System.Windows.Media.Imaging.RenderTargetBitmap(
            760, 300, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        // 从未测量或排布过的 visual 会渲染成空位图：必须先跑一遍布局，
        // RenderTargetBitmap 才能看到东西。
        visual.Measure(new System.Windows.Size(760, 300));
        visual.Arrange(new System.Windows.Rect(0, 0, 760, 300));
        render.Render(visual);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(render));
        using var stream = new System.IO.MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// 把回调切回 UI 线程再执行。后台流程（截图翻译协调器、异步翻译）都不该直接摸 WPF 控件：
    /// 跨线程访问抛的 InvalidOperationException 会被上层的 catch 吞掉，最后只表现为「界面没反应」。
    /// </summary>
    private void OnUiThread(Action action)
    {
        try
        {
            if (Dispatcher.CheckAccess())
            {
                action();
                return;
            }

            _ = Dispatcher.BeginInvoke(action);
        }
        catch (Exception ex)
        {
            Trace($"ui dispatch failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Trace(string message)
    {
        try
        {
            System.IO.File.AppendAllText(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ft-uitest-errors.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { }
    }

    private void OnRawMouseDown(ScreenPoint point)
    {
        if (_toolbar is not null && _toolbar.ContainsPhysical(point)) return;
        if (_card is not null && _card.ContainsPhysical(point)) return;
        _toolbar?.Hide();
        _card?.Hide();
    }

    private void OnToolbarCopy(ScreenPoint anchor) => _ = RunToolbarActionAsync(anchor, copy: true);

    private void OnToolbarTranslate(ScreenPoint anchor) => _ = RunToolbarActionAsync(anchor, copy: false);

    private async Task RunToolbarActionAsync(ScreenPoint anchor, bool copy)
    {
        // 抬起鼠标后重新抓取一次（成功率最高）；只重读一次，之后回落到剪贴板。
        // UIA 重读跑在工作线程上（ReadAt 可能阻塞约 550 ms——bugbot P1 UI 冻结）
        var capture = _capture!.CachedSelection;
        FastTranslation.Platform.Windows.WindowsSelectionCapture.Diag(
            $"click action copy={copy} cachedLen={capture.Text.Length} anchorEq={capture.Anchor == anchor} hwnd=0x{capture.SourceWindowHandle:X} method={capture.Method}");
        if (string.IsNullOrEmpty(capture.Text) || capture.Anchor != anchor)
        {
            var reread = await Task.Run(() => _capture.ReadSelectionAsync(anchor, CancellationToken.None));
            if (!string.IsNullOrEmpty(reread.Text)) capture = reread;
        }
        if (string.IsNullOrEmpty(capture.Text)
            && _settings.ClipboardFallbackEnabled
            && capture.SourceWindowHandle != 0
            && !SelectionCoordinator.IsClipboardRisky(ProcessNameOf(capture.SourceWindowHandle)))
        {
            // 用户在选好文字和点「复制」之间可能切过窗口——
            // 点这个按钮就代表想抓那段文字，所以先把来源窗口
            // 带回前台再按 Ctrl+C（v21：Qwen 页面的兜底）
            FastTranslation.Platform.Windows.WindowsSelectionCapture.SwitchSourceToForeground(capture.SourceWindowHandle);
            await Task.Delay(250);
            var fallback = await _capture.ReadViaClipboardAsync(capture.SourceWindowHandle, capture.Anchor, CancellationToken.None);
            if (!string.IsNullOrEmpty(fallback.Text)) capture = fallback;
        }

        if (copy)
        {
            if (!string.IsNullOrEmpty(capture.Text))
            {
                try { Clipboard.SetText(capture.Text); } catch { }
            }
            return;
        }
        if (string.IsNullOrEmpty(capture.Text)) return; // 没选中任何东西——保持安静
        await _coordinator!.TranslateCapturedAsync(capture);
    }

    private static string ProcessNameOf(long hwnd)
    {
        try
        {
            if (hwnd == 0) return string.Empty;
            Win32Process.GetWindowThreadProcessId((IntPtr)hwnd, out var pid);
            return pid == 0 ? string.Empty : System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
        }
        catch { return string.Empty; }
    }

    internal static class Win32Process
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    }

    private void OnHotkey()
    {
        if (!_captureEnabled) return;
        var fgHandle = Win32Facade.GetForegroundWindowHandle();
        if (fgHandle == 0) return;
        // 解析真正的前台进程：名字为空会绕过终端保护
        //（IsClipboardRisky），让 Ctrl+C 杀掉正在运行的命令（bugbot P1）
        var fgProcess = ProcessNameOf(fgHandle);
        var point = Win32Facade.PrimaryScreenCenter();
        _ = _coordinator!.HandleTriggerAsync(new TriggerEvent(
            TriggerKind.Hotkey, point, fgHandle, fgProcess, Environment.TickCount64));
    }

    /// <summary>三个入口（工具栏按钮 / 托盘菜单 / Ctrl+Alt+S）都只调这一个方法。</summary>
    private void RunScreenshotTranslation()
    {
        var hasKey = false;
        try { hasKey = !string.IsNullOrWhiteSpace(_secrets.LoadSecret(QwenMtProvider.ApiKeySecretName)); }
        catch { }
        FastTranslation.Platform.Windows.WindowsSelectionCapture.Diag(
            $"screenshot: requested, hasKey={hasKey}, coordinator={(_imageCoordinator is not null ? "set" : "NULL")}");
        if (!hasKey)
        {
            // 和主窗口「配置 Key」按钮同一条既有路径：打开设置页去填，不另造提示框
            _tray?.ShowBalloon("截图翻译", "还没有配置 API Key,请在设置里填写。");
            ShowSettings();
            return;
        }

        _toolbar?.Hide();
        _card?.Hide();
        _imageWindow?.Hide(); // 浮窗是 Topmost，不先收起会被截进下一张图
        _ = _imageCoordinator!.RunAsync();
    }

    private void OnCopyRequested()
    {
        if (_lastResult is { Succeeded: true })
        {
            try { Clipboard.SetText(_lastResult.TranslatedText); } catch { }
        }
        _card?.Hide();
    }

    /// <summary>
    /// 从结果卡保存。未登录时会打开账号窗口而不是报错：
    /// 单词本住在服务端，所以「保存」在这里没有本地含义（D8——免费版不能保存）。
    /// </summary>
    /// <summary>
    /// 走开发者网关的那一次翻译。返回 null 表示"这次别用网关"（没登录 / 没有令牌），
    /// 组合 provider 会安静地改用用户自己的 Key，用户不需要知道网关存在。
    /// </summary>
    private Task<TranslationResult?> GatewayTranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken) => GuardGatewayAsync("translate", async () =>
        {
            if (_account is not { IsSignedIn: true } account) return null;
            if (!_gatewayHealth.IsUsable) return null;
            var token = await account.GetAccessTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(token) || _gatewayHttp is null) return null;

            // 每次现造一个 provider：令牌会换，而 Authorization 是请求头，只能接着当前令牌拼。
            // HttpClient 传进去（_ownsHttp = false），Dispose 不会把它一起关掉。
            using var provider = new QwenMtProvider(
                () => token,
                new QwenMtOptions
                {
                    BaseUrl = GatewayBaseUrl(),
                    TimeoutSeconds = 45,
                    // 设备指纹让服务端把日额度记到真正发请求的这台设备上
                    ExtraHeaders = new Dictionary<string, string> { ["X-FT-Device"] = account.DeviceFingerprint },
                },
                _gatewayHttp);
            var gatewayResult = await provider.TranslateAsync(request, cancellationToken);
            NoteGatewayFailure(gatewayResult.Error);
            return gatewayResult;
        });

    /// <summary>截图翻译走网关的 /v1/image；返回 null 表示这次别用网关（见文本链路的说明）。</summary>
    private Task<ImageTranslationResult?> GatewayTranslateImageAsync(
        ImageTranslationRequest request,
        CancellationToken cancellationToken) => GuardGatewayAsync("image", async () =>
        {
            if (_account is not { IsSignedIn: true } account) return null;
            if (!_gatewayHealth.IsUsable) return null;
            var token = await account.GetAccessTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(token) || _gatewayHttp is null) return null;

            using var provider = new QwenMtImageTranslator(
                () => token,
                new QwenMtImageOptions
                {
                    // 图片端点是一整条 URL，网关不拼后缀，所以这里直接给到 /v1/image
                    BaseUrl = GatewayBaseUrl() + "/image",
                    TimeoutSeconds = 60,
                    ExtraHeaders = new Dictionary<string, string> { ["X-FT-Device"] = account.DeviceFingerprint },
                },
                _gatewayHttp);
            var imageResult = await provider.TranslateImageAsync(request, cancellationToken);
            NoteGatewayFailure(imageResult.Error);
            return imageResult;
        });

    /// <summary>
    /// OCR 走网关时复用同一套令牌与设备头，只是路径换成 /v1/ocr。
    /// 返回 null（没登录 / 拿不到令牌）让 <see cref="GatewayOcrProvider"/> 回落到用户自己的 Key。
    /// </summary>
    private Task<ImageOcrResult?> GatewayRecognizeImageAsync(
        ImageOcrRequest request,
        CancellationToken cancellationToken) => GuardGatewayAsync("ocr", async () =>
        {
            if (_account is not { IsSignedIn: true } account) return null;
            if (!_gatewayHealth.IsUsable) return null;
            var token = await account.GetAccessTokenAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(token) || _gatewayHttp is null) return null;

            using var client = new QwenOcrClient(
                () => token,
                new QwenOcrOptions
                {
                    BaseUrl = GatewayBaseUrl() + "/ocr",
                    TimeoutSeconds = 60,
                    ExtraHeaders = new Dictionary<string, string> { ["X-FT-Device"] = account.DeviceFingerprint },
                },
                _gatewayHttp);
            var ocrResult = await client.RecognizeAsync(request, cancellationToken);
            NoteGatewayFailure(ocrResult.Error);
            return ocrResult;
        });

    /// <summary>
    /// 文档里的一段。没有 <c>TranslationResult</c> 那条兜底链：段落翻译失败就是失败，
    /// 抛出去让 <see cref="SegmentTranslator"/> 重试、用尽后把这一段标成「未翻译」，
    /// 而不是拿一句空译文写回用户的文档。
    /// </summary>
    private async Task<string> TranslateDocumentSegmentsAsync(
        string text,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        // 源语言给 auto：文档里的段落可能整段是英文也可能整段是中文，
        // 逐段猜比整份猜更准，而 qwen-mt 接受 auto。
        var request = new TranslationRequest(
            text,
            "auto",
            LanguageCatalog.ToApiName(targetLanguage),
            _settings.Model);
        var result = await _documentProvider!.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded) return result.TranslatedText;

        // 额度/余额耗尽是「换个 Key 或充值」而不是「这一段有问题」：换成专用异常让
        // SegmentTranslator 立刻放弃整份作业，而不是逐段把 3 次重试都打在枯竭的额度上。
        if (result.Error is { Code: TranslationErrorCode.QuotaExhausted } quota)
        {
            throw new QuotaExhaustedException(quota.Message);
        }

        throw new InvalidOperationException(result.Error?.Message ?? "翻译失败");
    }

    /// <summary>
    /// 三条网关链路的兜底。任何没预料到的异常都不该让整次翻译 / 截图翻译失败：
    /// 记一行诊断，返回 null，让组合 provider 安静地回落到用户自己的 Key。
    /// 取消要照原样抛上去——那是调用方主动结束，不是网关的错。
    /// </summary>
    private async Task<T?> GuardGatewayAsync<T>(string stage, Func<Task<T?>> body) where T : class
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            WindowsSelectionCapture.Diag($"gateway {stage} threw {ex.GetType().Name}: {ex.Message}");
            Trace($"gateway {stage} threw {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>译图窗标题栏：来源进程 + 文本链路的异常（有才拼）。</summary>
    private static string CaptureCaption(ImageCaptureResult result)
    {
        var caption = string.IsNullOrWhiteSpace(result.OriginApp)
            ? "截图翻译"
            : "截图翻译 · 来自 " + result.OriginApp;
        if (!string.IsNullOrWhiteSpace(result.TextError)) caption += " · " + result.TextError;
        return caption;
    }

    /// <summary>
    /// 译图窗里的「保存到单词本」：原文取识别框里当前的文本（用户可能已经把 OCR 的错字改了），
    /// 译文用截图那趟已经算好的那句（<see cref="ImageNoteDraft.Create"/> 保证原文被改过时不会存错译文）。
    /// </summary>
    private void SaveScreenshotText(ImageNoteDraft draft)
    {
        if (draft.SourceText.Length == 0) return;
        if (_account is not { IsSignedIn: true })
        {
            _imageWindow?.ShowSaveError("先在账号里登录再保存");
            _tray?.ShowBalloon("Fast Translation", "先在账号里登录,保存的单词才能跨设备同步");
            ShowAccount();
            return;
        }

        _ = SaveScreenshotTextAsync(draft);
    }

    private async Task SaveScreenshotTextAsync(ImageNoteDraft draft)
    {
        if (_account is not { IsSignedIn: true }) return;
        try
        {
            var saved = await _account.SaveNoteAsync(
                NoteClassifier.Classify(draft.SourceText),
                draft.SourceText,
                draft.TargetText,
                draft.SourceLang,
                draft.TargetLang,
                draft.Model,
                ForegroundInfo.ProcessName(),
                CancellationToken.None);
            if (saved.Succeeded)
            {
                var kind = NoteKinds.Display(saved.Value!.Kind);
                _imageWindow?.MarkSaved(kind);
                _tray?.ShowBalloon("Fast Translation", "已保存到" + kind + "本");
                return;
            }

            if (saved.Error!.Code == AccountErrorCode.SubscriptionRequired)
            {
                _imageWindow?.ShowSaveError(saved.Error.Display);
                _tray?.ShowBalloon("Fast Translation", saved.Error.Display);
                ShowAccount();
                return;
            }

            var message = saved.Error.Code == AccountErrorCode.NotSignedIn
                ? "先在账号里登录,保存的单词才能跨设备同步"
                : saved.Error.Display;
            _imageWindow?.ShowSaveError(message);
            _tray?.ShowBalloon("Fast Translation", message);
        }
        catch (Exception ex)
        {
            Trace($"save screenshot note: {ex}");
            _imageWindow?.ShowSaveError("保存没成功,稍后再试一次");
            _tray?.ShowBalloon("Fast Translation", "保存没成功,稍后再试一次");
        }
    }

    /// <summary>
    /// 发往本机或内网地址的请求不该走系统代理：Windows 上每个请求会先做一次代理自动探测，
    /// 而本机网关根本不需要代理。探测失败/被拒的代价直接算在用户第一次翻译的等待里。
    /// </summary>
    private static HttpClient CreateApiHttpClient(TimeSpan timeout, string baseUrl)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            UseProxy = !IsLocalAddress(baseUrl),
        };
        return new HttpClient(handler) { Timeout = timeout };
    }

    private static bool IsLocalAddress(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
        && (uri.IsLoopback || (IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address)));

    private string GatewayBaseUrl()
    {
        var host = string.IsNullOrWhiteSpace(_settings.AccountBaseUrl)
            ? AccountApiClient.DefaultBaseUrl
            : _settings.AccountBaseUrl.TrimEnd('/');
        return host + "/v1";
    }

    private async void OnSaveRequested()
    {
        if (_lastResult is not { Succeeded: true } result) return;
        if (_account is null || !_account.IsSignedIn)
        {
            _card?.ShowNotice("先在账号里登录,保存的单词才能跨设备同步");
            ShowAccount();
            return;
        }

        var sourceText = string.IsNullOrWhiteSpace(result.RequestText)
            ? _lastSelection.Text
            : result.RequestText;
        var originApp = ForegroundInfo.ProcessName();
        try
        {
            var saved = await _account.SaveNoteAsync(
                NoteClassifier.Classify(sourceText),
                sourceText,
                result.TranslatedText,
                result.SourceLanguage,
                result.TargetLanguage,
                result.Model,
                originApp,
                CancellationToken.None);
            if (saved.Succeeded)
            {
                _card?.MarkSaved();
                _card?.ShowNotice("已保存到" + NoteKinds.Display(saved.Value!.Kind) + "本");
                return;
            }
            if (saved.Error!.Code == AccountErrorCode.SubscriptionRequired)
            {
                // 订阅闸门：先说明为什么没存上，再把人送到能升级/兑换激活码的账号窗
                _card?.ShowNotice(saved.Error!.Display);
                ShowAccount();
                return;
            }
            _card?.ShowNotice(saved.Error!.Code == AccountErrorCode.NotSignedIn
                ? "先在账号里登录,保存的单词才能跨设备同步"
                : saved.Error!.Display);
        }
        catch (Exception ex)
        {
            Trace($"save note: {ex}");
            _card?.ShowNotice("保存没成功,稍后再试一次");
        }
    }

    private async void OnReplaceRequested() => await WriteBackAsync(replace: true);

    private async void OnInsertRequested() => await WriteBackAsync(replace: false);

    private async Task WriteBackAsync(bool replace)
    {
        if (_lastResult is not { Succeeded: true } || string.IsNullOrEmpty(_lastSelection.Text)) return;
        if (!_settings.WriteBackEnabled) return;
        _card?.Hide();
        var outcome = replace
            ? await _writeBack!.ReplaceAsync(_lastSelection, _lastResult.TranslatedText, CancellationToken.None)
            : await _writeBack!.InsertAsync(_lastSelection, _lastResult.TranslatedText, CancellationToken.None);
        if (outcome is not (WriteBackOutcome.Replaced or WriteBackOutcome.Inserted))
        {
            // 非模态通知（曾经用 MessageBox 把应用冻住过一次）；不产生剪贴板副作用
            _card?.ShowNotice(replace ? "当前内容不能替换诶,换一个试试" : "当前内容不能插入诶,换一个试试");
        }
    }

    /// <summary>
    /// 应用外观（v84）：设置里换主题或强调色时立刻重刷，不必重启。
    /// 画刷是 po:Freeze="False" 的可变实例，ThemeBridge 就地改色，所有 DynamicResource 引用
    /// 跟着变；只有原生标题栏要单独再推一次 DWM——它只在 SourceInitialized 里设过一次（v38）。
    /// 参数用「设置值」而不是解析结果，好让调用方原样传 _settings.Theme。
    /// </summary>
    internal static void ApplyAppearance(string? themeSetting, string? accent)
    {
        ThemeBridge.Apply(ThemePalette.Resolve(themeSetting), accent);
        if (Current is not App app) return;

        // 已开的窗口都要重刷标题栏；Show() 之后才建 HWND 的窗口由 WindowChrome.Apply 兜住
        foreach (Window window in app.Windows)
        {
            try { WindowChrome.SetDarkTitleBar(window, ThemePalette.Current == AppTheme.Dark); }
            catch { /* 只是外观：DWM 拒绝就保留系统默认标题栏 */ }
        }
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null) return;
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void ShowOnboarding()
    {
        var window = new OnboardingWindow(_secrets, _settings, _settingsService);
        window.ShowDialog();
    }

    private void ShowNotice(UpdateCheckNotice notice) =>
        NoticeWindow.Show(DialogOwner(), notice.Title, notice.Body, notice.LinkUrl);

    /// <summary>
    /// 更新对话框该挂在哪个窗口上：用户正在用的、属于我们的那一个。检查可能从设置窗口
    /// 发起，而设置窗口是盖在主窗口上的模态窗口——把对话框挂在主窗口上会让它落到错误的
    /// 页面上（用户反馈 2026-10：设置页点检查更新，弹窗盖在使用页上）。没有活动窗口说明
    /// 检查来自托盘；那时应用通常缩在托盘里，没有 Owner，对话框就落到屏幕中央。
    /// </summary>
    private Window? DialogOwner()
    {
        foreach (var window in Windows.OfType<Window>())
        {
            if (window.IsVisible && window.IsActive) return window;
        }
        return _mainWindow is { IsVisible: true } ? _mainWindow : null;
    }

    /// <summary>
    /// 账号状态会碰到的东西都在这里：底部那一行，以及账号窗口打开时通过协调器的事件
    /// 重新读取自己的副本。
    /// </summary>
    private void OnAccountChanged()
    {
        var profile = _account?.Session?.Profile;
        _mainWindow?.RefreshAccountStatus(profile?.Email, profile?.PlanDisplay);
        _ = _mainWindow?.RefreshReviewBadgeAsync();
    }

    /// <summary>
    /// 后台探网关有没有人在听，结论写进 <see cref="_gatewayHealth"/>。翻译路径只读结论、从不
    /// 自己发起探测，所以那两秒永远落在后台线程上。网关被标记不可用后每
    /// <see cref="GatewayHealth.DownProbeIntervalMs"/> 重探一次，探通即恢复走网关——
    /// 免费额度链路不能因为一次失败就永久放弃。
    /// </summary>
    private async Task RunGatewayProbeLoopAsync()
    {
        while (!_gatewayProbeCts.IsCancellationRequested)
        {
            if (!_gatewayHealth.IsUsable && _gatewayHealth.TryBeginProbe())
            {
                try { _gatewayHealth.MarkProbed(await ProbeGatewayOnceAsync().ConfigureAwait(false)); }
                finally { _gatewayHealth.EndProbe(); }
            }

            try { await Task.Delay(GatewayHealth.DownProbeIntervalMs, _gatewayProbeCts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>连一次网关端口就够判断了：连接被拒和端口无人监听都会在这里失败。</summary>
    private async Task<bool> ProbeGatewayOnceAsync()
    {
        if (!Uri.TryCreate(GatewayBaseUrl(), UriKind.Absolute, out var uri)) return false;
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(uri.Host, uri.Port, timeout.Token).ConfigureAwait(false);
            Trace($"gateway probe: {uri.Authority} reachable");
            return true;
        }
        catch (Exception ex)
        {
            Trace($"gateway probe: {uri.Authority} unreachable ({ex.GetType().Name})");
            return false;
        }
    }

    /// <summary>
    /// 5xx 也要歇一会。网关返回 5xx 说明它活着但不肯干活（典型是 Gateway.Enabled=false 时的
    /// <c>503 gateway_disabled</c>），这时每一条翻译都先白跑一次完整往返再回落到自己的 Key，
    /// 用户只感觉到「启动后就慢」。429、额度用完、401 是网关在正常回答，下次照样要问。
    /// </summary>
    private void NoteGatewayFailure(TranslationError? error)
    {
        if (error?.Code is not (TranslationErrorCode.Network or TranslationErrorCode.ServerError)) return;
        _gatewayHealth.MarkDown();
        Trace($"gateway marked down: {error.Message}");
    }

    private async Task RestoreAccountAsync()
    {
        if (_account is null) return;
        try
        {
            await _account.TryRestoreAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Trace($"account restore: {ex}");
        }
        // Core 一路 ConfigureAwait(false)，续跑可能已经不在 UI 线程上；提示条是窗口，必须调度回去
        _ = Dispatcher.BeginInvoke(PromptSignInWhenSignedOut);
    }

    /// <summary>
    /// 启动时没登录态就给一条引导（用户反馈：进去什么提示都没有，不知道免费额度要用账号换）。
    /// 判定点必须在 TryRestoreAsync **之后**：本地 token 还在读的时候算「未登录」会每次启动都瞎提示。
    /// </summary>
    private void PromptSignInWhenSignedOut()
    {
        if (_account is not { IsSignedIn: false }) return;
        // 首次运行的引导窗正在收集 API Key，再压一条「先登录」只是噪音
        if (!_settings.FirstRunCompleted) return;
        ToastWindow.Show("未登录:免费额度、生词本同步与语音翻译都用不了。", "去登录",
            () => ShowPage(MainPageId.Account));
    }

    // 托盘「账号」与各处「先登录」引导的目的地：菜单项写的就是「账号」，落点必须是账号页(v98)
    private void ShowAccount() => ShowPage(MainPageId.Account);

    private void ShowNotes() => ShowPage(MainPageId.Word);

    private void ShowSettings() => ShowPage(MainPageId.Settings);

    /// <summary>文档翻译页（v72）。</summary>
    private void ShowDocument() => ShowPage(MainPageId.Document);

    /// <summary>应用内每个目的地现在都是单窗口的一个页面了（设计 D13）。</summary>
    private void ShowPage(MainPageId page)
    {
        ShowMainWindow();
        _mainWindow?.ShowPage(page);
        if (page == MainPageId.Speech) _ = RefreshSpeechQuotaHintAsync();
    }

    /// <summary>
    /// 「存在新版本」的唯一入口。只有确认过、校验过的下载才会到达调用方，也只有到那时
    /// 应用才交给安装程序并退出——安装程序自己会启动新构建，所以再回到这里会和它抢。
    /// </summary>
    private void PresentUpdate(UpdateManifest manifest, ReleaseVersion currentVersion)
    {
        var window = new UpdateWindow(manifest, currentVersion, UpdateCoordinator.IsInstalledCopy());
        if (DialogOwner() is { } owner)
        {
            window.Owner = owner;
        }
        else
        {
            // 没有 Owner 的 CenterOwner 窗口会落在任意位置；定时器触发时
            // 应用通常正缩在托盘里
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (window.ShowDialog() != true) return;
        var installer = window.DownloadedPath;
        if (string.IsNullOrEmpty(installer) || _updates is null) return;

        if (!_updates.ApplyUpdate(installer, out var error))
        {
            UpdateLog.Write($"apply failed: {error}");
            _tray?.ShowBalloon("更新失败", "无法启动安装程序,请到下载页手动更新。");
        }
        // ApplyUpdate 成功就已经退出了应用；这之后不会再执行
    }

    // ----- 语音翻译（Task 19）：组装采集→重采样→协调器→浮窗 -----

    private void RunSpeechTranslation()
    {
        if (_speechCoordinator is not null)
        {
            // 已在会话中：把浮窗带到前台，再点一次就靠浮窗上的「结束」收尾
            ShowSpeechWindow();
            return;
        }
        if (_speechWindow is { IsPaused: true })
        {
            // 暂停期间再按热键/托盘 = 接着讲。不能当全新会话：那会把留着字幕的窗口清空(v98)
            ShowSpeechWindow();
            OnSpeechResumeRequested();
            return;
        }
        if (_account is not { IsSignedIn: true })
        {
            _tray?.ShowBalloon("语音翻译", "语音翻译按秒计费,先登录账号再开始");
            return;
        }
        Observe(StartSpeechSessionAsync(), "语音启动失败");
    }

    private async Task StartSpeechSessionAsync(bool resume = false)
    {
        var account = _account!;
        // 额度预检必须查服务端会查的每一个桶（月度 + 免费档的日额度），
        // 判定收在 SpeechQuotaGate 里，免得窗口开了、会话建了才开始 429
        var usage = await account.ReadUsageAsync(CancellationToken.None).ConfigureAwait(true);
        if (!usage.Succeeded || usage.Value is null)
        {
            _tray?.ShowBalloon("语音翻译", "暂时读不到额度信息,请稍后再试");
            return;
        }
        SetSpeechQuotaHint(usage.Value);
        if (SpeechQuotaGate.Refusal(usage.Value) is { } refusal)
        {
            _tray?.ShowBalloon("语音翻译", refusal);
            return;
        }

        // 浮窗只在第一条会话创建：暂停/继续要留字幕，而事件重复订阅会让一次点击收尾两次(v98)
        if (_speechWindow is null)
        {
            _speechWindow = new SpeechWindow();
            _speechWindow.StopRequested += OnSpeechStopRequested;
            _speechWindow.PauseRequested += OnSpeechPauseRequested;
            _speechWindow.ResumeRequested += OnSpeechResumeRequested;
            _speechWindow.ClearRequested += () => { };
            // 浮窗被点 X 就收尾整个会话，不能让会话在看不见的窗口里继续烧额度
            _speechWindow.Closed += (_, _) => { _speechWindow = null; EndSpeechSession(null); };
        }
        if (resume) _speechWindow.ResumeSession();
        else _speechWindow.BeginSession();

        // 传输与字幕服务的生命周期都挂在会话上，EndSpeechSession 统一释放
        var transport = new GatewaySpeechTransport(
            async () => account is { IsSignedIn: true } a
                ? await a.GetAccessTokenAsync(CancellationToken.None).ConfigureAwait(false)
                : null,
            new Uri(GatewayBaseUrl()),
            account.DeviceFingerprint,
            _gatewayHttp);
        var options = new SpeechSessionOptions(
            SourceRate: 16000,
            MaxSentences: _settings.SpeechMaxSentences);
        var subtitles = new SpeechTranscriptService(
            TranslateSpeechBatchAsync,
            options);
        var coordinator = new SpeechTranslationCoordinator(transport, subtitles);
        _speechTransport = transport;
        _speechSubtitles = subtitles;
        _speechCoordinator = coordinator;
        coordinator.TranscriptUpdated += line => Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () => _speechWindow?.ShowLine(line, _settings.SpeechShowSource, _settings.SpeechMaxSentences));
        coordinator.Failed += message => Dispatcher.BeginInvoke(() => EndSpeechSession(message));
        // 服务端按 max(秒,MinBilledSeconds)×CharsPerSecond 记额度，这里必须同口径换算成「已用 N 字符」
        coordinator.UsageObserved += seconds => Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () => _speechWindow?.SetUsage((long)(Math.Max(seconds, SpeechQuotaGate.MinBilledSeconds) * SpeechQuotaGate.CharsPerSecond)));

        StartSpeechCapture();

        await coordinator.StartAsync(options).ConfigureAwait(true);
        if (coordinator.State == SpeechCoordinatorState.Failed)
        {
            EndSpeechSession("无法连接语音服务,请稍后再试");
        }
    }

    // 回环采集：设备给的是混音格式（典型 48k×2ch float32），重采样成 16k mono PCM16 喂给协调器。
    // 独立成方法是为了设备切换时只重建采集，字幕与网络连接都保得住
    private void StartSpeechCapture()
    {
        var capture = new WasapiLoopbackCapture();
        _speechCapture = capture;
        capture.Started += format =>
        {
            _speechResampler = new AudioResampler(format.SampleRate, format.Channels);
        };
        capture.DataAvailable += block => PumpSpeechAudio(block);
        // v79: 切换默认播放设备时旧回环会话必然失效——重建采集而不是结束会话，
        // 用户感知只是字幕停顿一秒多
        capture.DeviceInvalidated += () => Dispatcher.BeginInvoke(
            () => Observe(RestartSpeechCaptureAsync(), "切换音频设备后恢复失败"));
        capture.Failed += ex => Dispatcher.BeginInvoke(() =>
            EndSpeechSession("无法采集系统声音: " + ex.Message));
        capture.Start();
    }

    private async Task RestartSpeechCaptureAsync()
    {
        if (_speechCoordinator is null) return;
        var stale = _speechCapture;
        _speechCapture = null;
        _speechResampler = null;
        if (stale is not null)
        {
            // 同上：UI 线程不能等 Join(2s)，设备切换时窗口会假死
            _ = Task.Run(() =>
            {
                stale.Stop();
                stale.Dispose();
            });
        }
        // 给音频引擎留出端点切换的时间；设备失效后立刻重开常常拿不到新端点
        await Task.Delay(1500).ConfigureAwait(true);
        if (_speechCoordinator is null) return;
        StartSpeechCapture();
    }

    // 免费档的日额度通常比月度先到顶。只报「本月还能讲几分钟」会让人在开场被拒时
    // 完全想不通（本月明明还剩六千字符），所以两个窗口都报出来。
    private void SetSpeechQuotaHint(AccountUsage usage)
    {
        var monthly = usage.RemainingMonthlyChars / SpeechQuotaGate.OneMinuteChars;
        var text = usage.RemainingDailyChars is { } daily
            ? $"语音按 20 字符/秒计费,今天可讲约 {daily / SpeechQuotaGate.OneMinuteChars} 分钟,本月约 {monthly} 分钟"
            : $"语音按 20 字符/秒计费,本月剩余可讲约 {monthly} 分钟";
        _mainWindow?.SetSpeechQuota(text);
    }

    private async Task RefreshSpeechQuotaHintAsync()
    {
        if (_account is not { IsSignedIn: true }) return;
        var usage = await _account.ReadUsageAsync(CancellationToken.None).ConfigureAwait(true);
        if (usage.Succeeded && usage.Value is not null)
        {
            SetSpeechQuotaHint(usage.Value);
        }
    }

    // 采集线程回调：float32 混音块 → 下混+线性插值+PCM16 → 推给协调器环形缓冲
    private void PumpSpeechAudio(byte[] block)
    {
        var resampler = _speechResampler;
        var coordinator = _speechCoordinator;
        if (resampler is null || coordinator is null) return;
        var floats = MemoryMarshal.Cast<byte, float>(block);
        var output = new byte[resampler.MaxOutputBytes(floats.Length)];
        var written = resampler.Process(floats, output);
        if (written > 0) coordinator.PushAudio(output[..written]);
    }

    // 字幕服务的翻译回调：走文档链路同一个组合 provider（网关优先、失败回落自带 Key），
    // 不能用 _translation——它「新请求取消上一在途」的语义会把并发的字幕批互相打断。
    private async Task<string> TranslateSpeechBatchAsync(string text, CancellationToken cancellationToken)
    {
        var request = new TranslationRequest(
            text,
            "auto",
            LanguageCatalog.ToApiName(_settings.SpeechTargetLanguage),
            _settings.Model);
        var result = await _documentProvider!.TranslateAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded) return result.TranslatedText;
        throw new InvalidOperationException(result.Error?.Message ?? "翻译失败");
    }

    private void OnSpeechStopRequested() => EndSpeechSession(null);

    /// <summary>
    /// 暂停 = 真的结束这条 WS 会话，而不是挂着连接不采集。理由（v98，服务端实测口径）：
    /// 服务端给会话设的是墙钟硬顶 <c>CancelAfter(sessionSeconds)</c>，空闲期照常计时，暂停白吃会话寿命；
    /// 更要命的是收尾有两条记账路径——一条 usage 帧都没收到时按<b>整段墙钟</b>兜底（v81 防「连上就砍」白嫖），
    /// 开局就暂停、一句没说的那段会被全记到额度上（免费档一次会话上限 100 秒 = 当日全部额度）。
    /// 代价：每轮重开多付一次最短计费保底（5 秒 = 100 字符）。
    /// </summary>
    private void OnSpeechPauseRequested() => EndSpeechSession(null, paused: true);

    private void OnSpeechResumeRequested()
    {
        if (_speechCoordinator is not null) return;
        if (_account is not { IsSignedIn: true })
        {
            _tray?.ShowBalloon("语音翻译", "语音翻译按秒计费,先登录账号再开始");
            return;
        }
        // 继续要走完整的预检：暂停这段时间日额度可能已经见底，那时 StartSpeechSessionAsync 会自己
        // 弹气泡并把窗口留在「已暂停」，不能假装已经接上了
        Observe(StartSpeechSessionAsync(resume: true), "语音启动失败");
    }

    /// <summary>观察后台任务的异常。</summary>
    /// <remarks>fire-and-forget 的 Task 无人 await 时异常会被静默丢弃，语音链路一静默失败用户只看到窗口一直转圈，
    /// 所以统一收口到 EndSpeechSession 显示原因。</remarks>
    private void Observe(Task task, string failureMessage) =>
        _ = task.ContinueWith(
            t => Dispatcher.BeginInvoke(() =>
                EndSpeechSession(failureMessage + ":" + t.Exception!.GetBaseException().Message)),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    private void EndSpeechSession(string? error, bool paused = false)
    {
        var coordinator = _speechCoordinator;
        var resampler = _speechResampler;
        var subtitles = _speechSubtitles;
        _speechCoordinator = null;
        _speechResampler = null;
        var capture = _speechCapture;
        _speechCapture = null;
        if (resampler is not null && coordinator is not null)
        {
            // 重采样器内部还压着不足一块的样本（最多 100ms），收尾时补发一次，
            // 否则每段话的尾巴都会被吞掉，字幕末尾缺字。
            try
            {
                var tail = new byte[resampler.MaxOutputBytes(1600 * 4)];
                var count = resampler.Flush(tail);
                if (count > 0) coordinator.PushAudio(tail[..count]);
            }
            catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
            {
                // 协调器已进入 Finishing 时 PushAudio 会拒绝，收尾补发失败不影响结束流程
            }
        }

        if (capture is not null)
        {
            // Stop() 内部 Join 等采集线程退出（最多 2s）。这是 UI 线程，同步等会把窗口冻住，
            // 所以甩到后台；采集线程之后的回调因 _speechResampler/_speechCoordinator 已置空而直接返回
            _ = Task.Run(() =>
            {
                try
                {
                    capture.Stop();
                    capture.Dispose();
                }
                catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException)
                {
                    // 采集设备已被拔掉或已释放时 Stop/Dispose 会抛，收尾阶段吞掉
                }
            });
        }

        if (coordinator is not null)
        {
            Observe(coordinator.StopAsync(), "语音结束失败");
            // 不等 DisposeAsync：协调器内部对停传已有 2s 超时；这里是 UI 线程，同步等待会死锁
            Observe(coordinator.DisposeAsync().AsTask(), "语音资源释放失败");
        }

        _speechTransport = null;
        _speechSubtitles = null;
        // 暂停只改浮窗显示（留字幕与累计值）；「结束」才写已结束并把「继续」禁掉
        if (paused) _speechWindow?.PauseSession();
        else _speechWindow?.EndSession(error, subtitles?.DroppedCount ?? 0);
    }

    private void ShowSpeechWindow()
    {
        if (_speechWindow is null) return;
        // 不调 Activate()：开始语音时用户多半正在打字，抢焦点会把输入打断；
        // 用户要拖动或点关闭时自己会点它，Show() 已足够可见（置顶）。
        _speechWindow.Show();
    }

    private void ExitApp()
    {
        _reallyExiting = true;
        _toolbar?.Dispose();
        _card?.Dispose();
        _imageCoordinator?.Dispose();
        _ocrProvider?.Dispose();
        _imageProvider?.Dispose();
        _imageWindow?.Dispose();
        _imageHotkey?.Dispose();
        _speechHotkey?.Dispose();
        EndSpeechSession(null);
        _coordinator?.Dispose();
        _capture?.Dispose();
        _hotkey?.Dispose();
        if (_account is not null) _account.Changed -= OnAccountChanged;
        _accountClient = null;
        // 不 Dispose 文档链路：它的组合 provider 包着与划词链路共用的那个 ownProvider，
        // Dispose 会顺着把用户 Key 的 provider 也关掉，而进程正要退出，没必要冒这个风险。
        _documentProvider = null;
        _gatewayHttp?.Dispose();
        _gatewayHttp = null;
        // 只取消不 Dispose：探测循环此刻可能正挂在 Task.Delay 上，Dispose 会让它的注册抛
        // ObjectDisposedException；后台线程随进程退出即可。
        _gatewayProbeCts.Cancel();
        _tray?.Dispose();
        _mainWindow?.Close();
        try { _mutex?.ReleaseMutex(); } catch { }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _settingsService.Save(_settings);
        base.OnExit(e);
    }

    // ----- IResultPresenter：结果展示接口 -----
    public void ShowLoading(string previewText, CapturedSelection source)
    {
        _toolbar?.Hide();
        _card?.ShowLoading(previewText, source);
    }

    public void ShowCard(TranslationResult result, CapturedSelection source)
    {
        _lastSelection = source;
        _lastResult = result;
        _toolbar?.Hide();
        _card?.ShowResult(result, source);
    }

    public void ShowTransientError(string message) => _card?.ShowResult(
        new TranslationResult(string.Empty, string.Empty, "auto", "auto", string.Empty, 0, false)
        { Error = new TranslationError(TranslationErrorCode.Unknown, message) },
        CapturedSelection.Empty(FastTranslation.Platform.Windows.WindowsSelectionCapture.CurrentCursorPoint()));

    public void HideCard() => _card?.Hide();

    private static byte[]? LoadIconBytes()
    {
        try
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/FastTranslation.ico"));
            if (resource is null) return null;
            using var stream = resource.Stream;
            using var ms = new System.IO.MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return null; // 托盘回落到自己绘制的图标
        }
    }
}