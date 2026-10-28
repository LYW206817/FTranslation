using System.Collections.ObjectModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FastTranslation.Core.Account;
using FastTranslation.Core.Notes;
using FastTranslation.Platform.Windows;

namespace FastTranslation.App;

/// <summary>
/// The notebook: three shelves (单词 / 短语 / 语句) over one table, a list, and a detail pane that
/// can re-file an entry or annotate it. Every mutation goes to the server — there is no local copy,
/// so the three views are the same rows filtered by <see cref="NoteKind"/>.
/// </summary>
public partial class NotesWindow : Window
{
    private readonly AccountCoordinator _account;
    private readonly ObservableCollection<Note> _items = new();
    private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(320) };

    private NoteKind _shelf = NoteKind.Word;
    private Note? _selected;
    private bool _loading;
    private bool _suppressKindChange;
    private CancellationTokenSource? _loadCts;
    private string _search = string.Empty;

    public NotesWindow(AccountCoordinator account)
    {
        InitializeComponent();
        WindowChrome.Apply(this);
        _account = account;
        NotesList.ItemsSource = _items;
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            _search = SearchBox.Text.Trim();
            _ = LoadAsync();
        };
        _account.Changed += OnAccountChanged;
        Closed += (_, _) =>
        {
            _account.Changed -= OnAccountChanged;
            _loadCts?.Cancel();
            _loadCts?.Dispose();
        };
        Loaded += (_, _) => _ = LoadAsync();
    }

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
                EmptyText.Text = string.IsNullOrEmpty(_search)
                    ? "这一栏还是空的。翻译时点结果卡上的「保存」就会出现在这里。"
                    : "没有匹配「" + _search + "」的条目。";
            }
            else
            {
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
        Headline.Text = _account.IsSignedIn ? "单词本" : "单词本(未登录)";
        EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var active = _shelf switch
        {
            NoteKind.Word => WordButton,
            NoteKind.Phrase => PhraseButton,
            _ => SentenceButton,
        };
        foreach (var button in new[] { WordButton, PhraseButton, SentenceButton })
        {
            button.Style = ReferenceEquals(button, active)
                ? (Style)FindResource("ShelfButtonActive")
                : (Style)FindResource("ShelfButton");
        }

        if (_loading && _items.Count == 0) EmptyText.Text = "正在加载…";
        if (!_account.IsSignedIn)
        {
            EmptyText.Text = "先登录账号,保存的单词就能跨设备同步。";
            EmptyText.Visibility = Visibility.Visible;
        }

        DetailEmpty.Visibility = _selected is null ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel.Visibility = _selected is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowDetail(Note note)
    {
        DetailSource.Text = note.SourceText;
        DetailTarget.Text = note.TargetText;
        DetailOrigin.Text = note.OriginLine + (string.IsNullOrWhiteSpace(note.Model) ? string.Empty : " · " + note.Model);
        NoteBox.Text = note.Annotation;
        DetailStatus.Text = string.Empty;

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

    private void Shelf_Click(object sender, RoutedEventArgs e)
    {
        var wire = (sender as Button)?.Tag as string;
        if (!NoteKinds.TryParse(wire, out var kind)) return;
        _shelf = kind;
        _selected = null;
        _items.Clear();
        Render();
        _ = LoadAsync();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        // typing re-issues the query; the timer keeps one keystroke per 320 ms instead of one per key
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void Refresh_Click(object _, RoutedEventArgs e) => _ = LoadAsync();

    private void Notes_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = NotesList.SelectedItem as Note;
        if (_selected is not null) ShowDetail(_selected);
        Render();
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
            DetailStatus.Text = result.Succeeded
                ? "备注已保存。"
                : result.Error!.Display;
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
