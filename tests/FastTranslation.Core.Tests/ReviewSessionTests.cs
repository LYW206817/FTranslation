using FastTranslation.Core.Notes;
using Xunit;

namespace FastTranslation.Core.Tests;

/// <summary>v52:审核页改为遍历一个 session,而不是由窗口遍历自己的列表,所以遍历
/// 本身现在可测了。以下用例锁定过去只能靠点击才看得到的边界:
/// 空队列必须在第一次渲染前就标记完成,而 Advance 越过末尾时
/// 不能把索引移出数组。</summary>
public class ReviewSessionTests
{
    private static Note Card(string source) =>
        new(Guid.NewGuid(), NoteKind.Word, source, "译:" + source, "en", "zh", "", "Code.exe", "",
            1761700000, 1761700000);

    [Fact]
    public void EmptyQueue_IsFinishedImmediately()
    {
        var session = new ReviewSession([]);

        Assert.True(session.IsFinished);
        Assert.Null(session.Current);
        Assert.Equal(0, session.Total);
        Assert.Equal(0, session.Completed);
    }

    [Fact]
    public void SingleCard_StartsOnItAndFinishesAfterOneAdvance()
    {
        var session = new ReviewSession([Card("mitigate")]);

        Assert.False(session.IsFinished);
        Assert.Equal("mitigate", session.Current!.SourceText);

        Assert.False(session.Advance());
        Assert.True(session.IsFinished);
        Assert.Null(session.Current);
        Assert.Equal(1, session.Completed);
    }

    [Fact]
    public void Advance_WalksTheCardsInOrder()
    {
        var session = new ReviewSession([Card("a"), Card("b"), Card("c")]);
        var seen = new List<string>();

        while (session.Current is { } card)
        {
            seen.Add(card.SourceText);
            session.Advance();
        }

        Assert.Equal(["a", "b", "c"], seen);
        Assert.Equal(3, session.Completed);
        Assert.True(session.IsFinished);
    }

    [Fact]
    public void AdvancePastTheEnd_DoesNotOvershoot()
    {
        var session = new ReviewSession([Card("a")]);
        session.Advance();

        Assert.False(session.Advance());
        Assert.False(session.Advance());
        Assert.Equal(1, session.Completed);
    }

    [Fact]
    public void TotalAndCompleted_DriveTheProgressLabel()
    {
        var session = new ReviewSession([Card("a"), Card("b")]);
        session.Advance();

        // 卡片在屏幕上时,页面渲染的是 第 {Completed + 1} / {Total} 条
        Assert.Equal(1, session.Completed);
        Assert.Equal(2, session.Total);
        Assert.Equal("b", session.Current!.SourceText);
    }

    [Fact]
    public void Cards_AreASnapshotOfTheSourceQueue()
    {
        var queue = new List<Note> { Card("a") };
        var session = new ReviewSession(queue);

        queue.Add(Card("late"));

        Assert.Single(session.Cards);
        Assert.Equal(1, session.Total);
    }

    [Fact]
    public void NullCards_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ReviewSession(null!));
    }
}
