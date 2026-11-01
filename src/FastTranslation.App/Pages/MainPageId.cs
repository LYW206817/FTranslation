namespace FastTranslation.App.Pages;

/// <summary>
/// 单窗口的八个页面（设计 D13）。这里的顺序就是侧边栏的渲染顺序，所以在中间插入页面会
/// 连带移动它的按钮——这是有意为之。
/// </summary>
public enum MainPageId
{
    Home,
    Word,
    Phrase,
    Sentence,
    Review,
    Document,
    Speech,
    Account,
    Plan,
    Settings,
}
