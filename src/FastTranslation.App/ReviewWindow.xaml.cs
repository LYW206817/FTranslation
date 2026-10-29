using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FastTranslation.Core.Account;
using FastTranslation.Core.Notes;
using FastTranslation.Platform.Windows;

namespace FastTranslation.App;

/// <summary>
/// The review window (v48, design D15). A window rather than a page because it owns the keyboard
/// for the whole session: space to reveal, 1-4 to grade. Nothing here decides the next due date —
/// <see cref="ReviewSchedule"/> on the server does that, this only forwards the grade.
/// </summary>
public partial class ReviewWindow : Window
{
    /// <summary>Server default: how many not-yet-seen cards to mix in per day.</summary>
    private const int NewLimit = 20;

    /// <summary>Server default: how many cards one sitting asks for.</summary>
    private const int QueueLimit = 50;

    private readonly AccountCoordinator _account;
    private readonly List<Note> _queue = [];
    private int _index;
    private bool _revealed;
    private bool _grading;

    private Note? Current => _index < _queue.Count ? _queue[_index] : null;

    /// <summary>Lets the shell jump to the notebook from the empty state.</summary>
    public event Action? NotesRequested;

    /// <summary>Raised once the window is gone so the sidebar badge can be recounted.</summary>
    public event Action? QueueSettled;

    public ReviewWindow(AccountCoordinator account)
    {
        InitializeComponent();
        WindowChrome.Apply(this);
        _account = account;
        _account.Changed += OnAccountChanged;
        Closed += (_, _) =>
        {
            _account.Changed -= OnAccountChanged;
            QueueSettled?.Invoke();
        };
        Loaded += async (_, _) =>
        {
            // WPF hands the initial focus to the first focusable child, which is 跳过 — then Space
            // would skip instead of revealing. Focus the window itself so the shortcuts reach
            // OnKeyDown until the user deliberately tabs to a button.
            Focus();
            await LoadAsync();
        };
    }

    private void OnAccountChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (IsLoaded) _ = LoadAsync();
    });

    private async Task LoadAsync()
    {
        if (_account.Session is null)
        {
            ShowEmpty("登录后就能复习存下来的单词和句子。", showNotes: false);
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var dayStart = now - (now % 86_400L);
        var result = await _account.ReviewQueueAsync(dayStart, NewLimit, QueueLimit, CancellationToken.None);
        if (!result.Succeeded)
        {
            ShowEmpty(EmptyMessage(result.Error!), showNotes: true);
            return;
        }

        _queue.Clear();
        // Due first: a card that was promised for today outranks a brand-new one.
        _queue.AddRange(result.Value!.All);
        _index = 0;
        Render();
    }

    private void Render()
    {
        var note = Current;
        if (note is null)
        {
            ShowEmpty(_queue.Count == 0 ? "今天没有要复习的,明天再来。" : "这一轮过完了。", showNotes: true);
            return;
        }

        EmptyPanel.Visibility = Visibility.Collapsed;
        CardHost.Visibility = Visibility.Visible;
        GradeBar.Visibility = Visibility.Visible;
        SkipButton.Visibility = Visibility.Visible;

        _revealed = false;
        // A word is one glance, a sentence needs reading; sizing both the same wastes the card.
        PromptText.FontSize = note.Kind == NoteKind.Word ? 30 : 21;
        PromptText.Text = note.SourceText;
        AnswerPanel.Visibility = Visibility.Collapsed;
        RevealMask.Visibility = Visibility.Visible;
        StatusText.Visibility = Visibility.Collapsed;
        ProgressText.Text = $"第 {_index + 1} / {_queue.Count} 条";
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
        if (Current is not { } note || _revealed) return;
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
        if (!_revealed || _grading || Current is not { } note) return;
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

            _index++;
            Render();   // reaching the end is Render's own empty state
        }
        finally
        {
            _grading = false;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            Reveal();
            e.Handled = true;
            return;
        }

        if (!_grading)
        {
            switch (e.Key)
            {
                case Key.D1 or Key.NumPad1: _ = GradeAsync("forgot"); e.Handled = true; return;
                case Key.D2 or Key.NumPad2: _ = GradeAsync("fuzzy"); e.Handled = true; return;
                case Key.D3 or Key.NumPad3: _ = GradeAsync("familiar"); e.Handled = true; return;
                case Key.D4 or Key.NumPad4: _ = GradeAsync("known"); e.Handled = true; return;
            }
        }

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void SetGradeEnabled(bool enabled)
    {
        ForgotButton.IsEnabled = enabled;
        FuzzyButton.IsEnabled = enabled;
        FamiliarButton.IsEnabled = enabled;
        KnownButton.IsEnabled = enabled;
    }

    private void ShowEmpty(string message, bool showNotes)
    {
        CardHost.Visibility = Visibility.Collapsed;
        EmptyPanel.Visibility = Visibility.Visible;
        EmptyText.Text = message;
        EmptyNotesButton.Visibility = showNotes ? Visibility.Visible : Visibility.Collapsed;
        // nothing to grade and nothing to skip once the queue is done, so the bar goes away
        GradeBar.Visibility = Visibility.Collapsed;
        SkipButton.Visibility = Visibility.Collapsed;
        ProgressText.Text = string.Empty;
        SetGradeEnabled(false);
    }

    private static string EmptyMessage(AccountError error) => error.Code switch
    {
        AccountErrorCode.NotSignedIn => "登录后就能复习存下来的单词和句子。",
        AccountErrorCode.SubscriptionRequired => "复习要订阅之后才能用,去套餐页看看。",
        _ => error.Display,
    };

    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        // Skipping writes nothing: the card is still due, so it comes back next time (design §8.4).
        if (Current is null) return;
        _index++;
        Render();
    }

    private void Notes_Click(object sender, RoutedEventArgs e)
    {
        NotesRequested?.Invoke();
        Close();
    }
}
