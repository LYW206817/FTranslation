using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace FastTranslation.App;

/// <summary>
/// 由模板驱动弹出层的 ComboBox：模板里完全不用 Binding(InvariantGlobalization 会让 WPF
/// 根因）。PART_Toggle 的点击在代码里翻转 IsDropDownOpen；ControlTemplate 则通过
/// IsDropDownOpen 触发器打开 PART_Popup。
/// </summary>
public class FtComboBox : ComboBox
{
    private ToggleButton? _toggle;

    public FtComboBox()
    {
        // v111：点击下拉框时焦点必须立刻落回 ComboBox 自己。模板里的 PART_Toggle 是
        // Focusable=False，点击不会给 ComboBox 键盘焦点——于是从点开到选中之间品牌色
        // 焦点环都不出现，用户反馈「点了没反应、要等选中后才亮」。
        // Focus() 幂等，已经聚焦时再点不会丢焦点，也不会影响 PART_Toggle 的点击。
        PreviewMouseLeftButtonDown += (_, _) => Focus();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_toggle is not null) _toggle.Click -= OnToggleClick;
        _toggle = GetTemplateChild("PART_Toggle") as ToggleButton;
        if (_toggle is not null) _toggle.Click += OnToggleClick;
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
        => IsDropDownOpen = !IsDropDownOpen;
}
