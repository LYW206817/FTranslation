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

    // v63：中文没有空格，旧规则「不含空白且不超过 24 字符就是单词」把整句话都塞进单词本。
    [Theory]
    [InlineData("苹果", NoteKind.Word)]
    [InlineData("图书馆", NoteKind.Word)]
    [InlineData("画蛇添足", NoteKind.Phrase)]
    [InlineData("无风不起浪", NoteKind.Phrase)]
    [InlineData("她安静地离开了房间", NoteKind.Sentence)]
    [InlineData("我们今天下午去公园散步吧", NoteKind.Sentence)]
    [InlineData("他先走了，我留下来", NoteKind.Sentence)]
    [InlineData("我觉得这个方案可行，因为成本更低", NoteKind.Sentence)]
    public void Chinese_text_is_judged_by_character_count(string source, NoteKind expected) =>
        Assert.Equal(expected, NoteClassifier.Classify(source));

    [Fact]
    public void A_chinese_sentence_without_punctuation_is_not_a_word()
    {
        // 用户实际保存过的样本：13 个汉字、无标点、无空格
        Assert.Equal(NoteKind.Sentence, NoteClassifier.Classify("这句话我不太明白意思"));
    }

    [Fact]
    public void A_mixed_sentence_still_uses_the_chinese_buckets()
    {
        // 汉字 + 英文混排也走字数分档，不会因为“有空白”而误判成短语上限之外的句子
        Assert.Equal(NoteKind.Phrase, NoteClassifier.Classify("API 失败"));
    }

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
