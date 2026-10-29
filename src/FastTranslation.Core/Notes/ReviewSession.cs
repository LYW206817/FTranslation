namespace FastTranslation.Core.Notes;

/// <summary>
/// The cards of one sitting plus how far into them you are (v52, design D16). Review used to live in
/// its own window, where the window's fields were the state; now it is a page, and the page needs
/// something that can be tested without a Dispatcher. Nothing here talks to the server: grading is
/// still the page's job, this only tracks position.
/// </summary>
public sealed class ReviewSession
{
    private readonly List<Note> _cards;

    /// <summary>Takes a snapshot: the caller may keep mutating its own queue afterwards.</summary>
    public ReviewSession(IEnumerable<Note> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        _cards = [.. cards];
    }

    public IReadOnlyList<Note> Cards => _cards;

    public int Total => _cards.Count;

    /// <summary>How many cards have been graded or skipped so far.</summary>
    public int Completed { get; private set; }

    /// <summary>The card on screen, or null once the sitting is over.</summary>
    public Note? Current => Completed < _cards.Count ? _cards[Completed] : null;

    public bool IsFinished => Completed >= _cards.Count;

    /// <summary>
    /// Moves past the current card. Returns whether another one is waiting, so the caller can
    /// render the done state in the same call instead of asking twice.
    /// </summary>
    public bool Advance()
    {
        if (IsFinished) return false;
        Completed++;
        return !IsFinished;
    }
}
