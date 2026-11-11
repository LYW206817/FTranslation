namespace FastTranslation.App.Pages;

/// <summary>
/// 单窗口的十个页面（设计 D13）。侧边栏的渲染顺序由 MainWindow.xaml 的 SidebarItems 决定；
/// 在中间插入页面会连带移动它的按钮——这是有意为之。
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
