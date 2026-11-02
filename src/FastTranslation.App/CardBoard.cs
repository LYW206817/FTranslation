using System;
using System.Windows;
using System.Windows.Controls;

namespace FastTranslation.App;

/// <summary>
/// 按卡片自己声明的列号排版：宽窗口时并成多列，窄到一列分不到
/// <see cref="MinColumnWidth"/> 时退化成单列。
/// </summary>
/// <remarks>
/// v88：这里原来是「逐张放进当前最矮的列」的自动分列，效果图里写死的左右归属被它打乱
/// （文档页的「失败」被塞到右列、设置页的「服务」被塞到左列），全屏下与设计稿逐张错位。
/// 现在卡片用 <see cref="ColumnProperty"/> 显式声明列号，顺序固定为列优先（先左列后右列），
/// 退化成单列时按同一顺序竖排，读起来仍与设计稿一致。
/// </remarks>
public sealed class CardBoard : Panel
{
    /// <summary>列与列之间的净间距(像素)。</summary>
    public const double Gap = 16;

    /// <summary>期望列数；实际列数还会被可用宽度压小。</summary>
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(CardBoard),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>分列之后每列至少要有这么宽，否则并回单列(像素)。</summary>
    /// <remarks>
    /// v88：默认值原来是 560，两列要 1136 的可用宽，而 2560×1600 屏最大化后内容区正好卡在
    /// 1128–1145 之间——差几十像素就掉回单列，用户看到的「全屏和设计稿完全不同」有一部分是这个。
    /// 500 让两列在 1016 就成立：全屏分两列各约 560，默认 1040 窗口(可用 820)仍是单列。
    /// </remarks>
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(CardBoard),
        new FrameworkPropertyMetadata(500.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>子元素所属的列号，从 0(左)开始；超出实际列数时并到最后一列。</summary>
    public static readonly DependencyProperty ColumnProperty = DependencyProperty.RegisterAttached(
        "Column", typeof(int), typeof(CardBoard),
        new FrameworkPropertyMetadata(
            0,
            FrameworkPropertyMetadataOptions.AffectsParentMeasure
            | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    /// <summary>想要的列数，默认 1。</summary>
    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>单列的最小宽度，默认 560；窄窗口靠它退回单列。</summary>
    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    /// <summary>读子元素声明的列号。</summary>
    public static int GetColumn(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (int)element.GetValue(ColumnProperty);
    }

    /// <summary>声明子元素落在哪一列；0 是最左那一列。</summary>
    public static void SetColumn(DependencyObject element, int value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ColumnProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = EffectiveColumns(availableSize.Width);
        var columnWidth = ColumnWidth(availableSize.Width, columns);
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility != Visibility.Visible) continue;

            // 高度给「无限」而不是列高：卡片高度必须由内容决定，给固定高度就会在
            // 短的卡片底部拉出一块空白（验收要的就是「高度 = 内容 + 内边距」）。
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
        }

        var columnOf = ResolveColumns(columns);
        var columnHeights = new double[columns];
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            if (columnOf[i] < 0) continue;

            columnHeights[columnOf[i]] += CardHeight(i) + StackedGap(columnOf, i);
        }

        var tallest = 0.0;
        foreach (var height in columnHeights)
        {
            tallest = Math.Max(tallest, height);
        }

        // 整体高度取最高那一列：Arrange 从每列列顶往下依次排，最长的那列决定要滚多远，
        // 短的那列尾部留白——若反过来把每张卡摊成平均高度，卡片就被拉长了。
        return new Size(columnWidth * columns + Gap * (columns - 1), tallest);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = EffectiveColumns(finalSize.Width);
        var columnWidth = ColumnWidth(finalSize.Width, columns);
        var columnOf = ResolveColumns(columns);
        var offsets = new double[columns];
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            var column = columnOf[i];
            if (column < 0)
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }

            if (offsets[column] > 0) offsets[column] += Gap;
            child.Arrange(new Rect(
                column * (columnWidth + Gap), offsets[column], columnWidth, child.DesiredSize.Height));
            offsets[column] += child.DesiredSize.Height;
        }

        return finalSize;
    }

    /// <summary>每张卡所在列：不可见的为 -1，超出实际列数的并到最后一列。</summary>
    private int[] ResolveColumns(int columns)
    {
        var columnOf = new int[InternalChildren.Count];
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            columnOf[i] = child.Visibility != Visibility.Visible
                ? -1
                : Math.Min(Math.Max(GetColumn(child), 0), columns - 1);
        }

        return columnOf;
    }

    /// <summary>这张卡与它上面那张之间的间距；列首的卡不计。</summary>
    private double StackedGap(int[] columnOf, int index)
    {
        for (var i = 0; i < index; i++)
        {
            if (columnOf[i] == columnOf[index]) return Gap;
        }

        return 0;
    }

    private double CardHeight(int index) => ((UIElement)InternalChildren[index]).DesiredSize.Height;

    /// <summary>按可用宽度算有效列数：塞不下 <see cref="MinColumnWidth"/> 就退回单列。</summary>
    private int EffectiveColumns(double availableWidth)
    {
        var wanted = Math.Max(1, Columns);
        if (double.IsInfinity(availableWidth) || double.IsNaN(availableWidth)) return wanted;

        var fits = (int)Math.Floor((availableWidth + Gap) / (Math.Max(MinColumnWidth, 1) + Gap));
        return Math.Max(1, Math.Min(wanted, fits));
    }

    /// <summary>按有效列数平分可用宽度；宽度无界时退回最小列宽（只用于量一遍）。</summary>
    private double ColumnWidth(double availableWidth, int columns)
    {
        var usable = availableWidth - Gap * (columns - 1);
        if (double.IsInfinity(usable) || double.IsNaN(usable) || usable <= 0) return Math.Max(MinColumnWidth, 1);

        return usable / columns;
    }
}
