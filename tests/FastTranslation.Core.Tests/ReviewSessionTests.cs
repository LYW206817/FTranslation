using FastTranslation.Core.Notes;
using Xunit;

namespace FastTranslation.Core.Tests;

/// <summary>
/// v52: the review page walks a session instead of a window walking its own list, so the walking
/// itself is now testable. These cases pin the edges that used to only be visible by clicking:
/// an empty queue must be finished before the first render, and Advance past the end must not
/// move the index off the array.
/// </summary>
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

        // the page renders 第 {Completed + 1} / {Total} 条 while a card is on screen
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
