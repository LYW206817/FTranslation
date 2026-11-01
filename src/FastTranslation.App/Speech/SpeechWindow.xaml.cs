using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using FastTranslation.Core;
using FastTranslation.Core.Speech;

namespace FastTranslation.App.Speech;

/// <summary>
/// 语音字幕浮窗。视图层只做三件事：展示字幕行、展示会话状态、把「结束/清空」回传给 App。
/// 会话状态与采集全部在 App/SpeechTranslationCoordinator，窗口不持有会话。
/// </summary>
public sealed partial class SpeechWindow : Window
{
    private readonly ObservableCollection<SpeechLineRow> _lines = new();
    private readonly DispatcherTimer _timer;
    private DateTimeOffset _startedAt = DateTimeOffset.Now;
    private bool _running;

    /// <summary>用户点了「结束」；会话收尾在 App。</summary>
    public event Action? StopRequested;

    /// <summary>用户点了「清空」，App 只清显示，不动字幕服务的记账。</summary>
    public event Action? ClearRequested;

    public SpeechWindow()
    {
        InitializeComponent();
        LineList.ItemsSource = _lines;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => UpdateElapsed();
        // 浮窗默认贴屏幕底部居中：字幕是「看一眼」的界面，视线自然停在下方但不挡播放器进度条；
        // 必须等布局完成（ContentRendered）再定位，SourceInitialized 时 ActualWidth 还是 0
        ContentRendered += (_, _) => PlaceAtBottom();
    }

    /// <summary>开始一轮会话：清空字幕、起计时、亮状态点。</summary>
    public void BeginSession()
    {
        _lines.Clear();
        _startedAt = DateTimeOffset.Now;
        _running = true;
        LiveDot.Fill = (Brush)FindResource("BrandBrush");
        StateText.Text = "语音翻译 · 采集中";
        StopButton.Content = "结束";
        _timer.Start();
        UpdateElapsed();
        Show();
        Activate();
    }

    /// <summary>会话结束（正常或失败）：停计时、灰状态点、可再点一次结束关闭窗口。</summary>
    public void EndSession(string? error)
    {
        _running = false;
        _timer.Stop();
        LiveDot.Fill = (Brush)FindResource("TextMutedBrush");
        StateText.Text = string.IsNullOrEmpty(error) ? "已结束" : error;
        StopButton.Content = "关闭";
        UsageText.Text = "";
        UpdateElapsed();
    }

    /// <summary>字幕行写入（新增或按 SentenceId 原地刷新），并裁到 MaxSentences 行。</summary>
    public void ShowLine(SpeechTranscriptLine line, bool showSource, int maxSentences)
    {
        var index = FindLine(line.SentenceId);
        if (index >= 0)
        {
            _lines[index] = SpeechLineRow.From(line, showSource);
        }
        else
        {
            _lines.Add(SpeechLineRow.From(line, showSource));
            while (_lines.Count > maxSentences) _lines.RemoveAt(0);
        }
        // 半透明=仍会被改写的行，终稿恢复实色：视觉上区分「草稿」与「定稿」
        LineScroll.ScrollToEnd();
    }

    public void SetUsage(long chars) => UsageText.Text = chars > 0 ? $"已用 {chars} 字符" : "";

    private int FindLine(int sentenceId)
    {
        for (var i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].SentenceId == sentenceId) return i;
        }
        return -1;
    }

    private void UpdateElapsed()
    {
        var elapsed = DateTimeOffset.Now - _startedAt;
        ElapsedText.Text = _running ? elapsed.ToString(@"mm\:ss") : "";
    }

    private void PlaceAtBottom()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth > 0 ? (area.Width - ActualWidth) / 2 : 0);
        Top = area.Bottom - ActualHeight - 80;
    }

    // 头部拖动：无边框窗口的标题栏就是这一行
    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (_running) StopRequested?.Invoke();
        else Close();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _lines.Clear();
        ClearRequested?.Invoke();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>字幕行的展示形状：非终稿半透明，原文可选显示。</summary>
    public sealed record SpeechLineRow(int SentenceId, string Source, string Target, bool ShowSource, bool IsFinal)
    {
        public static SpeechLineRow From(SpeechTranscriptLine line, bool showSource) =>
            new(line.SentenceId, line.Source, line.Target, showSource, line.IsFinal);

        public Visibility SourceVisibility => ShowSource && Source.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        public double FinalOpacity => IsFinal ? 1.0 : 0.55;
    }
}
