using System.Text;
using FastTranslation.Core;
using FastTranslation.Core.Lexicon;
using Xunit;

namespace FastTranslation.Core.Tests;

public class WordShapeTests
{
    [Theory]
    [InlineData("bank", "bank")]
    [InlineData("  Bank  ", "Bank")]
    [InlineData("bank,", "bank")]
    [InlineData("hello.", "hello")]
    [InlineData("\"hello\"", "hello")]
    [InlineData("don't", "don't")]
    [InlineData("well-known", "well-known")]
    public void Single_english_words_are_accepted(string input, string expected) =>
        Assert.Equal(expected, WordShape.TryNormalize(input));

    [Theory]
    [InlineData("two words")]
    [InlineData("银行")]
    [InlineData("bank123")]
    [InlineData("")]
    [InlineData("   ")]
    public void Sentences_and_non_english_are_rejected(string input) =>
        Assert.Null(WordShape.TryNormalize(input));

    [Fact]
    public void Overlong_input_is_rejected() =>
        Assert.Null(WordShape.TryNormalize(new string('a', WordShape.MaxLength + 1)));
}

public class EcdictTextTests
{
    [Fact]
    public void Senses_are_grouped_by_part_of_speech()
    {
        var senses = EcdictText.ParseSenses("n. 银行, 堤, 岸\nvt. 存入银行, 靠岸\nvi. 倾斜");

        Assert.Equal(2, senses.Count);
        Assert.Equal("n.", senses[0].PartOfSpeech);
        Assert.Equal(new[] { "银行", "堤", "岸" }, senses[0].Definitions);
        // vt 与 vi 都归到动词组，界面不会出现两个 v.
        Assert.Equal("v.", senses[1].PartOfSpeech);
        Assert.Equal(new[] { "存入银行", "靠岸", "倾斜" }, senses[1].Definitions);
    }

    [Fact]
    public void Literal_backslash_n_from_the_csv_is_treated_as_a_line_break()
    {
        var senses = EcdictText.ParseSenses("n. 银行\\nvt. 存入银行");

        Assert.Equal(2, senses.Count);
        Assert.Equal("v.", senses[1].PartOfSpeech);
        Assert.Equal(new[] { "存入银行" }, senses[1].Definitions);
    }

    [Fact]
    public void Lines_without_part_of_speech_attach_to_the_previous_sense()
    {
        var senses = EcdictText.ParseSenses("n. 银行\n[计] 地址, 寻址");

        Assert.Single(senses);
        // [计] 这类领域标记保留在释义里：它是词条信息的一部分，抹掉反而看不出义项归属。
        Assert.Equal(new[] { "银行", "[计] 地址", "寻址" }, senses[0].Definitions);
    }

    [Fact]
    public void Forms_skip_lemma_markers_and_the_headword_itself()
    {
        var forms = EcdictText.ParseForms("bank", "s:banks/i:banking/d:banked/p:banked/0:bank/1:bank");

        Assert.Equal(["banks", "banking", "banked"], forms);
    }

    [Fact]
    public void Tags_are_localized_and_unknown_tags_are_kept()
    {
        var tags = EcdictText.ParseTags("cet4 cet6 ky custom");

        Assert.Equal(new[] { "四级", "六级", "考研", "custom" }, tags);
    }

    [Theory]
    [InlineData("bæŋk", "/bæŋk/")]
    [InlineData("/bæŋk/", "/bæŋk/")]
    [InlineData("ә'baut", "/ə'baut/")]
    public void Phonetic_gets_slashes_and_schwa_is_normalized(string input, string expected) =>
        Assert.Equal(expected, EcdictText.FormatPhonetic(input));
}

public class EcdictLexiconTests
{
    [Fact]
    public void Lookup_is_case_insensitive_and_parses_the_entry()
    {
        var path = WriteLexicon(
            ("bank", "bæŋk", "n. 银行, 堤\nvt. 存入银行", "s:banks", "cet4"));
        try
        {
            var lexicon = EcdictLexicon.TryLoad(path);

            Assert.NotNull(lexicon);
            Assert.True(lexicon!.TryLookup("BANK", out var entry));
            Assert.Equal("bank", entry.Headword);
            Assert.Equal("/bæŋk/", entry.Phonetic);
            Assert.Equal("银行", entry.Summary);
            Assert.Equal(new[] { "banks" }, entry.Forms);
            Assert.Equal(new[] { "四级" }, entry.Tags);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_word_returns_false()
    {
        var path = WriteLexicon(("bank", "bæŋk", "n. 银行", "", ""));
        try
        {
            var lexicon = EcdictLexicon.TryLoad(path);
            Assert.False(lexicon!.TryLookup("missing", out _));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Missing_file_loads_as_null_instead_of_throwing() =>
        Assert.Null(EcdictLexicon.TryLoad(Path.Combine(Path.GetTempPath(), "ft-no-such-" + Guid.NewGuid().ToString("N") + ".ftd")));

    /// <summary>按 build-ecdict.py 的 FTD1 布局写一个最小词库。</summary>
    private static string WriteLexicon(params (string Word, string Phonetic, string Translation, string Exchange, string Tag)[] entries)
    {
        var path = Path.Combine(Path.GetTempPath(), "ft-lexicon-" + Guid.NewGuid().ToString("N") + ".ftd");
        var words = entries.Select(e => Encoding.UTF8.GetBytes(e.Word)).ToArray();
        var payloads = entries
            .Select(e => Encoding.UTF8.GetBytes(string.Join('\t', e.Word, e.Phonetic, e.Translation, e.Exchange, e.Tag)))
            .ToArray();
        var indexSize = words.Sum(w => 2 + w.Length + 8);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("FTD1"u8);
        writer.Write(entries.Length);

        var offset = 4 + 4 + indexSize;
        for (var i = 0; i < entries.Length; i++)
        {
            writer.Write((short)words[i].Length);
            writer.Write(words[i]);
            writer.Write(offset);
            writer.Write(payloads[i].Length);
            offset += payloads[i].Length;
        }
        foreach (var payload in payloads) writer.Write(payload);
        return path;
    }
}

public class TranslationServiceDictionaryTests
{
    private sealed class FakeLexicon(DictionaryEntry? entry) : ILexicon
    {
        public bool TryLookup(string word, out DictionaryEntry found)
        {
            found = entry!;
            return entry is not null && string.Equals(entry.Headword, word, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static AppSettings Settings() => new() { Model = "m", TargetLanguage = "auto" };

    private static DictionaryEntry Bank() => new(
        "bank", "/bæŋk/",
        [new DictionarySense("n.", ["银行", "堤"])],
        ["banks"], ["四级"]);

    [Fact]
    public async Task Single_word_hits_the_lexicon_without_calling_the_provider()
    {
        var provider = new FakeProvider();
        var service = new TranslationService(provider, new FakeLexicon(Bank()));

        var result = await service.TranslateAsync("bank", Settings());

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Dictionary);
        Assert.Equal("银行", result.TranslatedText);
        Assert.Equal(DictionaryEntry.SourceName, result.Model);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Sentence_still_goes_to_the_provider()
    {
        var provider = new FakeProvider();
        var service = new TranslationService(provider, new FakeLexicon(Bank()));

        var result = await service.TranslateAsync("the bank is closed", Settings());

        Assert.Null(result.Dictionary);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Unknown_word_falls_back_to_the_provider()
    {
        var provider = new FakeProvider();
        var service = new TranslationService(provider, new FakeLexicon(null));

        var result = await service.TranslateAsync("bank", Settings());

        Assert.Null(result.Dictionary);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task Dictionary_hit_cancels_the_previous_in_flight_request()
    {
        var entered = new TaskCompletionSource();
        var gate = new TaskCompletionSource();
        var provider = new FakeProvider
        {
            Handler = r =>
            {
                entered.TrySetResult();
                gate.Task.Wait(5000);
                return new TranslationResult(r.Text, "x", r.SourceLanguage, r.TargetLanguage, r.Model, 1, false);
            },
        };
        var service = new TranslationService(provider, new FakeLexicon(Bank()));

        var slow = Task.Run(() => service.TranslateAsync("a slow sentence", Settings()));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var word = await service.TranslateAsync("bank", Settings());

        Assert.NotNull(word.Dictionary);
        gate.TrySetResult();
        var slowResult = await slow;
        Assert.Equal(TranslationErrorCode.Cancelled, slowResult.Error?.Code);
    }

    [Fact]
    public async Task Dictionary_result_is_cached()
    {
        var provider = new FakeProvider();
        var service = new TranslationService(provider, new FakeLexicon(Bank()));

        await service.TranslateAsync("bank", Settings());
        var second = await service.TranslateAsync("bank", Settings());

        Assert.True(second.FromCache);
        Assert.NotNull(second.Dictionary);
        Assert.Equal(0, provider.Calls);
    }
}
