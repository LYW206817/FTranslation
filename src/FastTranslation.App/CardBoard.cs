using System;
using System.Windows;
using System.Windows.Controls;

namespace FastTranslation.App;

/// <summary>
/// 把卡片逐张放进「当前最矮的那一列」的流式面板：窗口宽时并成多列，窄到一列分不到
/// <see cref="MinColumnWidth"/> 时自动退化成单列。
/// </summary>
public sealed class CardBoard : Panel
{
    /// <summary>列与列之间的净间距(像素)。</summary>
    public const double Gap = 16;

    /// <summary>期望列数；实际列数还会被可用宽度压小。</summary>
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(CardBoard),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>分列之后每列至少要有这么宽，否则并回单列(像素)。</summary>
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(CardBoard),
        new FrameworkPropertyMetadata(560.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

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

        File(columns, out var columnHeights);
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
        var columnOf = File(columns, out _);
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

            child.Arrange(new Rect(
                column * (columnWidth + Gap), offsets[column], columnWidth, child.DesiredSize.Height));
            offsets[column] += child.DesiredSize.Height;
        }

        return finalSize;
    }

    /// <summary>
    /// 按声明顺序把可见子元素逐张放进当前最矮的一列，返回每张卡所在的列号（不可见的为 -1）。
    /// </summary>
    private int[] File(int columns, out double[] columnHeights)
    {
        var columnOf = new int[InternalChildren.Count];
        columnHeights = new double[columns];
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var child = InternalChildren[i];
            if (child.Visibility != Visibility.Visible)
            {
                columnOf[i] = -1;
                continue;
            }

            // 为什么按最短列放：卡片高度由内容决定、彼此相差几倍，按序号轮流放会出现
            // 一列很长一列很短；每次挑当前最矮的那一列，后面的卡片自动去补短的那列，
            // 两列高度差最小，滚到底时不会先滑过一大片空白。
            var target = 0;
            for (var c = 1; c < columns; c++)
            {
                if (columnHeights[c] < columnHeights[target]) target = c;
            }

            columnOf[i] = target;
            columnHeights[target] += child.DesiredSize.Height;
        }

        return columnOf;
    }

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
