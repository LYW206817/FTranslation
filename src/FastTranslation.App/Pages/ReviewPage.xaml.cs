using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FastTranslation.Core.Account;
using FastTranslation.Core.Notes;

namespace FastTranslation.App.Pages;

/// <summary>
/// 复习 as a page (v52, design D16). Two modes in the same page, because a separate window made
/// the review feel like leaving the app: first a list of what is waiting today with a checkbox per
/// card (translations visible — you are choosing what to drill, not what to guess), then the drill
/// itself, card by card, in the page's own content area.
///
/// The page owns no scheduling: <see cref="ReviewSchedule"/> on the server decides the next due
/// date, this only forwards the grade and advances <see cref="ReviewSession"/>.
/// </summary>
public partial class ReviewPage : UserControl
{
    /// <summary>Server default: how many not-yet-seen cards to mix in per day.</summary>
    private const int NewLimit = 20;

    /// <summary>Server default: how many cards one sitting asks for.</summary>
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

    /// <summary>Raised when the user wants the notebook instead; the shell owns navigation.</summary>
    public event Action? NotesRequested;

    /// <summary>Raised when the queue changed under the sidebar badge (graded, skipped, abandoned).</summary>
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

        // The keys have to work no matter where focus sits: clicking the sidebar leaves it on that
        // button, so the page itself would never see Space. Tunnelling from the window catches it.
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
    /// Called every time the shell brings this page to the front. Mid-sitting it must not throw the
    /// session away (the user may have glanced at a word and come back), so only the choose mode
    /// refetches.
    /// </summary>
    public void Reload()
    {
        if (InSession)
        {
            RenderCard();
            return;
        }

        // A finished session stays on its done screen: re-entering the page is exactly when the
        // user wants to pick the next batch, and refetching would silently replace that decision.
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
        // Due first: a card that was promised for today outranks a brand-new one.
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

    /// <summary>Keeps the counter, the start button and the 全选 box honest with each other.</summary>
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
        // A word is one glance, a sentence needs reading; sizing both the same wastes the card.
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

    /// <summary>The small "when will I see this again" line under each grade, from the real schedule.</summary>
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
        // Grading before the answer is visible would be guessing, so the buttons stay dead until then.
        if (!_revealed || _grading || _session is null || _session.Current is not { } note) return;
        if (!ReviewSchedule.TryParseGrade(wire, out var grade)) return;

        _grading = true;
        SetGradeEnabled(false);
        try
        {
            var result = await _account.GradeNoteAsync(note.Id, grade, CancellationToken.None);
            if (!result.Succeeded)
            {
                // The card stays put: a failed grade must not cost the user the card (design §9).
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

    /// <summary>Skipping writes nothing: the card is still due, so it comes back next time (design §8.4).</summary>
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
        // nothing to skip and no shortcuts to hint at once the sitting is over
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

        // Nothing is waiting on the done screen, and Space there would scroll the list behind it.
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
/// One row of the choose list. A view model only because a checkbox has to be able to say "not this
/// one" without touching the note itself.
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
