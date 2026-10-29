namespace FastTranslation.Core.Notes;

/// <summary>
/// 一次复习的卡片集合以及当前进度（v52，设计 D16）。复习过去独占一个窗口，窗口字段就是状态；
/// 现在它变成一个页面，页面需要一份不依赖 Dispatcher 也能测的状态。这里不碰服务端：
/// 打分仍是页面的职责，本类只记位置。
/// </summary>
public sealed class ReviewSession
{
    private readonly List<Note> _cards;

    /// <summary>取快照：之后调用方可以继续改自己的队列。</summary>
    public ReviewSession(IEnumerable<Note> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        _cards = [.. cards];
    }

    public IReadOnlyList<Note> Cards => _cards;

    public int Total => _cards.Count;

    /// <summary>到目前为止已评分或跳过多少张卡。</summary>
    public int Completed { get; private set; }

    /// <summary>当前屏幕上的卡片；一轮复习结束后为 null。</summary>
    public Note? Current => Completed < _cards.Count ? _cards[Completed] : null;

    public bool IsFinished => Completed >= _cards.Count;

    /// <summary>
    /// 前进到下一张卡。返回是否还有卡片等待，这样调用方可以在同一次调用里渲染完成态，
    /// 而不必再问一次。
    /// </summary>
    public bool Advance()
    {
        if (IsFinished) return false;
        Completed++;
        return !IsFinished;
    }
}
