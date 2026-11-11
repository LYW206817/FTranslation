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
    private TimeSpan _countedElapsed;
    private long _countedUsageChars;
    private long _sessionUsageChars;
    private bool _running;
    private bool _paused;

    /// <summary>用户点了「结束」；会话收尾在 App。</summary>
    public event Action? StopRequested;

    /// <summary>用户点了「暂停」。暂停是真的结束这条 WS 会话（口径见 App.OnSpeechPauseRequested），
    /// 本子窗只留字幕与累计值。</summary>
    public event Action? PauseRequested;

    /// <summary>用户点了「继续」；App 重新预检额度并开一条会话，成功后回来调 <see cref="ResumeSession"/>。</summary>
    public event Action? ResumeRequested;

    /// <summary>是否停在「已暂停」态：热键/托盘在这期间按「语音翻译」要接上继续，而不是当全新会话。</summary>
    public bool IsPaused => _paused;

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

    /// <summary>开始全新的一轮会话：字幕、计时、用量全部归零，亮状态点。</summary>
    public void BeginSession()
    {
        _lines.Clear();
        _countedElapsed = TimeSpan.Zero;
        _countedUsageChars = 0;
        UsageText.Text = "";
        StartCounting();
    }

    /// <summary>从「已暂停」接回采集：字幕与累计的时长/用量都保留，只有本轮会话从零开始计。</summary>
    public void ResumeSession() => StartCounting();

    private void StartCounting()
    {
        _running = true;
        _paused = false;
        _sessionUsageChars = 0;
        _startedAt = DateTimeOffset.Now;
        LiveDot.Fill = (Brush)FindResource("BrandBrush");
        StateText.Text = "语音翻译 · 采集中";
        StopButton.Content = "结束";
        PauseButton.Content = "暂停";
        PauseButton.IsEnabled = true;
        _timer.Start();
        UpdateElapsed();
        Show();
        Activate();
    }

    /// <summary>暂停：停表、灰点、本轮时长与用量并进累计值，按钮换成「继续」。</summary>
    public void PauseSession()
    {
        CommitCounters();
        _running = false;
        _paused = true;
        _timer.Stop();
        LiveDot.Fill = (Brush)FindResource("TextMutedBrush");
        StateText.Text = $"已暂停,字幕保留;点「继续」重开一轮采集(每轮最少按 {SpeechQuotaGate.MinBilledSeconds} 秒计费)";
        PauseButton.Content = "继续";
        // 暂停态没有会话可「结束」，那颗按钮回到关闭的语义；要接着讲是点「继续」
        StopButton.Content = "关闭";
        UpdateElapsed();
    }

    /// <summary>
    /// v110: 截图结束后把临时收起的浮窗放回来，但不抢焦点。Show() 默认会激活窗口，
    /// 用户刚被截图遮罩夺走焦点，再被浮窗抢一次会打断他回到原应用的操作。
    /// </summary>
    public void ShowWithoutActivation()
    {
        var previous = ShowActivated;
        ShowActivated = false;
        Show();
        ShowActivated = previous;
    }

    /// <summary>会话真的结束（正常或失败）：停计时、灰状态点、可再点一次关闭窗口。暂停不走这里。</summary>
    public void EndSession(string? error, int dropped = 0)
    {
        CommitCounters();
        _running = false;
        _paused = false;
        _timer.Stop();
        LiveDot.Fill = (Brush)FindResource("TextMutedBrush");
        // 被丢弃的句子必须说出来：否则用户只看到字幕少了几个词，以为模型漏译
        StateText.Text = !string.IsNullOrEmpty(error)
            ? error
            : dropped > 0
                ? $"已结束({dropped} 句因处理不过来被丢弃)"
                : "已结束";
        StopButton.Content = "关闭";
        // 会话已释放，没有「继续」可点——重开得走「开始语音翻译」，那会重新预检额度
        PauseButton.IsEnabled = false;
        PauseButton.Content = "暂停";
        UsageText.Text = "";
        UpdateElapsed();
    }

    // 每暂停/结束一次就是一条新的 WS 会话，服务端 usage 从 0 重计：累计值必须由窗口自己保住，
    // 否则点「继续」后「已用 N 字符」和计时会掉回小数字
    private void CommitCounters()
    {
        if (_running) _countedElapsed += DateTimeOffset.Now - _startedAt;
        _countedUsageChars += _sessionUsageChars;
        _sessionUsageChars = 0;
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

    /// <summary>本轮会话的累计用量（服务端按会话累计上报），显示的是窗口生命周期内的总用量。</summary>
    public void SetUsage(long chars)
    {
        _sessionUsageChars = chars;
        var total = _countedUsageChars + _sessionUsageChars;
        UsageText.Text = total > 0 ? $"已用 {total} 字符" : "";
    }

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
        // 暂停态要显示冻住的累计时长（用户才看得出这段还没讲完的部分仍在计数），真结束才清空
        var shown = _running ? _countedElapsed + (DateTimeOffset.Now - _startedAt) : _countedElapsed;
        ElapsedText.Text = _running || _paused ? shown.ToString(@"mm\:ss") : "";
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

    // 状态翻转不在这里做：暂停/继续都会真的结束或重开 WS 会话，只有 App 那边成功了才改显示，
    // 免得重开被额度拒绝时窗口已经写成「采集中」却在骗人
    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_paused) ResumeRequested?.Invoke();
        else if (_running) PauseRequested?.Invoke();
    }

    // 只清显示，不动字幕服务的记账；无需绕回 App，事件本来就是空订阅
    private void Clear_Click(object sender, RoutedEventArgs e) => _lines.Clear();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>字幕行的展示形状：非终稿半透明，原文可选显示，终稿无译文时标「未翻译」。</summary>
    public sealed record SpeechLineRow(
        int SentenceId, string Source, string Target, bool ShowSource, bool IsFinal, bool IsUntranslated)
    {
        public static SpeechLineRow From(SpeechTranscriptLine line, bool showSource) =>
            new(line.SentenceId, line.Source, line.Target, showSource, line.IsFinal, line.IsUntranslated);

        public Visibility SourceVisibility => ShowSource && Source.Length > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        public Visibility TargetVisibility => string.IsNullOrWhiteSpace(Target)
            ? Visibility.Collapsed
            : Visibility.Visible;

        public Visibility UntranslatedVisibility => IsUntranslated ? Visibility.Visible : Visibility.Collapsed;

        public double FinalOpacity => IsFinal ? 1.0 : 0.55;
    }
}
