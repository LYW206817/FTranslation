using FastTranslation.Core.Notes;
using Xunit;

namespace FastTranslation.Core.Tests;

public class NoteClassifierTests
{
    [Theory]
    [InlineData("hello", NoteKind.Word)]
    [InlineData("serendipity", NoteKind.Word)]
    [InlineData("  resilient  ", NoteKind.Word)]
    [InlineData("韧", NoteKind.Word)]
    [InlineData("give up", NoteKind.Phrase)]
    [InlineData("look forward to", NoteKind.Phrase)]
    [InlineData("in the long run", NoteKind.Phrase)]
    [InlineData("I saw this video and did not laugh.", NoteKind.Sentence)]
    [InlineData("Really?", NoteKind.Sentence)]
    [InlineData("this is a fairly long sentence without any punctuation at all", NoteKind.Sentence)]
    [InlineData("她安静地离开了房间。", NoteKind.Sentence)]
    public void Classify_picks_the_shelf(string source, NoteKind expected) =>
        Assert.Equal(expected, NoteClassifier.Classify(source));

    [Fact]
    public void Empty_text_is_a_sentence() => Assert.Equal(NoteKind.Sentence, NoteClassifier.Classify("   "));

    [Fact]
    public void A_long_unbroken_string_is_still_not_a_word() =>
        Assert.Equal(NoteKind.Word, NoteClassifier.Classify(new string('a', NoteClassifier.MaxWordLength)));

    [Fact]
    public void Beyond_max_word_length_without_spaces_falls_out_of_word()
    {
        var kind = NoteClassifier.Classify(new string('a', NoteClassifier.MaxWordLength + 1));
        Assert.NotEqual(NoteKind.Word, kind);
    }
}
