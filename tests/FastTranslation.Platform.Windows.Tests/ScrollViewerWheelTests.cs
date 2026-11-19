using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace FastTranslation.Platform.Windows.Tests;

public class ScrollViewerWheelTests
{
    [Theory]
    [InlineData(3, 600, 48.0)]
    [InlineData(5, 600, 80.0)]
    [InlineData(-1, 600, 540.0)]
    [InlineData(0, 600, 0.0)]
    public void ComputeWheelStep_FollowsSystemLinesOrPageMode(int lines, double viewportHeight, double expected)
    {
        Assert.Equal(expected, ScrollViewerWheel.ComputeWheelStep(lines, viewportHeight), precision: 6);
    }

    [Theory]
    [InlineData(100, 500, -120, 48, 148)]
    [InlineData(100, 500, 120, 48, 52)]
    [InlineData(100, 500, -15, 48, 106)]
    [InlineData(100, 500, 30, 48, 88)]
    [InlineData(10, 500, 120, 48, 0)]
    [InlineData(480, 500, -120, 48, 500)]
    public void ComputeTargetOffset_ScalesProportionalDeltaAndClamps(
        double current, double scrollableHeight, int delta, double step, double expected)
    {
        Assert.Equal(expected, ScrollViewerWheel.ComputeTargetOffset(current, scrollableHeight, delta, step), precision: 6);
    }

    [Theory]
    [InlineData(0, 200, 120, false)]
    [InlineData(10, 200, 120, true)]
    [InlineData(200, 200, -120, false)]
    [InlineData(190, 200, -120, true)]
    [InlineData(0, 0, -120, false)]
    [InlineData(0, 0.4, -120, false)]
    [InlineData(50, 200, 0, false)]
    public void CanScroll_RespectsDirectionAndBoundary(
        double offset, double scrollableHeight, int delta, bool expected)
    {
        Assert.Equal(expected, ScrollViewerWheel.CanScroll(offset, scrollableHeight, delta));
    }

    [Fact]
    public void ForwardEnabled_ForwardsWheelFromPasswordBoxContentHostToOuterScrollViewer()
    {
        RunInSta(() =>
        {
            var passwordBox = new PasswordBox();
            var content = new StackPanel
            {
                Children =
                {
                    new Border { Height = 120 },
                    passwordBox,
                    new Border { Height = 400 },
                },
            };
            var outer = new ScrollViewer
            {
                Width = 300,
                Height = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = content,
            };

            ScrollViewerWheel.SetForward(outer, true);
            outer.Measure(new Size(300, 200));
            outer.Arrange(new Rect(0, 0, 300, 200));
            outer.UpdateLayout();

            var innerHost = FindVisualChild<ScrollViewer>(passwordBox);
            Assert.NotNull(innerHost);

            var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent,
                Source = innerHost,
            };
            outer.RaiseEvent(preview);
            outer.UpdateLayout();

            Assert.True(preview.Handled);
            Assert.True(outer.VerticalOffset > 0);
        });
    }

    [Fact]
    public void ForwardEnabled_LetsScrollableInnerViewerScrollUntilBoundaryThenForwards()
    {
        RunInSta(() =>
        {
            var inner = new ScrollViewer
            {
                Height = 100,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new Border { Height = 200 },
            };
            var outer = new ScrollViewer
            {
                Width = 300,
                Height = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel
                {
                    Children =
                    {
                        inner,
                        new Border { Height = 400 },
                    },
                },
            };

            ScrollViewerWheel.SetForward(outer, true);
            outer.Measure(new Size(300, 200));
            outer.Arrange(new Rect(0, 0, 300, 200));
            outer.UpdateLayout();

            // 内层仍有滚动空间时，外层不拦截
            var midPreview = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent,
                Source = inner,
            };
            outer.RaiseEvent(midPreview);
            Assert.False(midPreview.Handled);
            Assert.Equal(0, outer.VerticalOffset);

            // 内层滚到底部后，继续向下滚轮由外层接管
            inner.ScrollToEnd();
            inner.UpdateLayout();

            var bottomPreview = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent,
                Source = inner,
            };
            outer.RaiseEvent(bottomPreview);
            outer.UpdateLayout();
            Assert.True(bottomPreview.Handled);
            Assert.True(outer.VerticalOffset > 0);
        });
    }

    [Fact]
    public void ForwardEnabled_HandlesRunContentElementSourceWithoutThrowing()
    {
        RunInSta(() =>
        {
            var run = new Run("hello");
            var textBlock = new TextBlock();
            textBlock.Inlines.Add(run);
            var outer = new ScrollViewer
            {
                Width = 300,
                Height = 200,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = new StackPanel
                {
                    Children =
                    {
                        textBlock,
                        new Border { Height = 400 },
                    },
                },
            };

            ScrollViewerWheel.SetForward(outer, true);
            outer.Measure(new Size(300, 200));
            outer.Arrange(new Rect(0, 0, 300, 200));
            outer.UpdateLayout();

            var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent,
                Source = run,
            };
            outer.RaiseEvent(preview);
            outer.UpdateLayout();

            Assert.True(preview.Handled);
            Assert.True(outer.VerticalOffset > 0);
        });
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
