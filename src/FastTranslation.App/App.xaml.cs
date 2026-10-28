using System.Threading;
using System.Windows;
using FastTranslation.Core;
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
    private WindowsSelectionCapture? _capture;
    private SelectionCoordinator? _coordinator;
    private SelectionToolbar? _toolbar;
    private ResultCardWindow? _card;
    private ImageResultWindow? _imageWindow;
    private ImageTranslationCoordinator? _imageCoordinator;
    private IImageTranslationProvider? _imageProvider;
    private WriteBackService? _writeBack;
    private TranslationService? _translation;
    private SettingsService _settingsService = new();
    private AppSettings _settings = new();
    private readonly DpapiSecretStore _secrets = new();
    private bool _captureEnabled = true;
    private UpdateCoordinator? _updates;

    private CapturedSelection _lastSelection = CapturedSelection.Empty(default);
    private TranslationResult? _lastResult;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, SingleInstanceMutexName, out var isNew);
        if (!isNew)
        {
            // second launch: wake the existing instance's window instead of exiting silently
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
            args.Handled = true; // tray app must never die silently on one bad event
        };

        _settings = _settingsService.Load();

        // palette before any window exists: the XAML brushes are mutable (po:Freeze="False") and
        // ThemeBridge recolors them in place, so every style and template picks up the theme (v24)
        ThemeBridge.Apply(ThemePalette.Resolve(_settings.Theme));

        var provider = new QwenMtProvider(
            () => _secrets.LoadSecret(QwenMtProvider.ApiKeySecretName),
            new QwenMtOptions
            {
                BaseUrl = string.IsNullOrWhiteSpace(_settings.OverrideBaseUrl)
                    ? new QwenMtOptions().BaseUrl
                    : _settings.OverrideBaseUrl,
            });
        _translation = new TranslationService(provider);

        // stage-1 toolbar (复制/翻译) + stage-2 result card (复制/替换/插入/✕)
        _toolbar = new SelectionToolbar();
        _toolbar.CopyClicked += OnToolbarCopy;
        _toolbar.TranslateClicked += OnToolbarTranslate;
        _toolbar.ScreenshotClicked += _ => RunScreenshotTranslation();
        _card = new ResultCardWindow();
        _card.CopyRequested += OnCopyRequested;
        _card.ReplaceRequested += OnReplaceRequested;
        _card.InsertRequested += OnInsertRequested;

        _capture = new WindowsSelectionCapture(
            Dispatcher,
            fallbackEnabled: () => _settings.ClipboardFallbackEnabled && _captureEnabled,
            log: m => System.Diagnostics.Debug.WriteLine(m),
            isExcluded: processName => _settings.ExcludedProcesses
                .Any(n => string.Equals(n, processName, StringComparison.OrdinalIgnoreCase)));
        _writeBack = new WriteBackService(_capture.Reader);
        _coordinator = new SelectionCoordinator(_capture, this, _translation, () => _settings);
        _capture.SelectionToolbarRequested += captured =>
        {
            if (!_captureEnabled) return;
            _card?.Hide();
            _toolbar?.ShowAt(captured);
        };
        _capture.RawMouseDown += OnRawMouseDown; // outside-click dismissal for toolbar + card
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
            // actually register the fallback — previously it only changed the label
            // while no hotkey worked at all (bugbot P2)
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
        _tray.ExitRequested += ExitApp;
        _tray.Show("Fast Translation — 取词已开启");

        // 截屏翻译走独立的图片通道：端点、模型、返回（译图）都和文本翻译不同，
        // 所以不复用 _translation；key 仍然读同一个 secret，和文本 provider 完全一致。
        _imageProvider = new QwenMtImageTranslator(
            () => _secrets.LoadSecret(QwenMtProvider.ApiKeySecretName));
        _imageWindow = new ImageResultWindow();
        _imageCoordinator = new ImageTranslationCoordinator(
            _imageProvider,
            () => _settings,
            result => _imageWindow?.ShowResult(
                result.JpegBytes,
                $"{result.SourceLanguage} → {result.TargetLanguage} · {result.Model} · {result.ElapsedMs}ms"),
            message => _tray?.ShowBalloon("截图翻译失败", message));
        _tray.ScreenshotTranslateRequested += RunScreenshotTranslation;

        // 第二个热键服务：HotkeyService 每个实例有自己的 message-only 窗口，
        // 取词热键(Register/重注册)完全不受影响。
        // 失败提示必须排在 _tray.Show() 之后：ShowBalloon 在图标还没添加时直接返回。
        _imageHotkey = new HotkeyService();
        _imageHotkey.Pressed += RunScreenshotTranslation;
        if (!_imageHotkey.Register(_settings.ImageHotkey))
        {
            _tray.ShowBalloon("Fast Translation", $"截图翻译热键 {_settings.ImageHotkey} 被占用,请到设置里换一个");
        }

        _mainWindow = new MainWindow(
            _translation,
            () => _settings,
            _settingsService,
            _secrets,
            ShowSettings,
            enabled =>
            {
                _captureEnabled = enabled;
                _tray?.SetTooltip(enabled ? "Fast Translation — 取词已开启" : "Fast Translation — 已暂停");
            },
            () => _captureEnabled);
        _mainWindow.Closing += (_, args) =>
        {
            if (_reallyExiting) return;
            args.Cancel = true;      // close button hides to tray; real exit is 托盘 → 退出
            _mainWindow.Hide();
        };
        _mainWindow.Show();

        if (!_settings.FirstRunCompleted)
        {
            // onboarding exists to collect the API key; once a key is present it must never nag again
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

        // updater last: it only schedules a timer, and a check that runs before the UI exists
        // would have nowhere to put its dialog
        _updates = new UpdateCoordinator(
            Dispatcher,
            PresentUpdate,
            // a manual check must be visible: Win11 notification settings swallow tray balloons.
            // a themed window rather than MessageBox — the native one ignores the palette (v38)
            ShowNotice,
            ExitApp);
        _updates.Start();

        // startup warmup (user feedback: dead period before the app became usable):
        // 1) UIA client cold-init on a background thread  2) overlay HWNDs pre-created
        System.Threading.Tasks.Task.Run(() =>
        {
            try { UiaSelectionReader.Warmup(); } catch { }
        });
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            try { _toolbar?.Prewarm(); _card?.Prewarm(); } catch { }
        });

        // QA hook: FT_UITEST=dropdown -> auto-open the language dropdown after startup
        // (environment-gated, no input injection needed when the desktop is busy)
        var uitest = Environment.GetEnvironmentVariable("FT_UITEST");
        if (uitest == "caret")
        {
            var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            uiTimer.Tick += (_, _) =>
            {
                uiTimer.Stop();
                try
                {
                    var box = _mainWindow?.InputBox;
                    if (box is null) return;
                    box.Focus();
                    System.Windows.Input.Keyboard.Focus(box);
                    var rect = box.GetRectFromCharacterIndex(0);
                    var hint = _mainWindow?.FindName("InputHint") as System.Windows.Controls.TextBlock;
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
            // deterministic click-path probe: which element would a real mouse hit?
            var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            uiTimer.Tick += (_, _) =>
            {
                uiTimer.Stop();
                try
                {
                    var combo = _mainWindow?.TargetCombo;
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
                try { _mainWindow?.TargetCombo.IsDropDownOpen = true; }
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
                    _card?.ShowResult(new Core.TranslationResult(
                        "the quick brown fox jumps over the lazy dog and keeps running",
                        "那只敏捷的棕色狐狸跳过了那只懒惰的狗,然后继续在草地上奔跑。",
                        "auto", "Chinese", "qwen-mt-flash", 531, false),
                        CapturedSelection.Empty(new ScreenPoint(200, 260)));
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"uitest: {ex}"); }
            };
            uiTimer.Start();
        }
        else if (uitest is "notice-confirm")
        {
            // the two-button shape (UpdateWindow's "install location will change" question)
            var uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1800) };
            uiTimer.Tick += (_, _) =>
            {
                uiTimer.Stop();
                _ = Dispatcher.BeginInvoke(() => NoticeWindow.Confirm(_mainWindow,
                    "安装位置会变", "当前程序不在默认安装位置。安装新版本会把它装到默认位置,并保留现有设置。继续吗?"));
            };
            uiTimer.Start();
        }
        else if (uitest is "notice-settings")
        {
            // FT_UITEST=notice-settings -> open the settings page, then raise the notice the way a
            // manual check does: it must land on the page that asked, not on the window underneath
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
            // FT_UITEST=notice[-link] -> open the update notice so a screenshot can prove the
            // dialog follows the palette; nobody clicks a real update check on a QA box
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
        // fresh capture from mouse-up (highest success); re-read once, then clipboard fallback.
        // UIA re-read runs on a worker thread (ReadAt can block ~550 ms — bugbot P1 UI freeze)
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
            // the user may have switched windows between selecting and clicking 复制 —
            // clicking the button IS the intent to grab that text, so bring the source
            // window back to the foreground before Ctrl+C (v21: Qwen-page fallback)
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
        if (string.IsNullOrEmpty(capture.Text)) return; // nothing selected — stay silent
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
        // resolve the real foreground process: an empty name would bypass the terminal
        // guard (IsClipboardRisky) and let Ctrl+C kill a running command (bugbot P1)
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
            // non-modal notice (a MessageBox froze the app once); no clipboard side effects
            _card?.ShowNotice(replace ? "当前内容不能替换诶,换一个试试" : "当前内容不能插入诶,换一个试试");
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
    /// The window an update dialog belongs on: whichever of ours the user is working in. A check
    /// can start from the settings window, which is modal over the main window — owning the
    /// dialog by the main window dropped it on the wrong page (user report 2026-10: 设置页点检查更新,
    /// 弹窗盖在使用页上). No active window means the check came from the tray; the app is usually
    /// hidden there, so there is no owner and the dialog goes to the screen centre.
    /// </summary>
    private Window? DialogOwner()
    {
        foreach (var window in Windows.OfType<Window>())
        {
            if (window.IsVisible && window.IsActive) return window;
        }
        return _mainWindow is { IsVisible: true } ? _mainWindow : null;
    }

    private void ShowSettings()
    {
        var window = new SettingsWindow(_settings, _settingsService, _secrets, _hotkey!,
            () => _updates?.CheckAsync(interactive: true));
        // anchor the page to the main window: as a loose top-level window it lands at the screen
        // centre and a dialog opened from it has no visible link back to the app (v39)
        if (_mainWindow is { IsVisible: true, WindowState: not WindowState.Minimized } owner)
        {
            window.Owner = owner;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        window.ShowDialog();
    }

    /// <summary>
    /// The single entry point for "a newer version exists". Only a confirmed, verified
    /// download reaches the caller, and only then does the app hand over to the installer and
    /// quit — the installer starts the new build itself, so coming back here would race it.
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
            // an ownerless CenterOwner window lands at an arbitrary spot; the app is usually
            // hidden in the tray when the timer fires
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
        // a successful ApplyUpdate already exited the app; nothing after this runs
    }

    private void ExitApp()
    {
        _reallyExiting = true;
        _toolbar?.Dispose();
        _card?.Dispose();
        _imageCoordinator?.Dispose();
        _imageProvider?.Dispose();
        _imageWindow?.Dispose();
        _imageHotkey?.Dispose();
        _coordinator?.Dispose();
        _capture?.Dispose();
        _hotkey?.Dispose();
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

    // ----- IResultPresenter -----
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
            return null; // tray falls back to its drawn glyph
        }
    }
}
