using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TodoFloat.Controls;

/// <summary>
/// Animates the visible height of its content without scaling the content itself.
/// This keeps expanded task details crisp and avoids the heavy LayoutTransform path.
/// </summary>
public sealed class AnimatedExpander : Decorator
{
    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register(
            nameof(IsExpanded),
            typeof(bool),
            typeof(AnimatedExpander),
            new FrameworkPropertyMetadata(false, OnIsExpandedChanged));

    public static readonly DependencyProperty ExpandDurationProperty =
        DependencyProperty.Register(
            nameof(ExpandDuration),
            typeof(Duration),
            typeof(AnimatedExpander),
            new PropertyMetadata(new Duration(TimeSpan.FromMilliseconds(340))));

    public static readonly DependencyProperty CollapseDurationProperty =
        DependencyProperty.Register(
            nameof(CollapseDuration),
            typeof(Duration),
            typeof(AnimatedExpander),
            new PropertyMetadata(new Duration(TimeSpan.FromMilliseconds(170))));

    public static readonly DependencyProperty AnimatedHeightProperty =
        DependencyProperty.Register(
            nameof(AnimatedHeight),
            typeof(double),
            typeof(AnimatedExpander),
            new FrameworkPropertyMetadata(
                0.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure |
                FrameworkPropertyMetadataOptions.AffectsArrange));

    public static readonly DependencyProperty IsAnimatingProperty =
        DependencyProperty.Register(
            nameof(IsAnimating),
            typeof(bool),
            typeof(AnimatedExpander),
            new PropertyMetadata(false));

    private double _naturalHeight;
    private double _naturalWidth;
    private double _lastMeasureWidth = double.NaN;
    private double _lastArrangeWidth = double.NaN;
    private double _lastArrangeHeight = double.NaN;
    private bool _isAnimatingHeight;
    private bool _isLoaded;
    private bool _isQueued;
    private int _animationVersion;

    public AnimatedExpander()
    {
        ClipToBounds = true;
        Loaded += (_, _) =>
        {
            _isLoaded = true;
            SyncHeightWithoutAnimation();
        };
        Unloaded += (_, _) =>
        {
            BeginAnimation(AnimatedHeightProperty, null);
            _animationVersion++;
            _isLoaded = false;
            _isAnimatingHeight = false;
            _isQueued = false;
            _lastArrangeWidth = double.NaN;
            _lastArrangeHeight = double.NaN;
            SetCurrentValue(IsAnimatingProperty, false);
        };
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public Duration ExpandDuration
    {
        get => (Duration)GetValue(ExpandDurationProperty);
        set => SetValue(ExpandDurationProperty, value);
    }

    public Duration CollapseDuration
    {
        get => (Duration)GetValue(CollapseDurationProperty);
        set => SetValue(CollapseDurationProperty, value);
    }

    public double AnimatedHeight
    {
        get => (double)GetValue(AnimatedHeightProperty);
        set => SetValue(AnimatedHeightProperty, value);
    }

    public bool IsAnimating
    {
        get => (bool)GetValue(IsAnimatingProperty);
        private set => SetValue(IsAnimatingProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is not { } child)
        {
            _naturalHeight = 0;
            _naturalWidth = 0;
            _lastMeasureWidth = double.NaN;
            _lastArrangeWidth = double.NaN;
            _lastArrangeHeight = double.NaN;
            return new Size(0, 0);
        }

        var measureWidth = double.IsInfinity(constraint.Width)
            ? Math.Max(0, ActualWidth)
            : constraint.Width;

        if (!IsExpanded && !_isAnimatingHeight)
        {
            return new Size(0, 0);
        }

        if (ShouldMeasureChild(measureWidth))
        {
            MeasureChild(child, measureWidth);
        }

        if (!_isLoaded)
        {
            SetCurrentValue(AnimatedHeightProperty, IsExpanded ? _naturalHeight : 0.0);
        }

        return new Size(_naturalWidth, GetVisibleHeight());
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        if (Child is not { } child)
        {
            Clip = null;
            _lastArrangeWidth = double.NaN;
            _lastArrangeHeight = double.NaN;
            return arrangeBounds;
        }

        if (!IsExpanded && !_isAnimatingHeight)
        {
            Clip = new RectangleGeometry(new Rect(0, 0, arrangeBounds.Width, 0));
            return new Size(arrangeBounds.Width, 0);
        }

        var visibleHeight = GetVisibleHeight();
        var contentHeight = Math.Max(_naturalHeight, child.DesiredSize.Height);
        if (ShouldArrangeChild(arrangeBounds.Width, contentHeight))
        {
            child.Arrange(new Rect(0, 0, arrangeBounds.Width, contentHeight));
            _lastArrangeWidth = arrangeBounds.Width;
            _lastArrangeHeight = contentHeight;
        }

        Clip = new RectangleGeometry(new Rect(0, 0, arrangeBounds.Width, visibleHeight));
        return new Size(arrangeBounds.Width, visibleHeight);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (sizeInfo.WidthChanged && IsExpanded && !_isAnimatingHeight)
        {
            QueueHeightSync();
        }
    }

    private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((AnimatedExpander)d).QueueStateAnimation();
    }

    private void QueueStateAnimation()
    {
        if (!_isLoaded)
        {
            SyncHeightWithoutAnimation();
            return;
        }

        SetCurrentValue(IsAnimatingProperty, true);
        if (_isQueued) return;
        _isQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _isQueued = false;
            AnimateToState();
        }), DispatcherPriority.Loaded);
    }

    private void QueueHeightSync()
    {
        if (!_isLoaded || _isQueued) return;
        _isQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _isQueued = false;
            SyncHeightWithoutAnimation();
        }), DispatcherPriority.Loaded);
    }

    private void AnimateToState()
    {
        if (IsExpanded)
        {
            MeasureNaturalHeight();
        }
        else if (_naturalHeight <= 0)
        {
            _naturalHeight = Math.Ceiling(Math.Max(ActualHeight, RenderSize.Height));
        }

        var target = IsExpanded ? _naturalHeight : 0.0;
        var from = Math.Max(0, ActualHeight);

        BeginAnimation(AnimatedHeightProperty, null);
        SetCurrentValue(AnimatedHeightProperty, from);
        _isAnimatingHeight = true;
        SetCurrentValue(IsAnimatingProperty, true);
        var version = ++_animationVersion;

        var animation = new DoubleAnimation
        {
            From = from,
            To = target,
            Duration = IsExpanded ? ExpandDuration : CollapseDuration,
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new CubicEase
            {
                EasingMode = IsExpanded ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };

        animation.Completed += (_, _) =>
        {
            if (version != _animationVersion) return;

            _isAnimatingHeight = false;
            BeginAnimation(AnimatedHeightProperty, null);
            SetCurrentValue(AnimatedHeightProperty, target);
            SetCurrentValue(IsAnimatingProperty, false);
            InvalidateMeasure();
        };

        BeginAnimation(AnimatedHeightProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void SyncHeightWithoutAnimation()
    {
        BeginAnimation(AnimatedHeightProperty, null);
        _animationVersion++;
        _isAnimatingHeight = false;
        SetCurrentValue(IsAnimatingProperty, false);
        if (IsExpanded)
        {
            MeasureNaturalHeight();
            SetCurrentValue(AnimatedHeightProperty, _naturalHeight);
        }
        else
        {
            SetCurrentValue(AnimatedHeightProperty, 0.0);
        }
        InvalidateMeasure();
    }

    private void MeasureNaturalHeight()
    {
        if (Child is not { } child)
        {
            _naturalHeight = 0;
            _naturalWidth = 0;
            _lastMeasureWidth = double.NaN;
            _lastArrangeWidth = double.NaN;
            _lastArrangeHeight = double.NaN;
            return;
        }

        var width = ActualWidth;
        if (double.IsNaN(width) || width <= 0)
        {
            width = RenderSize.Width;
        }

        MeasureChild(child, Math.Max(0, width));
    }

    private bool ShouldMeasureChild(double measureWidth)
    {
        if (!_isAnimatingHeight) return true;
        if (double.IsNaN(_lastMeasureWidth)) return true;
        if (_naturalHeight <= 0 && IsExpanded) return true;
        return Math.Abs(_lastMeasureWidth - measureWidth) > 0.5;
    }

    private bool ShouldArrangeChild(double arrangeWidth, double arrangeHeight)
    {
        if (!_isAnimatingHeight) return true;
        if (double.IsNaN(_lastArrangeWidth) || double.IsNaN(_lastArrangeHeight)) return true;
        return Math.Abs(_lastArrangeWidth - arrangeWidth) > 0.5 ||
               Math.Abs(_lastArrangeHeight - arrangeHeight) > 0.5;
    }

    private void MeasureChild(UIElement child, double measureWidth)
    {
        child.Measure(new Size(Math.Max(0, measureWidth), double.PositiveInfinity));
        _naturalWidth = Math.Ceiling(child.DesiredSize.Width);
        _naturalHeight = Math.Ceiling(child.DesiredSize.Height);
        _lastMeasureWidth = measureWidth;
        _lastArrangeWidth = double.NaN;
        _lastArrangeHeight = double.NaN;
    }

    private double GetVisibleHeight()
    {
        if (IsExpanded && !_isAnimatingHeight)
        {
            return _naturalHeight;
        }

        return Math.Clamp(AnimatedHeight, 0, _naturalHeight);
    }
}
