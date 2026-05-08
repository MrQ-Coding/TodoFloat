using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace TodoFloat.Controls;

public sealed class OverflowChangedEventArgs : EventArgs
{
    public IReadOnlyList<object?> HiddenItems { get; }
    public OverflowChangedEventArgs(IReadOnlyList<object?> hidden) { HiddenItems = hidden; }
}

/// <summary>
/// 单行水平布局：从左往右排，放不下的子元素不渲染。
/// 当出现溢出时，在右端预留 <see cref="OverflowIndicatorWidth"/> px，
/// 由外部叠一个 "…" 按钮显示一个下拉列表展示剩余项。
/// 通过 <see cref="OverflowChanged"/> 事件把被隐藏项的 DataContext 抛给外部。
/// </summary>
public sealed class OverflowHidePanel : Panel
{
    public static readonly DependencyProperty OverflowIndicatorWidthProperty =
        DependencyProperty.Register(nameof(OverflowIndicatorWidth), typeof(double), typeof(OverflowHidePanel),
            new FrameworkPropertyMetadata(32.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double OverflowIndicatorWidth
    {
        get => (double)GetValue(OverflowIndicatorWidthProperty);
        set => SetValue(OverflowIndicatorWidthProperty, value);
    }

    public event EventHandler<OverflowChangedEventArgs>? OverflowChanged;

    private bool _lastHasOverflow;
    private int _lastHiddenCount = -1;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (InternalChildren.Count == 0) return new Size(0, 0);

        // Phase 1: measure every child unbounded so we know desired widths.
        double totalDesired = 0;
        double maxHeight = 0;
        var widths = new double[InternalChildren.Count];
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var ch = InternalChildren[i];
            if (ch == null) continue;
            ch.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            widths[i] = ch.DesiredSize.Width;
            totalDesired += widths[i];
            if (ch.DesiredSize.Height > maxHeight) maxHeight = ch.DesiredSize.Height;
        }

        double avail = double.IsInfinity(availableSize.Width) ? totalDesired : availableSize.Width;
        bool overflow = totalDesired > avail + 0.5;
        // 出现溢出时预留 indicator 宽度
        double budget = overflow ? Math.Max(0, avail - OverflowIndicatorWidth) : avail;

        // Phase 2: 按预算从左往右量第二遍：能放下就放，放不下就 0×0
        double used = 0;
        bool stop = false;
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var ch = InternalChildren[i];
            if (ch == null) continue;
            if (stop)
            {
                ch.Measure(new Size(0, 0));
                continue;
            }
            double w = widths[i];
            if (used + w <= budget + 0.5)
            {
                used += w;
            }
            else
            {
                stop = true;
                ch.Measure(new Size(0, 0));
            }
        }

        double finalWidth = double.IsInfinity(availableSize.Width) ? totalDesired : availableSize.Width;
        return new Size(finalWidth, maxHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        var hidden = new List<object?>();
        for (int i = 0; i < InternalChildren.Count; i++)
        {
            var ch = InternalChildren[i];
            if (ch == null) continue;
            var d = ch.DesiredSize;
            bool fits = d.Width > 0 && d.Height > 0 && (x + d.Width) <= finalSize.Width + 0.5;
            if (fits)
            {
                ch.Arrange(new Rect(x, 0, d.Width, finalSize.Height));
                x += d.Width;
            }
            else
            {
                ch.Arrange(new Rect(0, 0, 0, 0));
                if (ch is FrameworkElement fe) hidden.Add(fe.DataContext);
                else hidden.Add(null);
            }
        }

        // 异步抛事件，避免 arrange 内同步触发布局（WPF 会拒绝）
        bool newHasOverflow = hidden.Count > 0;
        if (newHasOverflow != _lastHasOverflow || hidden.Count != _lastHiddenCount)
        {
            _lastHasOverflow = newHasOverflow;
            _lastHiddenCount = hidden.Count;
            var snapshot = hidden;
            Dispatcher.BeginInvoke(new Action(() =>
                OverflowChanged?.Invoke(this, new OverflowChangedEventArgs(snapshot))),
                DispatcherPriority.Loaded);
        }

        return finalSize;
    }
}
