using System.Windows;

namespace FastTranslation.App;

/// <summary>
/// 侧栏项的「当前页」标记（v86）。
/// 以前同步高亮是给十个按钮换 Style：Style 一换，整个 ControlTemplate 连同里面的
/// Border/ContentPresenter 全部拆掉重建，十项每点一次就重画十次——点侧栏那一顿就是它。
/// 换成附加属性后模板原地不动，只有真正变化的那两项重新求值触发器。
/// </summary>
public static class SidebarState
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive",
        typeof(bool),
        typeof(SidebarState),
        new FrameworkPropertyMetadata(false));

    public static bool GetIsActive(DependencyObject element) => (bool)element.GetValue(IsActiveProperty);

    public static void SetIsActive(DependencyObject element, bool value) => element.SetValue(IsActiveProperty, value);
}
