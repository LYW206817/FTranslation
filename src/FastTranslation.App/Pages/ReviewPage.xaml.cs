using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FastTranslation.Core.Account;
using FastTranslation.Core.Notes;

namespace FastTranslation.App.Pages;

/// <summary>
/// 复习是一个页面了（v52，设计 D16）。两种模式放在同一页里，因为以前独立窗口让复习
/// 感觉像离开了应用：先列出今天等着复习的卡片、每张带一个复选框（译文可见——你是在挑
/// 要练什么，不是猜什么），然后就在页面自己的内容区里逐张卡片进行练习。
///
/// 页面不做排程：下次到期日由服务端的 <see cref="ReviewSchedule"/> 决定，这里只转发
/// 评分并推进 <see cref="ReviewSession"/>。
/// </summary>
public partial class ReviewPage : UserControl
{
    /// <summary>服务端默认值：每天混入多少张还没见过的卡。</summary>
    private const int NewLimit = 20;

    /// <summary>服务端默认值：一次练习要多少张卡。</summary>
    private const int QueueLimit = 50;

    private readonly AccountCoordinator _account;
    private readonly ObservableCollection<ReviewPickItem> _picks = [];

    private ReviewSession? _session;
    private bool _revealed;
    private bool _grading;
    private bool _syncingPicks;
    private bool _subscribed;
    private Window? _host;
    private bool _loaded;

    /// <summary>用户想改去单词本时触发；导航由外壳负责。</summary>
    public event Action? NotesRequested;

    /// <summary>侧边栏角标下的队列发生变化（评分、跳过、放弃）时触发。</summary>
    public event Action? QueueSettled;

    public ReviewPage(AccountCoordinator account)
    {
        InitializeComponent();
        _account = account;
        PickList.ItemsSource = _picks;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private bool InSession => _session is { IsFinished: false };

    private void OnLoaded(object _, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            _account.Changed += OnAccountChanged;
            _subscribed = true;
        }

        // 无论焦点在哪，快捷键都必须生效：点过侧边栏后焦点留在那个按钮上，
        // 页面自己就永远收不到 Space。从窗口层隧道下来才能兜住。
        if (_host is null && Window.GetWindow(this) is { } host)
        {
            _host = host;
            host.PreviewKeyDown += OnHostPreviewKeyDown;
        }

        if (!_loaded)
        {
            _loaded = true;
            Reload();
        }
    }

    private void OnUnloaded(object _, RoutedEventArgs e)
    {
        if (_host is not null)
        {
            _host.PreviewKeyDown -= OnHostPreviewKeyDown;
            _host = null;
        }
    }

    private void OnAccountChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (IsLoaded && !InSession) Reload();
    });

    /// <summary>
    /// 外壳每次把本页切到前台时调用。练习进行中不能把会话丢掉（用户可能只是瞟了一眼
    /// 某个词又回来），所以只有选题模式会重新拉取。
    /// </summary>
    public void Reload()
    {
        if (InSession)
        {
            RenderCard();
            return;
        }

        // 已结束的练习停留在完成页：重新进入页面正是用户想挑下一批的时候，
        // 重新拉取会悄悄替掉这个决定。
        if (_session is { IsFinished: true } && SessionHost.Visibility == Visibility.Visible)
        {
            ShowDone();
            return;
        }

        _ = LoadPicksAsync();
    }

    private async Task LoadPicksAsync()
    {
        if (_account.Session is null)
        {
            ShowPickMessage("登录后就能复习存下来的单词和句子。");
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var dayStart = now - (now % 86_400L);
        var result = await _account.ReviewQueueAsync(dayStart, NewLimit, QueueLimit, CancellationToken.None);
        if (!result.Succeeded)
        {
            ShowPickMessage(EmptyMessage(result.Error!));
            return;
        }

        var queue = result.Value!;
        _picks.Clear();
        // 到期优先：今天该复习的卡排在全新的卡前面。
        foreach (var note in queue.All)
        {
            _picks.Add(new ReviewPickItem(note));
        }

        ShowPick();
    }

    private void ShowPick()
    {
        PickHost.Visibility = Visibility.Visible;
        SessionHost.Visibility = Visibility.Collapsed;
        DonePanel.Visibility = Visibility.Collapsed;
        _session = null;
        UpdatePickState();
    }

    private void ShowPickMessage(string message)
    {
        _picks.Clear();
        ShowPick();
        PickEmpty.Text = message;
        PickEmpty.Visibility = Visibility.Visible;
        UpdatePickState();
    }

    /// <summary>让计数器、开始按钮和「全选」框三者互相保持一致。</summary>
    private void UpdatePickState()
    {
        var checkedCount = _picks.Count(p => p.IsChecked);
        PickHint.Text = _picks.Count == 0
            ? string.Empty
            : "勾选今天要看的内容,再点开始复习";
        CountLabel.Text = _picks.Count == 0
            ? string.Empty
            : $"已选 {checkedCount} / {_picks.Count} 条";
        StartButton.IsEnabled = checkedCount > 0;
        StartButton.Content = checkedCount > 0 ? $"开始复习({checkedCount})" : "开始复习";

        _syncingPicks = true;
        try
        {
            SelectAll.IsChecked = _picks.Count > 0 && checkedCount == _picks.Count;
        }
        finally
        {
            _syncingPicks = false;
        }
    }

    private void Pick_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncingPicks) return;
        UpdatePickState();
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        if (_syncingPicks) return;
        var wanted = SelectAll.IsChecked == true;
        foreach (var pick in _picks) pick.IsChecked = wanted;
        UpdatePickState();
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _picks.Where(p => p.IsChecked).Select(p => p.Note).ToList();
        if (chosen.Count == 0) return;

        _session = new ReviewSession(chosen);
        PickHost.Visibility = Visibility.Collapsed;
        SessionHost.Visibility = Visibility.Visible;
        DonePanel.Visibility = Visibility.Collapsed;
        RenderCard();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadPicksAsync();

    private void RenderCard()
    {
        var note = _session?.Current;
        if (note is null)
        {
            ShowDone();
            return;
        }

        DonePanel.Visibility = Visibility.Collapsed;
        CardHost.Visibility = Visibility.Visible;
        GradeBar.Visibility = Visibility.Visible;
        SkipButton.Visibility = Visibility.Visible;
        SessionHint.Visibility = Visibility.Visible;
        _revealed = false;
        // 单词看一眼就够，句子要读；两者用同样的尺寸会浪费卡片。
        PromptText.FontSize = note.Kind == NoteKind.Word ? 30 : 21;
        PromptText.Text = note.SourceText;
        AnswerPanel.Visibility = Visibility.Collapsed;
        RevealMask.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Collapsed;
        ProgressText.Text = $"第 {_session!.Completed + 1} / {_session.Total} 条";
        CountLabel.Text = $"今日复习 · {_session.Total} 条";
        SetGradeEnabled(false);
        RefreshNextLabels(note);
    }

    /// <summary>每个档位下那行「下次什么时候再见」的小字，取自真实排程。</summary>
    private void RefreshNextLabels(Note note)
    {
        ForgotNext.Text = NextLabel(note, ReviewGrade.Forgot);
        FuzzyNext.Text = NextLabel(note, ReviewGrade.Fuzzy);
        FamiliarNext.Text = NextLabel(note, ReviewGrade.Familiar);
        KnownNext.Text = NextLabel(note, ReviewGrade.Known);
    }

    private static string NextLabel(Note note, ReviewGrade grade) =>
        ReviewSchedule.IntervalLabel(ReviewSchedule.NextStage(note.Stage, grade));

    private void Reveal_Click(object sender, MouseButtonEventArgs e) => Reveal();

    private void Reveal()
    {
        if (!InSession || _revealed || _session!.Current is not { } note) return;
        _revealed = true;
        RevealMask.Visibility = Visibility.Collapsed;
        AnswerPanel.Visibility = Visibility.Visible;
        AnswerText.Text = note.TargetText;
        OriginText.Text = note.OriginLine;
        AnnotationText.Text = note.Annotation;
        AnnotationText.Visibility = string.IsNullOrWhiteSpace(note.Annotation)
            ? Visibility.Collapsed
            : Visibility.Visible;
        SetGradeEnabled(true);
    }

    private async void Grade_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string wire }) return;
        await GradeAsync(wire);
    }

    private async Task GradeAsync(string wire)
    {
        // 答案没显示就评分等于瞎猜，所以在那之前按钮一直是禁用的。
        if (!_revealed || _grading || _session is null || _session.Current is not { } note) return;
        if (!ReviewSchedule.TryParseGrade(wire, out var grade)) return;

        _grading = true;
        SetGradeEnabled(false);
        try
        {
            var result = await _account.GradeNoteAsync(note.Id, grade, CancellationToken.None);
            if (!result.Succeeded)
            {
                // 卡片留在原地：评分失败不能让用户把卡弄丢（设计 §9）。
                StatusText.Text = result.Error!.Display;
                StatusText.Visibility = Visibility.Visible;
                SetGradeEnabled(true);
                return;
            }

            QueueSettled?.Invoke();
            if (!_session.Advance()) ShowDone();
            else RenderCard();
        }
        finally
        {
            _grading = false;
            _revealed = false;
        }
    }

    private void Skip_Click(object _, RoutedEventArgs e) => Skip();

    /// <summary>跳过不写任何东西：卡片仍然到期，下次还会回来（设计 §8.4）。</summary>
    private void Skip()
    {
        if (_session is null || _session.Current is null) return;
        if (!_session.Advance()) ShowDone();
        else RenderCard();
    }

    private void ShowDone()
    {
        DonePanel.Visibility = Visibility.Visible;
        CardHost.Visibility = Visibility.Collapsed;
        GradeBar.Visibility = Visibility.Collapsed;
        // 练习结束之后，既没有可跳过的，也没有要提示的快捷键
        SkipButton.Visibility = Visibility.Collapsed;
        SessionHint.Visibility = Visibility.Collapsed;
        ProgressText.Text = string.Empty;
        CountLabel.Text = "今日复习 · 结束";
        DoneText.Text = _session is { Total: > 0 }
            ? $"这轮看了 {_session.Total} 条。点「再选一批」看看还有没有到期的。"
            : string.Empty;
        SetGradeEnabled(false);
        QueueSettled?.Invoke();
    }

    private void End_Click(object sender, RoutedEventArgs e)
    {
        _session = null;
        _ = LoadPicksAsync();
        QueueSettled?.Invoke();
    }

    private void Again_Click(object sender, RoutedEventArgs e)
    {
        _session = null;
        _ = LoadPicksAsync();
    }

    private void Notes_Click(object sender, RoutedEventArgs e) => NotesRequested?.Invoke();

    private void SetGradeEnabled(bool enabled)
    {
        ForgotButton.IsEnabled = enabled;
        FuzzyButton.IsEnabled = enabled;
        FamiliarButton.IsEnabled = enabled;
        KnownButton.IsEnabled = enabled;
    }

    private void OnHostPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SessionHost.Visibility != Visibility.Visible) return;

        // 完成页上没有等待中的内容，在这里按 Space 只会把后面的列表滚走。
        if (_session is not { IsFinished: false })
        {
            if (e.Key == Key.Escape) { End_Click(sender, e); e.Handled = true; }
            return;
        }

        if (e.Key == Key.Space) { Reveal(); e.Handled = true; return; }

        if (!_grading)
        {
            switch (e.Key)
            {
                case Key.D1 or Key.NumPad1: _ = GradeAsync("forgot"); e.Handled = true; return;
                case Key.D2 or Key.NumPad2: _ = GradeAsync("fuzzy"); e.Handled = true; return;
                case Key.D3 or Key.NumPad3: _ = GradeAsync("familiar"); e.Handled = true; return;
                case Key.D4 or Key.NumPad4: _ = GradeAsync("known"); e.Handled = true; return;
                case Key.PageDown or Key.Right: Skip(); e.Handled = true; return;
            }
        }

        if (e.Key == Key.Escape)
        {
            End_Click(sender, e);
            e.Handled = true;
        }
    }

    private static string EmptyMessage(AccountError error) => error.Code switch
    {
        AccountErrorCode.NotSignedIn => "登录后就能复习存下来的单词和句子。",
        AccountErrorCode.SubscriptionRequired => "复习要订阅之后才能用,去套餐页看看。",
        _ => error.Display,
    };
}

/// <summary>
/// 选题列表里的一行。做成 view model 只是因为复选框需要能表达「不要这张」，
/// 而又不能改到笔记本身。
/// </summary>
public sealed class ReviewPickItem : INotifyPropertyChanged
{
    private bool _isChecked = true;

    public ReviewPickItem(Note note) => Note = note;

    public Note Note { get; }

    public string KindLabel => Note.KindDisplay;

    public string SourceText => Note.SourceText;

    public string TargetText => Note.TargetText;

    public bool IsNew => Note.Stage == 0;

    public string MetaLine => IsNew ? "今日新词 · 还没复习过" : "今天到期";

    public Visibility NewBadgeVisibility => IsNew ? Visibility.Visible : Visibility.Collapsed;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
