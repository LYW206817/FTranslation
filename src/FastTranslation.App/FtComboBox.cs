using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace FastTranslation.App;

/// <summary>
/// 由模板驱动弹出层的 ComboBox：模板里完全不用 Binding(InvariantGlobalization 会让 WPF
/// 根因）。PART_Toggle 的点击在代码里翻转 IsDropDownOpen；ControlTemplate 则通过
/// IsDropDownOpen 触发器打开 PART_Popup。
/// </summary>
public sealed class FtComboBox : ComboBox
{
    /// <summary>药丸样式左侧可选圆点画刷；为 null 时模板隐藏圆点。</summary>
    public static readonly DependencyProperty DotBrushProperty = DependencyProperty.Register(
        nameof(DotBrush), typeof(Brush), typeof(FtComboBox));

    /// <summary>药丸样式左侧可选前缀文案（如「翻译成」）；为空时模板隐藏前缀。</summary>
    public static readonly DependencyProperty PrefixTextProperty = DependencyProperty.Register(
        nameof(PrefixText), typeof(string), typeof(FtComboBox));

    private ToggleButton? _toggle;

    // v147：药丸形语言选择器以前把 FtComboBox 套进外层 Border(TopPill)，导致焦点/展开环
    // 画在白药丸内部、把圆点或前缀隔在外面。把圆点与前缀放进 FtComboBox 属性后，整枚药丸
    // 就是一个单一控件，模板用 TemplateBinding + Trigger 驱动且保持零 Binding。

    /// <summary>获取或设置药丸左侧圆点的填充画刷；为 <see langword="null"/> 时隐藏。</summary>
    public Brush? DotBrush
    {
        get => (Brush?)GetValue(DotBrushProperty);
        set => SetValue(DotBrushProperty, value);
    }

    /// <summary>获取或设置药丸左侧前缀文案；为空时隐藏。</summary>
    public string? PrefixText
    {
        get => (string?)GetValue(PrefixTextProperty);
        set => SetValue(PrefixTextProperty, value);
    }

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
