using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FastTranslation.Core.Account;
using FastTranslation.Core.Notes;

namespace FastTranslation.App.Pages;

/// <summary>
/// One shelf of the notebook (单词 / 短语 / 语句). The three shelves are the same rows filtered by
/// <see cref="NoteKind"/>, so this is one page built three times with a different shelf; the sidebar
/// owns the switching that <c>NotesWindow</c> used to do with its own buttons.
///
/// Every mutation goes to the server — there is no local copy. The detail pane can re-file an entry,
/// annotate it, and grade it on the same four-grade scale the review window uses (design D12).
/// </summary>
public partial class NotesPage : UserControl
{
    private readonly AccountCoordinator _account;
    private readonly NoteKind _shelf;
    private readonly ObservableCollection<Note> _items = new();
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(320) };

    private Note? _selected;
    private bool _loading;
    private bool _grading;
    private bool _suppressKindChange;
    private bool _subscribed;
    private CancellationTokenSource? _loadCts;
    private string _search = string.Empty;
    private int _total;

    /// <summary>Raised when the user asks for the review window; it reviews the whole queue, not this row.</summary>
    public event Action? ReviewRequested;

    public NotesPage(AccountCoordinator account, NoteKind shelf)
    {
        InitializeComponent();
        _account = account;
        _shelf = shelf;
        NotesList.ItemsSource = _items;
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            _search = SearchBox.Text.Trim();
            _ = LoadAsync();
        };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object _, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            _account.Changed += OnAccountChanged;
            _subscribed = true;
        }
        // Pages are cached, so coming back to a shelf must not show what the server no longer has
        _ = LoadAsync();
    }

    private void OnUnloaded(object _, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            _account.Changed -= OnAccountChanged;
            _subscribed = false;
        }
        _loadCts?.Cancel();
    }

    /// <summary>Public so App can refresh the visible shelf after a save from the result card.</summary>
    public void Reload() => _ = LoadAsync();

    private void OnAccountChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (!_account.IsSignedIn) Render();
    });

    private async Task LoadAsync()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        if (!_account.IsSignedIn)
        {
            _items.Clear();
            _total = 0;
            Render();
            DetailStatus.Text = string.Empty;
            return;
        }

        _loading = true;
        Render();
        try
        {
            var page = await _account.ListNotesAsync(_shelf, _search, 200, 0, cts.Token);
            if (cts.IsCancellationRequested) return;
            _items.Clear();
            if (page.Succeeded)
            {
                foreach (var note in page.Value!.Items) _items.Add(note);
                _total = page.Value.Total;
                EmptyText.Text = string.IsNullOrEmpty(_search)
                    ? "这一栏还是空的。翻译时点结果卡上的「保存」就会出现在这里。"
                    : "没有匹配「" + _search + "」的条目。";
            }
            else
            {
                _total = 0;
                EmptyText.Text = page.Error!.Display;
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("notes load: " + ex);
            _total = 0;
            EmptyText.Text = "加载失败,稍后再试一次。";
        }
        finally
        {
            if (!cts.IsCancellationRequested)
            {
                _loading = false;
                Render();
            }
        }
    }

    private void Render()
    {
        Headline.Text = NoteKinds.Display(_shelf) + "本" + (_account.IsSignedIn ? string.Empty : "(未登录)");
        CountLabel.Text = _total > 0 ? "共 " + _total + " 条" : string.Empty;
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_loading && _items.Count == 0) EmptyText.Text = "正在加载…";
        if (!_account.IsSignedIn)
        {
            EmptyText.Text = "先登录账号,保存的单词就能跨设备同步。";
            EmptyText.Visibility = Visibility.Visible;
        }

        DetailEmpty.Visibility = _selected is null ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.Visibility = _selected is null ? Visibility.Collapsed : Visibility.Visible;
        var canGrade = _selected is not null && _account.IsSignedIn && !_grading;
        SetGradeEnabled(canGrade);
    }

    private void ShowDetail(Note note)
    {
        DetailSource.Text = note.SourceText;
        DetailTarget.Text = note.TargetText;
        DetailOrigin.Text = note.OriginLine + (string.IsNullOrWhiteSpace(note.Model) ? string.Empty : " · " + note.Model);
        NoteBox.Text = note.Annotation;
        DetailStatus.Text = string.Empty;

        // the grade line doubles as the schedule preview: what was it last time, when is it back
        GradeLine.Text = note.GradeDisplay + " · " + note.DueLine;

        _suppressKindChange = true;
        try
        {
            KindCombo.SelectedIndex = note.Kind switch
            {
                NoteKind.Word => 0,
                NoteKind.Phrase => 1,
                _ => 2,
            };
        }
        finally
        {
            _suppressKindChange = false;
        }
    }

    private void SetGradeEnabled(bool enabled)
    {
        ForgotButton.IsEnabled = enabled;
        FuzzyButton.IsEnabled = enabled;
        FamiliarButton.IsEnabled = enabled;
        KnownButton.IsEnabled = enabled;
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        // typing re-issues the query; the timer keeps one keystroke per 320 ms instead of one per key
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void Refresh_Click(object _, RoutedEventArgs e) => _ = LoadAsync();

    private void Review_Click(object _, RoutedEventArgs e) => ReviewRequested?.Invoke();

    private void Notes_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = NotesList.SelectedItem as Note;
        if (_selected is not null) ShowDetail(_selected);
        Render();
    }

    /// <summary>
    /// Grading from the detail pane is the "mark it while I am here" path (design D12). It patches
    /// the row instead of reloading the list: a reload would drop the selection and scroll to top.
    /// </summary>
    private async void Grade_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || _grading) return;
        if (sender is not Button { Tag: string wire }) return;
        if (!ReviewSchedule.TryParseGrade(wire, out var grade)) return;

        _grading = true;
        SetGradeEnabled(false);
        try
        {
            var result = await _account.GradeNoteAsync(_selected.Id, grade, CancellationToken.None);
            if (result.Succeeded && result.Value is { } updated)
            {
                ReplaceRow(updated);
                ShowDetail(updated);
            }
            else
            {
                GradeLine.Text = "打分没成功,稍后再试一次";
            }
        }
        finally
        {
            _grading = false;
            Render();
        }
    }

    private async void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressKindChange || _selected is not { } note) return;
        var wire = (KindCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (!NoteKinds.TryParse(wire, out var kind) || kind == note.Kind) return;

        var result = await _account.UpdateNoteAsync(note.Id, kind, null, CancellationToken.None);
        if (!result.Succeeded)
        {
            DetailStatus.Text = result.Error!.Display;
            ShowDetail(note); // snap the combo back to what the server still has
            return;
        }
        DetailStatus.Text = "已改到" + NoteKinds.Display(kind) + "本。";
        // re-filing can move the row out of the shelf being viewed, so reload instead of patching
        _selected = null;
        await LoadAsync();
    }

    private async void SaveNote_Click(object _, RoutedEventArgs e)
    {
        if (_selected is not { } note) return;
        SaveNoteButton.IsEnabled = false;
        try
        {
            var result = await _account.UpdateNoteAsync(note.Id, null, NoteBox.Text.Trim(), CancellationToken.None);
            DetailStatus.Text = result.Succeeded ? "备注已保存。" : result.Error!.Display;
            if (result.Succeeded) ReplaceRow(result.Value!);
        }
        finally
        {
            SaveNoteButton.IsEnabled = true;
        }
    }

    private async void Delete_Click(object _, RoutedEventArgs e)
    {
        if (_selected is not { } note) return;
        DeleteButton.IsEnabled = false;
        try
        {
            var result = await _account.DeleteNoteAsync(note.Id, CancellationToken.None);
            if (!result.Succeeded)
            {
                DetailStatus.Text = result.Error!.Display;
                return;
            }
            _selected = null;
            _items.Remove(note);
            _total = Math.Max(0, _total - 1);
            Render();
        }
        finally
        {
            DeleteButton.IsEnabled = true;
        }
    }

    /// <summary>Patches one row in place so a note edit does not reload the whole page.</summary>
    private void ReplaceRow(Note updated)
    {
        for (var i = 0; i < _items.Count; i++)
        {
            if (_items[i].Id != updated.Id) continue;
            _items[i] = updated;
            NotesList.SelectedItem = updated;
            return;
        }
    }
}
