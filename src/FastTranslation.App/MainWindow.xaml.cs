using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FastTranslation.App.Pages;
using FastTranslation.Core;
using FastTranslation.Core.Account;
using FastTranslation.Core.Notes;
using FastTranslation.Platform.Windows;
using FastTranslation.Providers.QwenMt;

namespace FastTranslation.App;

/// <summary>
/// The single window (v48, design D13): a sidebar that never changes, and one page host that
/// swaps content. All the translation work lives in <see cref="MainPage"/>; this class is a shell.
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

    private readonly Dictionary<MainPageId, UserControl> _pages = [];
    private readonly Dictionary<NoteKind, NotesPage> _shelves = [];
    private MainPage? _home;
    private PlanPage? _plan;
    private SettingsPage? _settingsPage;
    private ReviewPage? _reviewPage;
    private MainPageId _current = MainPageId.Home;
    private bool _suppressCaptureToggle;
    private bool _ready;

    /// <summary>Forwarded from the home page so App never has to reach into a page instance.</summary>
    public event Action? SettingsRequested;

    /// <summary>Screenshot capture is owned by App; the home page only asks for it.</summary>
    public event Action? ScreenshotRequested;

    /// <summary>App's FT_UITEST hooks read the home page's controls; Build() keeps that safe before show.</summary>
    public MainPage PageHome => Build(MainPageId.Home) as MainPage ?? throw new InvalidOperationException("Home is a MainPage.");

    public MainWindow(
        TranslationService translation,
        AccountCoordinator account,
        Func<AppSettings> settings,
        SettingsService settingsService,
        ISecretStore secrets,
        HotkeyService hotkey,
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
        _checkUpdates = checkUpdates;
        _setCaptureEnabled = setCaptureEnabled;
        _isCaptureEnabled = isCaptureEnabled;

        // The capture switch raises Checked while InitializeComponent applies IsChecked="True",
        // long before the callbacks exist. Everything that touches them waits for this flag.
        _ready = true;
        ApplyCaptureState();
        ShowPage(MainPageId.Home);
    }

    /// <summary>Shown in the sidebar footer so the signed-in state is visible without opening anything.</summary>
    public void RefreshAccountStatus(string? email, string? planDisplay)
    {
        AccountStatus.Text = email is null
            ? "账号:未登录"
            : "账号:" + email + (string.IsNullOrEmpty(planDisplay) ? string.Empty : "(" + planDisplay + ")");
        PlanLabel.Text = email is null || string.IsNullOrEmpty(planDisplay) ? string.Empty : planDisplay;
    }

    /// <summary>
    /// Sidebar count of cards waiting today. Best effort: signed out or offline just means no badge,
    /// and the review window says why on its own. Never awaited from <see cref="ShowPage"/>: a page
    /// switch must not wait on the network.
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
            // a badge is decoration; an exception here must not escape onto the Dispatcher
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
    /// Pages are built lazily and cached: the settings page alone pulls in five groups of controls,
    /// and most sessions never open it (design D13).
    /// </summary>
    private UserControl Build(MainPageId page)
    {
        if (_pages.TryGetValue(page, out var existing)) return existing;
        var built = Create(page);
        _pages[page] = built;
        return built;
    }

    private UserControl Create(MainPageId page) => page switch
    {
        MainPageId.Home => Home(),
        MainPageId.Word => Notes(NoteKind.Word),
        MainPageId.Phrase => Notes(NoteKind.Phrase),
        MainPageId.Sentence => Notes(NoteKind.Sentence),
        MainPageId.Review => Review(),
        MainPageId.Plan => Plan(),
        MainPageId.Settings => Settings(),
        _ => Placeholder(page),
    };

    /// <summary>
    /// The three shelves are the same page with a different filter, so they are cached per kind:
    /// switching 单词 → 短语 → 单词 must not rebuild (and re-fetch) the same list.
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
    /// 复习 is a page now (v52, design D16): a separate window made the drill feel like leaving the
    /// app. The page keeps its own session across navigation, so it is built once and reused.
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
    /// Unrouted ids fall back here; every page in <see cref="MainPageId"/> has a factory above.
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

    public void ShowPage(MainPageId page)
    {
        // Rebuilding the same page would throw away whatever is typed in it, so a cached page is
        // only refreshed — and that refresh is also the first-visit load, which is why it runs
        // after Build as well: a page that was just constructed has never read anything.
        if (_current == page && PageHost.Content is not null)
        {
            RefreshPage(page);
            return;
        }

        _current = page;
        PageHost.Content = Build(page);
        SyncSidebar();
        RefreshPage(page);
    }

    /// <summary>
    /// Re-reads whatever a page shows from settings/account/server. Runs on every visit, including
    /// the one that just built the page.
    /// </summary>
    private void RefreshPage(MainPageId page)
    {
        switch (page)
        {
            case MainPageId.Plan: _plan?.Reload(); break;
            case MainPageId.Settings: _settingsPage?.ReloadFromSettings(); break;
            case MainPageId.Word: _shelves.GetValueOrDefault(NoteKind.Word)?.Reload(); break;
            case MainPageId.Phrase: _shelves.GetValueOrDefault(NoteKind.Phrase)?.Reload(); break;
            case MainPageId.Sentence: _shelves.GetValueOrDefault(NoteKind.Sentence)?.Reload(); break;
            // a sitting in progress survives a detour to a shelf and back; only the choose mode refetches
            case MainPageId.Review: _reviewPage?.Reload(); break;
            case MainPageId.Home: _home?.RefreshKeyStatus(); break;
        }
    }

    private void SyncSidebar()
    {
        foreach (var button in SidebarItems.Children.OfType<Button>())
        {
            var active = Enum.TryParse<MainPageId>(button.Tag as string, true, out var id) && id == _current;
            button.Style = (Style)FindResource(active ? "SidebarItemActive" : "SidebarItem");
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

    /// <summary>Writes the switch from state without letting the write ring back into the callback.</summary>
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
