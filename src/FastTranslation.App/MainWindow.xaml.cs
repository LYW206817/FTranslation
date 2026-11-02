using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FastTranslation.App.Pages;
using FastTranslation.Core;
using FastTranslation.Core.Account;
using FastTranslation.Core.Documents;
using FastTranslation.Core.Notes;
using FastTranslation.Platform.Windows;
using FastTranslation.Providers.QwenMt;

namespace FastTranslation.App;

/// <summary>
/// 唯一的窗口（v48，设计 D13）：一个永不变化的侧边栏，加一个负责换内容的页面宿主。
/// 所有翻译工作都在 <see cref="MainPage"/> 里；这个类只是外壳。
/// </summary>
public partial class MainWindow : Window
{
    private readonly TranslationService _translation;
    private readonly AccountCoordinator _account;
    private readonly Func<AppSettings> _settings;
    private readonly SettingsService _settingsService;
    private readonly ISecretStore _secrets;
    private readonly HotkeyService _hotkey;
    private readonly Action? _checkUpdates;
    private readonly Action<bool> _setCaptureEnabled;
    private readonly Func<bool> _isCaptureEnabled;
    private readonly DocumentTranslationCoordinator _documents;

    private readonly Dictionary<MainPageId, UserControl> _pages = [];
    private readonly Dictionary<NoteKind, NotesPage> _shelves = [];
    private MainPage? _home;
    private AccountPage? _accountPage;
    private PlanPage? _plan;
    private SettingsPage? _settingsPage;
    private ReviewPage? _reviewPage;
    private DocumentPage? _documentPage;
    private SpeechPage? _speechPage;

    // 额度提示由 App 取数后转发，页面自己不碰账号
    public void SetSpeechQuota(string text) => _speechPage?.SetQuota(text);
    private MainPageId _current = MainPageId.Home;
    private bool _suppressCaptureToggle;
    private bool _ready;

    /// <summary>从首页转发过来，这样 App 不必伸手去拿某个页面实例。</summary>
    public event Action? SettingsRequested;

    /// <summary>截图取词归 App 所有；首页只是发起请求。</summary>
    public event Action? ScreenshotRequested;

    /// <summary>App 的 FT_UITEST 钩子会读首页的控件；Build() 保证在显示前已就绪。</summary>
    public MainPage PageHome => Build(MainPageId.Home) as MainPage ?? throw new InvalidOperationException("Home is a MainPage.");

    public MainWindow(
        TranslationService translation,
        AccountCoordinator account,
        Func<AppSettings> settings,
        SettingsService settingsService,
        ISecretStore secrets,
        HotkeyService hotkey,
        DocumentTranslationCoordinator documents,
        Action<bool> setCaptureEnabled,
        Func<bool> isCaptureEnabled,
        Action? checkUpdates = null)
    {
        InitializeComponent();
        WindowChrome.Apply(this);
        if (Environment.GetEnvironmentVariable("FT_UITEST") is not null)
        {
            PreviewMouseLeftButtonDown += (_, _) =>
                System.IO.File.AppendAllText(System.IO.Path.GetTempPath() + "ft-uitest-errors.log", $"{DateTime.Now:HH:mm:ss.fff} window PreviewMouseDown\n");
        }
        _translation = translation;
        _account = account;
        _settings = settings;
        _settingsService = settingsService;
        _secrets = secrets;
        _hotkey = hotkey;
        _documents = documents;
        _checkUpdates = checkUpdates;
        _setCaptureEnabled = setCaptureEnabled;
        _isCaptureEnabled = isCaptureEnabled;

        // InitializeComponent 应用 IsChecked="True" 时就会触发 Checked，那时回调还不存在。
        // 所有会碰这些回调的代码都等这个标志。
        _ready = true;
        ApplyCaptureState();
        ShowPage(MainPageId.Home);

        // v86：页面在空闲时先建好并量一遍。点侧栏那一下原本要现场构造控件树再排版——设置页
        // 要拉进五组控件，实测从点到第一帧 185ms，这就是"点一下得等一下"的来源。放到
        // ApplicationIdle 里做，窗口早已经画出来了，用户看不到，也不拖慢启动。
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, PrebuildPages);
    }

    /// <summary>
    /// 把所有还没被打开过的页面构造出来并量一遍。控件模板的实例化与首次排版是切页开销的
    /// 大头，而它们和用户点哪一项无关——先付掉，点击时就只剩换 Content。
    /// 一次跑完，但放在窗口排好版之后的那一个空闲时刻；分成多次空闲时刻反而更糟——
    /// 用户一点侧栏，新的空闲任务就排到预热后面，越点越晚。
    /// </summary>
    private void PrebuildPages()
    {
        var size = new Size(PageHost.ActualWidth, PageHost.ActualHeight);
        if (size.Width < 1 || size.Height < 1)
        {
            // 窗口还没排出尺寸：这次收工就再没机会预热了，等下一帧重试。
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, PrebuildPages);
            return;
        }

        foreach (var id in Enum.GetValues<MainPageId>())
        {
            if (id == _current) continue;
            // 量一遍而不是塞进 PageHost：塞进去会真的画出来，用户会看到页面自己翻。
            // 只量不画就够——模板实例化与首次排版正是切页那一 nib 的大头。
            var page = Build(id);
            page.Measure(size);
            page.Arrange(new Rect(size));
        }
    }

    /// <summary>显示在侧边栏底部，这样不打开任何界面也能看到登录状态。</summary>
    public void RefreshAccountStatus(string? email, string? planDisplay)
    {
        AccountStatus.Text = email is null
            ? "账号:未登录"
            : "账号:" + email + (string.IsNullOrEmpty(planDisplay) ? string.Empty : "(" + planDisplay + ")");
        PlanLabel.Text = email is null || string.IsNullOrEmpty(planDisplay) ? string.Empty : planDisplay;
    }

    /// <summary>
    /// 侧边栏上今天等待复习的卡片数。尽力而为：未登录或离线就不显示角标，复习窗口自己会
    /// 说明原因。绝不在 <see cref="ShowPage"/> 里 await：切页面不能等网络。
    /// </summary>
    public async Task RefreshReviewBadgeAsync()
    {
        if (_account.Session is null)
        {
            SetBadge(ReviewBadge, ReviewBadgeText, 0);
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        AccountApiResult<ReviewQueue> result;
        try
        {
            result = await _account.ReviewQueueAsync(now - (now % 86_400L), 20, 50, CancellationToken.None);
        }
        catch (Exception)
        {
            // 角标只是装饰；这里的异常不能逃到 Dispatcher 上
            return;
        }

        SetBadge(ReviewBadge, ReviewBadgeText, result.Succeeded ? result.Value!.Count : 0);
    }

    private static void SetBadge(Border badge, TextBlock text, int count)
    {
        badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        text.Text = count > 99 ? "99+" : count.ToString();
    }

    /// <summary>
    /// 页面按需构建并缓存：光设置页就拉进五组控件，而大多数会话根本不会打开它（设计 D13）。
    /// </summary>
    private UserControl Build(MainPageId page)
    {
        if (_pages.TryGetValue(page, out var existing)) return existing;
        var built = CreatePage(page);
        _pages[page] = built;
        return built;
    }

    /// <summary>
    /// 页面对应表的唯一出处。<see cref="Build"/> 负责缓存，这里只负责"哪个 id 造什么"，
    /// 分成两个方法是因为 switch 的增长速度比缓存逻辑快得多（v72）。
    /// </summary>
    private UserControl CreatePage(MainPageId page) => page switch
    {
        MainPageId.Home => Home(),
        MainPageId.Word => Notes(NoteKind.Word),
        MainPageId.Phrase => Notes(NoteKind.Phrase),
        MainPageId.Sentence => Notes(NoteKind.Sentence),
        MainPageId.Review => Review(),
        MainPageId.Document => Document(),
        MainPageId.Speech => Speech(),
        MainPageId.Account => Account(),
        MainPageId.Plan => Plan(),
        MainPageId.Settings => Settings(),
        _ => Placeholder(page),
    };

    /// <summary>
    /// 三层书架是同一个页面换个过滤条件，所以按种类分别缓存：在 单词 → 短语 → 单词 之间
    /// 切换时，不能重建（并重新拉取）同一份列表。
    /// </summary>
    private NotesPage Notes(NoteKind kind)
    {
        if (_shelves.TryGetValue(kind, out var cached)) return cached;
        var page = new NotesPage(_account, kind);
        page.ReviewRequested += () => ShowPage(MainPageId.Review);
        _shelves[kind] = page;
        return page;
    }

    /// <summary>
    /// 复习现在是一个页面了（v52，设计 D16）：独立窗口让练习感觉像离开了应用。
    /// 页面在导航之间保留自己的会话，所以只构建一次并复用。
    /// </summary>
    private ReviewPage Review()
    {
        if (_reviewPage is not null) return _reviewPage;
        var page = new ReviewPage(_account);
        page.NotesRequested += () => ShowPage(MainPageId.Word);
        page.QueueSettled += () => _ = RefreshReviewBadgeAsync();
        _reviewPage = page;
        return page;
    }

    /// <summary>
    /// 文档翻译页（v72）：一次只跑一份文档，所以页面也是单例——切走再切回来时进度条与
    /// 未翻译清单都还在，用户不会因为点错了一下标签就丢掉跑到一半的作业。
    /// </summary>
    private DocumentPage Document()
    {
        if (_documentPage is not null) return _documentPage;
        _documentPage = new DocumentPage(_documents, _account, _settings, _settingsService);
        return _documentPage;
    }

    private SpeechPage Speech()
    {
        if (_speechPage is not null) return _speechPage;
        _speechPage = new SpeechPage(_settings, _settingsService);
        return _speechPage;
    }

    // v72: 文档作业的三个回调从 App 一路转到这里，再由页面落到控件上。转一层是为了让
    // App 不必知道页面是否已经构造过——窗口藏在托盘里时作业照样能跑完，只是没人画它。
    public void PresentDocument(DocumentJobResult result) => _documentPage?.Present(result);

    public void ReportDocumentError(string message) => _documentPage?.ReportError(message);

    public void ReportDocumentProgress(double fraction, int done, int total) =>
        _documentPage?.ReportProgress(fraction, done, total);

    /// <summary>
    /// 没有匹配到的 id 会落到这里；上面每个 <see cref="MainPageId"/> 里的页面都有对应工厂。
    /// </summary>
    private UserControl Placeholder(MainPageId page) => new()
    {
        Background = (Brush)FindResource("WindowBgBrush"),
        Content = new TextBlock
        {
            Text = page + " (建设中)",
            FontSize = 14,
            Foreground = (Brush)FindResource("TextMutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        },
    };

    private UserControl Home()
    {
        if (_home is not null) return _home;
        var page = new MainPage(_translation, _account, _settings, _settingsService, _secrets);
        page.SettingsRequested += () => SettingsRequested?.Invoke();
        page.ScreenshotRequested += () => ScreenshotRequested?.Invoke();
        page.PlanRequested += () => ShowPage(MainPageId.Plan);
        _home = page;
        return page;
    }

    /// <summary>
    /// 账号和套餐现在是两个页面：身份、名字和密码归账号，价格、计量和激活码归套餐。
    /// 两者之间的连接是一个事件，而不是共享实例。
    /// </summary>
    private AccountPage Account()
    {
        if (_accountPage is not null) return _accountPage;
        var page = new AccountPage(_account);
        page.PlanRequested += () => ShowPage(MainPageId.Plan);
        _accountPage = page;
        return page;
    }

    private PlanPage Plan()
    {
        if (_plan is not null) return _plan;
        _plan = new PlanPage(_account);
        return _plan;
    }

    private SettingsPage Settings()
    {
        if (_settingsPage is not null) return _settingsPage;
        _settingsPage = new SettingsPage(_settings(), _settingsService, _secrets, _hotkey, _checkUpdates);
        _settingsPage.Closed += () => ShowPage(MainPageId.Home);
        return _settingsPage;
    }

    /// <summary>切换到指定页面；同一个页面重复调用只刷新内容，不重建控件。</summary>
    public void ShowPage(MainPageId page)
    {
        // 重建同一个页面会丢掉里面已经输入的内容，所以缓存页面只做刷新——而这次刷新
        // 同时也是首次访问的加载，这就是它在 Build 之后也跑的原因：刚构造出来的页面
        // 还从没读过任何东西。
        if (_current == page && PageHost.Content is not null)
        {
            RefreshPage(page);
            return;
        }

        _current = page;
        var content = Build(page);

        // v88：先把侧栏选中态改掉，内容推到下一帧再挂。换页要重画整个内容区（全屏 2548×1400
        // 物理像素），首帧 40–110ms；两件事同一帧做，点击反馈要等这一大块画完才出现，手感就是
        // 「点了没反应」。拆开后点下去当帧只重画侧栏那一条，内容晚一帧到。
        SyncSidebar();
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            // 连点多页时队列里会堆几个挂载：只让最后一次生效，否则会闪回中间的页。
            if (_current == page) PageHost.Content = content;
        });
        RefreshPage(page);
    }

    /// <summary>
    /// 重新读取页面展示的来自设置/账号/服务端的内容。每次访问都会跑，包括刚刚构建出
    /// 页面的那一次。
    /// </summary>
    private void RefreshPage(MainPageId page)
    {
        switch (page)
        {
            case MainPageId.Account: _accountPage?.Reload(); break;
            case MainPageId.Plan: _plan?.Reload(); break;
            case MainPageId.Settings: _settingsPage?.ReloadFromSettings(); break;
            case MainPageId.Word: _shelves.GetValueOrDefault(NoteKind.Word)?.Reload(); break;
            case MainPageId.Phrase: _shelves.GetValueOrDefault(NoteKind.Phrase)?.Reload(); break;
            case MainPageId.Sentence: _shelves.GetValueOrDefault(NoteKind.Sentence)?.Reload(); break;
            // 进行中的练习要能在绕去书架再回来之后存活；只有选题模式会重新拉取
            case MainPageId.Review: _reviewPage?.Reload(); break;
            case MainPageId.Document: _documentPage?.Reload(); break;
            case MainPageId.Home: _home?.RefreshKeyStatus(); break;
        }
    }

    private void SyncSidebar()
    {
        foreach (var button in SidebarItems.Children.OfType<Button>())
        {
            var active = Enum.TryParse<MainPageId>(button.Tag as string, true, out var id) && id == _current;
            SidebarState.SetIsActive(button, active);
        }
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        if (!Enum.TryParse<MainPageId>(tag, ignoreCase: true, out var page)) return;
        ShowPage(page);
    }

    private void CaptureToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready || _suppressCaptureToggle) return;
        if (CaptureToggle.IsChecked is { } enabled)
        {
            CaptureToggle.Content = enabled ? "取词已开启" : "取词已暂停";
            _setCaptureEnabled(enabled);
        }
    }

    public void RefreshCaptureState()
    {
        if (_isCaptureEnabled() == (CaptureToggle.IsChecked == true)) return;
        ApplyCaptureState();
    }

    /// <summary>把开关的显示从状态写出，同时不让这次写入再回环到回调里。</summary>
    private void ApplyCaptureState()
    {
        var enabled = _isCaptureEnabled();
        _suppressCaptureToggle = true;
        try
        {
            CaptureToggle.IsChecked = enabled;
            CaptureToggle.Content = enabled ? "取词已开启" : "取词已暂停";
        }
        finally
        {
            _suppressCaptureToggle = false;
        }
    }
}
