namespace FastTranslation.App.Pages;

/// <summary>
/// The seven pages of the single window (design D13). The order here is the order the sidebar
/// renders, so inserting a page in the middle moves its button too — that is deliberate.
/// </summary>
public enum MainPageId
{
    Home,
    Word,
    Phrase,
    Sentence,
    Review,
    Account,
    Plan,
    Settings,
}
