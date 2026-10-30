using FastTranslation.Platform.Windows;
using Xunit;

namespace FastTranslation.Platform.Windows.Tests;

public class ImageNoteDraftTests
{
    private static readonly ImageNoteContext Sample = new("你好，世界", "English", "Chinese", "qwen-mt-flash");

    [Fact]
    public void Create_keeps_the_translation_when_the_text_was_not_edited()
    {
        var draft = ImageNoteDraft.Create("Hello world", "Hello world", Sample);

        Assert.Equal("Hello world", draft.SourceText);
        Assert.Equal("你好，世界", draft.TargetText);
        Assert.Equal("English", draft.SourceLang);
        Assert.Equal("Chinese", draft.TargetLang);
        Assert.Equal("qwen-mt-flash", draft.Model);
    }

    [Fact]
    public void Create_ignores_surrounding_whitespace_when_comparing_the_text()
    {
        var draft = ImageNoteDraft.Create("  Hello world  ", "Hello world\n", Sample);

        Assert.Equal("Hello world", draft.SourceText);
        Assert.Equal("你好，世界", draft.TargetText);
    }

    [Fact]
    public void Create_drops_the_translation_once_the_user_retranslates_the_ocr_text()
    {
        // OCR 把 l 认成 1 之类的手改，译文就和原文对不上了，存下去只会误导复习
        var draft = ImageNoteDraft.Create("He1lo world", "Hello world", Sample);

        Assert.Equal("Hello world", draft.SourceText);
        Assert.Equal(string.Empty, draft.TargetText);
        Assert.Equal(string.Empty, draft.Model);
        Assert.Equal("English", draft.SourceLang);
        Assert.Equal("Chinese", draft.TargetLang);
    }

    [Fact]
    public void Create_without_a_context_saves_the_text_only()
    {
        var draft = ImageNoteDraft.Create("Hello world", "Hello world", null);

        Assert.Equal("Hello world", draft.SourceText);
        Assert.Equal(string.Empty, draft.TargetText);
        Assert.Equal(string.Empty, draft.SourceLang);
        Assert.Equal(string.Empty, draft.TargetLang);
        Assert.Equal(string.Empty, draft.Model);
    }

    [Fact]
    public void Create_with_an_empty_translation_does_not_invent_a_model()
    {
        var draft = ImageNoteDraft.Create("Hello world", "Hello world", new ImageNoteContext(string.Empty, "English", "Chinese", "qwen-mt-flash"));

        Assert.Equal(string.Empty, draft.TargetText);
        Assert.Equal(string.Empty, draft.Model);
    }
}
