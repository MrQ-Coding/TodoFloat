using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using TodoFloat.Controls;
using TodoFloat.Services;
using TodoFloat.ViewModels;

namespace TodoFloat;

public partial class MainWindow : Window
{
    private static readonly Regex QuickDueTokenRegex = new(@"(?<!\S)(今天|明天|(?:\d{4}年)?\d{1,2}月\d{1,2}日|(?:[01]?\d|2[0-3]):[0-5]\d)(?!\S)", RegexOptions.Compiled);
    private static readonly Regex QuickPriorityTokenRegex = new(@"(?<!\S)(!!|!高|!中|!低|!)(?!\S)", RegexOptions.Compiled);
    private static readonly Regex QuickTagTokenRegex = new(@"(?<!\S)#[\p{L}\p{N}_-]+(?!\S)", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private DateTime _quickCalendarMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private CalendarPickerMode _calendarMode = CalendarPickerMode.Day;
    private int _yearGridStart = (DateTime.Today.Year / 12) * 12;

    // 合并面板里日历的 host：日历内的"年/月"切换只重渲染日历区，不动整个面板（不影响时间选择器）
    private ContentControl? _calendarHost;

    // 底部分类筛选条的溢出面板引用 + 当前隐藏项快照
    private OverflowHidePanel? _categoryOverflowPanel;
    private System.Collections.Generic.List<CategoryFilterViewModel> _hiddenCategoryFilters = new();
    private Point? _taskDragStartPoint;
    private TaskItemViewModel? _taskDragSource;
    private bool _suppressTaskRowClick;
    private IReadOnlyList<long>? _taskDragOriginalOrder;
    private FrameworkElement? _taskDragRow;
    private double _taskDragRowOpacity;
    private TaskDragAdorner? _taskDragAdorner;
    private AdornerLayer? _taskDragAdornerLayer;
    private Point _taskDragOffset;
    private bool _isTaskDragging;
    private bool _hasTaskDragPreview;
    private bool _isFinishingTaskDrag;
    private long? _lastTaskDragTargetId;
    private bool _lastTaskDragInsertAfter;
    private Point? _subtaskDragStartPoint;
    private TaskItemViewModel? _subtaskDragSource;
    private bool _suppressSubtaskClick;
    private bool _suppressCompleteClick;
    private bool _subtaskDragStartedSinceMouseDown;
    private DateTime _suppressSubtaskClickUntilUtc = DateTime.MinValue;
    private IReadOnlyList<long>? _subtaskDragOriginalOrder;
    private FrameworkElement? _subtaskDragRow;
    private double _subtaskDragRowOpacity;
    private TaskDragAdorner? _subtaskDragAdorner;
    private AdornerLayer? _subtaskDragAdornerLayer;
    private Point _subtaskDragOffset;
    private bool _isSubtaskDragging;
    private bool _hasSubtaskDragPreview;
    private bool _isFinishingSubtaskDrag;
    private long? _lastSubtaskDragTargetId;
    private bool _lastSubtaskDragInsertAfter;
    private Point? _topTabDragStartPoint;
    private ToggleButton? _topTabDragSource;
    private double _topTabDragSourceOpacity;
    private TaskDragAdorner? _topTabDragAdorner;
    private AdornerLayer? _topTabDragAdornerLayer;
    private Point _topTabDragOffset;
    private bool _isTopTabDragging;
    private bool _isFinishingTopTabDrag;
    private bool _suppressTopTabClick;
    private bool _isTopTabDragPending;
    private IReadOnlyList<string>? _topTabDragOriginalOrder;
    private bool _hasTopTabDragPreview;
    private string? _lastTopTabDragTargetKey;
    private bool _lastTopTabDragInsertAfter;

    private enum CalendarPickerMode { Day, Month, Year }

    private readonly MainViewModel _vm;
    private readonly SettingsService _settings = App.Settings;
    private readonly DispatcherTimer _saveDebounce;
    private readonly DispatcherTimer _autoHideTimer;
    private DispatcherTimer? _subtaskEditResetTimer;
    private bool _isHidden;
    private bool _isAnimating;
    // 顶部贴边：贴边后露出 2px 当作"勾住"的边沿；TriggerSize 略大让指针更容易唤醒
    private const double PeekSize = 2;
    private const double TriggerSize = 4;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(_settings);
        DataContext = _vm;
        AddHandler(
            UIElement.PreviewMouseDownEvent,
            new MouseButtonEventHandler(TopTab_PreviewMouseDown),
            true);

        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SaveBounds(); };

        // 25ms 轮询：~40Hz，几乎跟刷新率同步，鼠标到顶立刻感知
        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        _autoHideTimer.Tick += AutoHideTick;

        SourceInitialized += MainWindow_SourceInitialized;
        LocationChanged += (_, _) => _saveDebounce.Restart();
        SizeChanged += (_, _) => { _saveDebounce.Restart(); ApplyResponsiveTopBar(); };
        PreviewMouseDown += MainWindow_PreviewMouseDown;
        MouseLeftButtonDown += MainWindow_MouseLeftButtonDown;
        KeyDown += MainWindow_KeyDown;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;

        Loaded += (_, _) =>
        {
            // 启动时如果还是最大化态（上次崩了或异常），先还原避免锁死
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            RestoreWindowBounds();
            _autoHideTimer.Start();
            HookCategoryOverflowPanel();
            ApplySavedTopTabOrder();
            ApplyResponsiveTopBar();
        };
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        // Hide from Alt+Tab
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);

        // Win11: ask DWM to round the actual window corners (anti-aliased, native)
        if (hwnd != IntPtr.Zero)
        {
            var pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        }
    }

    private void RestoreWindowBounds()
    {
        var workArea = SystemParameters.WorkArea;
        Width = Math.Max(MinWidth, _settings.WindowWidth);
        Height = Math.Max(MinHeight, _settings.WindowHeight);

        var l = _settings.WindowLeft;
        var t = _settings.WindowTop;
        if (double.IsNaN(l) || double.IsNaN(t))
        {
            Left = workArea.Right - Width - 8;
            Top = workArea.Top + (workArea.Height - Height) / 2;
        }
        else
        {
            Left = l;
            Top = t;
        }
    }

    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var src = e.OriginalSource as DependencyObject;

        // 搜索模式下：点击输入卡之外任何地方都退出搜索
        // 例外：标题栏的 🔍 按钮自身（它的 Click 会自己 toggle）
        if (_vm.IsSearchMode
            && !_vm.IsSearchOnlyView
            && !IsInside(src, NewTaskRow)
            && (SearchButton == null || !IsInside(src, SearchButton)))
        {
            _vm.IsSearchMode = false;
        }

        if (IsInside(src, NewTaskRow)) return;

        if (NewTaskBox.IsKeyboardFocusWithin)
        {
            Keyboard.ClearFocus();
        }
    }

    private void SaveBounds()
    {
        if (_isAnimating || _isHidden) return;
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
    }

    private void MainWindow_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        // only initiate drag from the drag bar
        var hit = e.OriginalSource as DependencyObject;
        if (!IsInDragBar(hit)) return;
        if (_isHidden) ShowSidebar();
        // 兜底：万一窗口不知怎么进了最大化态，先还原才能 DragMove
        if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
        try { DragMove(); } catch { /* drag race */ }
        SnapToEdge();
    }

    private bool IsInDragBar(DependencyObject? d)
    {
        while (d != null)
        {
            // ButtonBase 涵盖 Button、ToggleButton、RadioButton 等所有可点击控件，
            // 顶栏现在直接放了 tabs 用的 ToggleButton，必须把这些点击事件让出来。
            if (d is System.Windows.Controls.Primitives.ButtonBase) return false;
            if (d is Grid g && g.Name == "DragBar") return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void SnapToEdge()
    {
        var wa = SystemParameters.WorkArea;
        const double snapDistance = 24;
        // 仅检测顶部贴边（左右贴边已废弃）
        if (Math.Abs(Top - wa.Top) < snapDistance)
        {
            Top = wa.Top;
            _settings.SnapEdge = "top";
        }
        else
        {
            _settings.SnapEdge = "none";
        }
        SaveBounds();
    }

    private int _awayTicks;
    private const int HideAfterTicks = 8; // ~200ms at 25ms interval（保留一点防误触）

    private void AutoHideTick(object? sender, EventArgs e)
    {
        if (!_vm.AutoHideEnabled || _settings.SnapEdge != "top")
        {
            if (_isHidden) ShowSidebar();
            _awayTicks = 0;
            return;
        }
        if (_isAnimating) return;

        var wa = SystemParameters.WorkArea;

        if (!GetCursorPos(out var p)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var mx = p.X / dpi.DpiScaleX;
        var my = p.Y / dpi.DpiScaleY;

        // 显示态时窗口的可见区域：贴顶 → 顶部坐标 = wa.Top
        double visTop = wa.Top;
        double visBottom = visTop + Height;

        var overVisibleArea = mx >= Left - 2 && mx <= Left + Width + 2 && my >= visTop - 2 && my <= visBottom + 2;
        var keyboardFocus = IsKeyboardFocusWithin || IsActive;

        // 触发条带：屏幕顶 TriggerSize 像素，且横向在窗口宽度范围内
        bool inTriggerZone = my <= wa.Top + TriggerSize && mx >= Left && mx <= Left + Width;

        if (!_isHidden)
        {
            if (overVisibleArea || keyboardFocus || inTriggerZone)
            {
                _awayTicks = 0;
            }
            else if (++_awayTicks >= HideAfterTicks)
            {
                HideSidebar(_settings.SnapEdge);
            }
        }
        else
        {
            if (inTriggerZone) ShowSidebar();
        }
    }

    private void HideSidebar(string edge)
    {
        if (_isHidden || _isAnimating) return;
        _isAnimating = true;
        var wa = SystemParameters.WorkArea;
        // 顶部贴边：把整窗向上推，只在屏幕顶部留 PeekSize 高度
        var target = wa.Top - Height + PeekSize;
        var anim = new DoubleAnimation
        {
            From = Top,
            To = target,
            Duration = TimeSpan.FromMilliseconds(70),
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (_, _) => { _isAnimating = false; _isHidden = true; };
        BeginAnimation(TopProperty, anim);
    }

    private void ShowSidebar()
    {
        if (!_isHidden && !_isAnimating) return;
        _isAnimating = true;
        var wa = SystemParameters.WorkArea;
        var target = wa.Top;
        var anim = new DoubleAnimation
        {
            From = Top,
            To = target,
            Duration = TimeSpan.FromMilliseconds(70),
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (_, _) =>
        {
            _isAnimating = false;
            _isHidden = false;
            BeginAnimation(TopProperty, null);
            Top = target;
            _awayTicks = 0;
        };
        BeginAnimation(TopProperty, anim);
    }

    private void NewTaskBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            // 搜索模式下不创建任务（搜索是 live 的，Enter 不需要做事）
            if (_vm.IsSearchMode)
            {
                e.Handled = true;
                return;
            }
            SubmitQuickAddOrFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_vm.IsSearchMode)
            {
                // 第一次 Esc：有内容就清空（保留搜索模式）；第二次 Esc：退出搜索
                if (!string.IsNullOrEmpty(_vm.QuickInput))
                {
                    _vm.QuickInput = string.Empty;
                }
                else if (!_vm.IsSearchOnlyView)
                {
                    _vm.IsSearchMode = false;
                }
                else
                {
                    Keyboard.ClearFocus();
                }
            }
            else
            {
                _vm.QuickInput = string.Empty;
                Keyboard.ClearFocus();
            }
            e.Handled = true;
        }
    }

    private void QuickAddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsSearchOnlyView)
        {
            NewTaskBox.Focus();
            e.Handled = true;
            return;
        }

        SubmitQuickAddOrFocus();
        e.Handled = true;
    }

    private void NewTaskRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;
        NewTaskBox.Focus();
        e.Handled = true;
    }

    private void SubmitQuickAddOrFocus()
    {
        if (_vm.IsSearchOnlyView)
        {
            NewTaskBox.Focus();
            return;
        }

        if (string.IsNullOrWhiteSpace(_vm.QuickInput))
        {
            NewTaskBox.Focus();
            return;
        }

        _vm.QuickAddCommand.Execute(null);
        NewTaskBox.Focus();
    }

    private void QuickDueButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        // 起始月份：优先用已有 chip 的月份，没有就用今天
        var anchor = _vm.CurrentChipDue ?? DateTime.Today;
        _quickCalendarMonth = new DateTime(anchor.Year, anchor.Month, 1);
        _yearGridStart = (anchor.Year / 12) * 12;
        _calendarMode = CalendarPickerMode.Day;
        InitTimePickerSelection();
        OpenQuickAddFlyout(button, CreateQuickDateTimePanel());
        e.Handled = true;
    }

    private void QuickPriorityButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        OpenQuickAddFlyout(button, BuildPriorityPanel());
        e.Handled = true;
    }

    private void QuickTagButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        OpenQuickAddFlyout(button, BuildCategoryPanel());
        e.Handled = true;
    }

    private StackPanel BuildPriorityPanel()
    {
        var panel = CreateFlyoutStack();
        panel.Children.Add(CreateQuickFlyoutButton(CreateFlyoutRow("\xE7C1", "高优先级", "!!", (Brush)FindResource("DangerBrush")), () => ApplyPriority("!!")));
        panel.Children.Add(CreateQuickFlyoutButton(CreateFlyoutRow("\xE7C1", "中优先级", "!", (Brush)FindResource("AccentBrush")), () => ApplyPriority("!")));
        panel.Children.Add(CreateQuickFlyoutButton(CreateFlyoutRow("\xE7C1", "低优先级", "!低", (Brush)FindResource("MutedBrush")), () => ApplyPriority("!低")));
        return panel;
    }

    // 分类 picker 是否已展开全部（点了 "…" 之后置 true，关闭弹窗复位）
    private bool _categoryPickerExpanded;
    private const int CategoryPickerVisibleCap = 5;

    private StackPanel BuildCategoryPanel()
    {
        _categoryPickerExpanded = false;
        var panel = CreateFlyoutStack();
        panel.MinWidth = 160;
        panel.MaxWidth = 180;

        var (searchWrap, searchBox) = BuildPickerSearchBox("搜索标签…");
        panel.Children.Add(searchWrap);

        var listHost = new WrapPanel { Margin = new Thickness(2, 0, 2, 2) };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 130,
            Content = listHost
        };
        panel.Children.Add(scroll);

        searchBox.TextChanged += (_, _) => RenderCategoryItems(listHost, searchBox.Text);
        RenderCategoryItems(listHost, string.Empty);

        Dispatcher.BeginInvoke(new Action(() => searchBox.Focus()), DispatcherPriority.Input);
        return panel;
    }

    /// <summary>
    /// 创建一个有 hover/focus 高亮 + 占位符的搜索框，返回 (外层 Border, 内部 TextBox)。
    /// 默认值/边框/背景必须挂在 Style.Setters，否则 Trigger 改不动（被本地值压住）。
    /// </summary>
    private (FrameworkElement Wrap, TextBox Box) BuildPickerSearchBox(string placeholder)
    {
        var box = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 12,
            Padding = new Thickness(8, 5, 8, 5),
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextBrush")
        };

        var hint = new TextBlock
        {
            Text = placeholder,
            FontSize = 12,
            Foreground = (Brush)FindResource("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            IsHitTestVisible = false
        };
        var hintStyle = new Style(typeof(TextBlock));
        hintStyle.Setters.Add(new Setter(TextBlock.VisibilityProperty, Visibility.Collapsed));
        var emptyTrigger = new DataTrigger
        {
            Binding = new System.Windows.Data.Binding("Text") { Source = box },
            Value = ""
        };
        emptyTrigger.Setters.Add(new Setter(TextBlock.VisibilityProperty, Visibility.Visible));
        hintStyle.Triggers.Add(emptyTrigger);
        hint.Style = hintStyle;

        var inner = new Grid();
        inner.Children.Add(box);
        inner.Children.Add(hint);

        var wrap = new Border
        {
            Margin = new Thickness(4, 4, 4, 6),
            Child = inner
        };

        // 默认/hover/focus 通过 Style.Setters + Triggers，不在元素上硬写本地值
        var wrapStyle = new Style(typeof(Border));
        wrapStyle.Setters.Add(new Setter(Border.BackgroundProperty, FindResource("Panel2Brush")));
        wrapStyle.Setters.Add(new Setter(Border.BorderBrushProperty, FindResource("BorderBrushSoft")));
        wrapStyle.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1)));
        wrapStyle.Setters.Add(new Setter(Border.CornerRadiusProperty, new CornerRadius(7)));

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, FindResource("AccentBrush")));
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty, FindResource("PanelBrush")));
        wrapStyle.Triggers.Add(hoverTrigger);

        var focusTrigger = new Trigger { Property = Border.IsKeyboardFocusWithinProperty, Value = true };
        focusTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, FindResource("AccentBrush")));
        focusTrigger.Setters.Add(new Setter(Border.BackgroundProperty, FindResource("PanelBrush")));
        wrapStyle.Triggers.Add(focusTrigger);

        wrap.Style = wrapStyle;
        return (wrap, box);
    }

    private void RenderCategoryItems(Panel host, string filter)
    {
        host.Children.Clear();
        var query = filter?.Trim() ?? string.Empty;

        // 过滤匹配项
        var matched = _vm.Categories
            .Select(c => c.Name?.Trim())
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Zip(_vm.Categories, (n, c) => (Name: n!, Color: c.Color))
            .Where(t => query.Length == 0 || t.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();

        if (matched.Count == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = "无匹配标签",
                FontSize = 11.5,
                Foreground = (Brush)FindResource("MutedBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(8, 8, 8, 8)
            });
            return;
        }

        // 没在搜索 + 没展开 + 数量超过阈值 → 显示前 Cap 个 + 一个 "…" pill
        var collapse = query.Length == 0
                       && !_categoryPickerExpanded
                       && matched.Count > CategoryPickerVisibleCap;
        var visibleCount = collapse ? CategoryPickerVisibleCap : matched.Count;

        for (var i = 0; i < visibleCount; i++)
        {
            host.Children.Add(CreateTagPill(matched[i].Name, matched[i].Color));
        }
        if (collapse)
        {
            host.Children.Add(CreateMorePill(host));
        }
    }

    /// <summary>"…" pill：点击展开剩余标签</summary>
    private Border CreateMorePill(Panel host)
    {
        var label = new TextBlock
        {
            Text = "…",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, -4, 0, 0)
        };
        var pill = new Border
        {
            Style = (Style)FindResource("ClickablePill"),
            Margin = new Thickness(2, 2, 2, 2),
            MinWidth = 32,
            Child = label
        };
        pill.MouseLeftButtonUp += (_, _) =>
        {
            _categoryPickerExpanded = true;
            RenderCategoryItems(host, string.Empty);
        };
        return pill;
    }

    /// <summary>
    /// 单个标签 pill（水平排列）：色点 + #名字，hover 边框变主色，点击应用并关弹窗
    /// </summary>
    private Border CreateTagPill(string name, string color)
    {
        var dot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = BrushFromHex(color),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 5, 0)
        };
        var label = new TextBlock
        {
            Text = name,
            FontSize = 11.5,
            Foreground = (Brush)FindResource("TextBrush2"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(dot);
        content.Children.Add(label);

        var pill = new Border
        {
            Style = (Style)FindResource("ClickablePill"),
            Margin = new Thickness(2, 2, 2, 2),
            Child = content
        };
        pill.MouseLeftButtonUp += (_, _) =>
        {
            ApplyCategory(name);
            QuickFlyout.IsOpen = false;
        };
        return pill;
    }

    private void ApplyPriority(string? raw)
    {
        if (_editingTask is { } t)
        {
            if (raw is null) _vm.UpdateTaskPriority(t, "");
            else _vm.UpdateTaskPriority(t, raw);
        }
        else
        {
            if (raw is null) _vm.ClearPriorityChip();
            else _vm.SetPriorityChip(raw);
        }
    }

    private void ApplyCategory(string name)
    {
        if (_editingTask is { } t) _vm.UpdateTaskCategory(t, name);
        else _vm.SetCategoryChip(name);
    }

    private StackPanel CreateFlyoutStack() => new() { MinWidth = 140 };

    private StackPanel CreateQuickCalendarPanel()
    {
        var panel = new StackPanel { MinWidth = 248 };
        switch (_calendarMode)
        {
            case CalendarPickerMode.Day:
                panel.Children.Add(CreateCalendarHeader());
                panel.Children.Add(CreateWeekdayRow());
                panel.Children.Add(CreateCalendarDayGrid());
                break;
            case CalendarPickerMode.Month:
                panel.Children.Add(CreateMonthPickerHeader());
                panel.Children.Add(CreateMonthGrid());
                break;
            case CalendarPickerMode.Year:
                panel.Children.Add(CreateYearPickerHeader());
                panel.Children.Add(CreateYearGrid());
                break;
        }
        return panel;
    }

    // 合并面板：日历 + 分隔线 + 时间选择器，竖向堆叠
    private FrameworkElement CreateQuickDateTimePanel()
    {
        var panel = new StackPanel { MinWidth = 264 };

        _calendarHost = new ContentControl { Content = CreateQuickCalendarPanel() };
        panel.Children.Add(_calendarHost);

        panel.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)FindResource("BorderBrushSoft"),
            Margin = new Thickness(8, 4, 8, 2)
        });

        panel.Children.Add(CreateTimePickerPanel());
        return panel;
    }

    // 在打开合并面板前调用：根据当前编辑/Quick 状态把待选时间预置到 _pendingHour/_pendingMinute
    private void InitTimePickerSelection()
    {
        var existing = _editingTask?.DueAt?.TimeOfDay ?? _vm.CurrentChipDue?.TimeOfDay;
        _pendingHour = null;
        _pendingMinute = null;
        _selectedHourButton = null;
        _selectedMinuteButton = null;
        if (existing is { } t)
        {
            // 默认 23:59 是"无具体时间"的占位，跳过预选
            if (!(t.Hours == 23 && t.Minutes == 59))
            {
                _pendingHour = t.Hours;
                if (t.Minutes is 0 or 15 or 30 or 45) _pendingMinute = t.Minutes;
            }
        }
    }

    // 把 _calendarHost.Content 重新渲染为当前 _calendarMode 对应的视图。
    // 替换早期版本里的 `QuickFlyoutContent.Content = CreateQuickCalendarPanel();`，
    // 这样切年/月/日时不会重渲染时间选择器。
    //
    // 重要：所有调用点都在 Button.Click 内（日期按钮、年/月按钮、上下月切换…）。
    // 如果同步替换 Content，当前点击的 Button 自身会随旧可视树一起被移除，
    // 而 WPF 还在路由 Click 事件，引用已无效的元素 → AccessViolation 闪退。
    // 用 BeginInvoke 把替换推到事件队列尾，等 Click 处理完再执行。
    private void RefreshCalendarHost()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_calendarHost is not null)
                _calendarHost.Content = CreateQuickCalendarPanel();
            else
                QuickFlyoutContent.Content = CreateQuickCalendarPanel();
        }), DispatcherPriority.Background);
    }

    private int? _pendingHour;
    private int? _pendingMinute;
    private Button? _selectedHourButton;
    private Button? _selectedMinuteButton;

    private StackPanel CreateTimePickerPanel()
    {
        var panel = new StackPanel { MinWidth = 180 };
        panel.Children.Add(new TextBlock
        {
            Text = "时间",
            FontSize = 11.5,
            Foreground = (Brush)FindResource("MutedBrush"),
            Margin = new Thickness(10, 6, 10, 8),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        // Column header row: "小时" | "分钟"
        var headerRow = new Grid { Margin = new Thickness(6, 0, 6, 4) };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var hourLabel = new TextBlock
        {
            Text = "小时",
            FontSize = 11,
            Foreground = (Brush)FindResource("MutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        };
        var spacer = new Border { Width = 1 };
        var minuteLabel = new TextBlock
        {
            Text = "分钟",
            FontSize = 11,
            Foreground = (Brush)FindResource("MutedBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4)
        };
        Grid.SetColumn(hourLabel, 0);
        Grid.SetColumn(spacer, 1);
        Grid.SetColumn(minuteLabel, 2);
        headerRow.Children.Add(hourLabel);
        headerRow.Children.Add(spacer);
        headerRow.Children.Add(minuteLabel);
        panel.Children.Add(headerRow);

        // Body: two scrollable columns sharing the same width and divider
        // Height fits 4 cells (matches minute count: 00/15/30/45) — slight 5th-row tease for scroll affordance
        var grid = new Grid { Margin = new Thickness(6, 0, 6, 6), Height = 124 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var hourScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var hourStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        for (var h = 0; h < 24; h++)
        {
            hourStack.Children.Add(CreateTimeListCell(h.ToString("00"), value: h, isMinute: false, initiallySelected: _pendingHour == h));
        }
        hourScroll.Content = hourStack;
        Grid.SetColumn(hourScroll, 0);
        grid.Children.Add(hourScroll);

        var divider = new Border
        {
            Width = 1,
            Background = (Brush)FindResource("BorderBrushSoft"),
            Margin = new Thickness(2, 4, 2, 4)
        };
        Grid.SetColumn(divider, 1);
        grid.Children.Add(divider);

        var minuteScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var minuteStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var m in new[] { 0, 15, 30, 45 })
        {
            minuteStack.Children.Add(CreateTimeListCell(m.ToString("00"), value: m, isMinute: true, initiallySelected: _pendingMinute == m));
        }
        minuteScroll.Content = minuteStack;
        Grid.SetColumn(minuteScroll, 2);
        grid.Children.Add(minuteScroll);

        panel.Children.Add(grid);
        return panel;
    }

    private Button CreateTimeListCell(string label, int value, bool isMinute, bool initiallySelected = false)
    {
        var btn = new Button
        {
            Content = label,
            FontSize = 12.5,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = (Brush)FindResource("TextBrush2"),
            Cursor = System.Windows.Input.Cursors.Hand,
            Padding = new Thickness(0, 6, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center
        };
        btn.Template = new ControlTemplate(typeof(Button))
        {
            VisualTree = MakeTitleButtonTemplate()
        };

        // 打开面板时按 _pendingHour/_pendingMinute 预选高亮
        if (initiallySelected)
        {
            btn.Background = (Brush)FindResource("AccentSoftBrush");
            btn.Foreground = (Brush)FindResource("AccentInkBrush");
            btn.FontWeight = FontWeights.SemiBold;
            if (isMinute) _selectedMinuteButton = btn;
            else _selectedHourButton = btn;
        }

        // Hover only when not selected (selected styling wins)
        btn.MouseEnter += (_, _) =>
        {
            if (btn == _selectedHourButton || btn == _selectedMinuteButton) return;
            btn.Background = (Brush)FindResource("RowHoverBrush");
        };
        btn.MouseLeave += (_, _) =>
        {
            if (btn == _selectedHourButton || btn == _selectedMinuteButton) return;
            btn.Background = Brushes.Transparent;
        };

        btn.Click += (_, _) =>
        {
            if (isMinute)
            {
                ApplyTimeSelection(ref _selectedMinuteButton, btn);
                _pendingMinute = value;
            }
            else
            {
                ApplyTimeSelection(ref _selectedHourButton, btn);
                _pendingHour = value;
            }

            // 两个轮都选好后即应用，但**不再自动关 popup**
            // —— 用户可能想接着调日期，关闭交给点击外部。
            if (_pendingHour is { } h && _pendingMinute is { } m)
            {
                if (_editingTask is { } t)
                {
                    var date = t.DueAt?.Date ?? DateTime.Today;
                    _vm.UpdateTaskDue(t, date.Add(new TimeSpan(h, m, 0)));
                }
                else
                {
                    SetTimeChip(h, m);
                }
            }
        };
        return btn;
    }

    private void ApplyTimeSelection(ref Button? slot, Button newBtn)
    {
        // Clear previous selection's styling so it returns to default
        if (slot is not null && slot != newBtn)
        {
            slot.Background = Brushes.Transparent;
            slot.Foreground = (Brush)FindResource("TextBrush2");
            slot.FontWeight = FontWeights.Normal;
        }
        newBtn.Background = (Brush)FindResource("AccentSoftBrush");
        newBtn.Foreground = (Brush)FindResource("AccentInkBrush");
        newBtn.FontWeight = FontWeights.SemiBold;
        slot = newBtn;
    }

    private void SetTimeChip(int hour, int minute) =>
        _vm.SetDueChip(DateTime.Today.Date.Add(new TimeSpan(hour, minute, 0)), hadDate: false);

    private Grid CreateCalendarHeader()
    {
        var header = new Grid { Margin = new Thickness(6, 3, 6, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var prev = CreateCalendarNavButton("\xE76B");
        prev.Click += (_, _) => ShiftQuickCalendarMonth(-1);

        // Title is two separate clickable parts: "2026年" → Year picker, "5月" → Month picker
        var titleStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var yearBtn = CreateCalendarTitleButton($"{_quickCalendarMonth.Year}年");
        yearBtn.Click += (_, _) =>
        {
            _yearGridStart = (_quickCalendarMonth.Year / 12) * 12;
            _calendarMode = CalendarPickerMode.Year;
            RefreshCalendarHost();
        };
        var monthBtn = CreateCalendarTitleButton($"{_quickCalendarMonth.Month}月");
        monthBtn.Click += (_, _) =>
        {
            _calendarMode = CalendarPickerMode.Month;
            RefreshCalendarHost();
        };
        titleStack.Children.Add(yearBtn);
        titleStack.Children.Add(monthBtn);

        var next = CreateCalendarNavButton("\xE76C");
        next.Click += (_, _) => ShiftQuickCalendarMonth(1);

        header.Children.Add(prev);
        Grid.SetColumn(titleStack, 1);
        header.Children.Add(titleStack);
        Grid.SetColumn(next, 2);
        header.Children.Add(next);
        return header;
    }

    private Grid CreateMonthPickerHeader()
    {
        var header = new Grid { Margin = new Thickness(6, 3, 6, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var prev = CreateCalendarNavButton("\xE76B");
        prev.Click += (_, _) => ShiftQuickCalendarYear(-1);
        var title = CreateCalendarTitleButton($"{_quickCalendarMonth.Year}年");
        title.Click += (_, _) =>
        {
            _yearGridStart = (_quickCalendarMonth.Year / 12) * 12;
            _calendarMode = CalendarPickerMode.Year;
            RefreshCalendarHost();
        };
        var next = CreateCalendarNavButton("\xE76C");
        next.Click += (_, _) => ShiftQuickCalendarYear(1);

        header.Children.Add(prev);
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        Grid.SetColumn(next, 2);
        header.Children.Add(next);
        return header;
    }

    private Grid CreateYearPickerHeader()
    {
        var header = new Grid { Margin = new Thickness(6, 3, 6, 8) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var prev = CreateCalendarNavButton("\xE76B");
        prev.Click += (_, _) =>
        {
            _yearGridStart -= 12;
            RefreshCalendarHost();
        };
        var title = new TextBlock
        {
            Text = $"{_yearGridStart} - {_yearGridStart + 11}",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var next = CreateCalendarNavButton("\xE76C");
        next.Click += (_, _) =>
        {
            _yearGridStart += 12;
            RefreshCalendarHost();
        };

        header.Children.Add(prev);
        Grid.SetColumn(title, 1);
        header.Children.Add(title);
        Grid.SetColumn(next, 2);
        header.Children.Add(next);
        return header;
    }

    private UniformGrid CreateYearGrid()
    {
        var grid = new UniformGrid { Columns = 3, Rows = 4, Margin = new Thickness(8, 0, 8, 8) };
        for (var i = 0; i < 12; i++)
        {
            var year = _yearGridStart + i;
            var isCurrent = year == _quickCalendarMonth.Year;
            var btn = new Button
            {
                Content = year.ToString(),
                Style = (Style)FindResource("QuickCalendarDayButton"),
                Width = double.NaN, // override style's 28 → auto, fits "2016"
                MinWidth = 52,
                FontSize = 12,
                Margin = new Thickness(2),
                Foreground = (Brush)FindResource("TextBrush2")
            };
            if (isCurrent)
            {
                btn.Background = (Brush)FindResource("AccentSoftBrush");
                btn.Foreground = (Brush)FindResource("AccentInkBrush");
                btn.FontWeight = FontWeights.SemiBold;
            }
            btn.Click += (_, _) =>
            {
                _quickCalendarMonth = new DateTime(year, _quickCalendarMonth.Month, 1);
                _calendarMode = CalendarPickerMode.Month;
                RefreshCalendarHost();
            };
            grid.Children.Add(btn);
        }
        return grid;
    }

    private Button CreateCalendarTitleButton(string text)
    {
        var btn = new Button
        {
            Content = text,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush"),
            Cursor = System.Windows.Input.Cursors.Hand,
            Padding = new Thickness(3, 2, 3, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        btn.Template = new ControlTemplate(typeof(Button))
        {
            VisualTree = MakeTitleButtonTemplate()
        };
        btn.MouseEnter += (_, _) => btn.Background = (Brush)FindResource("RowHoverBrush");
        btn.MouseLeave += (_, _) => btn.Background = Brushes.Transparent;
        return btn;
    }

    private static FrameworkElementFactory MakeTitleButtonTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(Button.BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        border.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Button.PaddingProperty));

        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        return border;
    }

    private UniformGrid CreateMonthGrid()
    {
        var grid = new UniformGrid { Columns = 3, Rows = 4, Margin = new Thickness(8, 0, 8, 8) };
        for (var m = 1; m <= 12; m++)
        {
            var month = m;
            var isCurrent = month == _quickCalendarMonth.Month;
            var btn = new Button
            {
                Content = $"{m}月",
                Style = (Style)FindResource("QuickCalendarDayButton"),
                FontSize = 12,
                Margin = new Thickness(2),
                Foreground = (Brush)FindResource("TextBrush2")
            };
            if (isCurrent)
            {
                btn.Background = (Brush)FindResource("AccentSoftBrush");
                btn.Foreground = (Brush)FindResource("AccentInkBrush");
                btn.FontWeight = FontWeights.SemiBold;
            }
            btn.Click += (_, _) =>
            {
                _quickCalendarMonth = new DateTime(_quickCalendarMonth.Year, month, 1);
                _calendarMode = CalendarPickerMode.Day;
                RefreshCalendarHost();
            };
            grid.Children.Add(btn);
        }
        return grid;
    }

    private void ShiftQuickCalendarYear(int years)
    {
        var target = _quickCalendarMonth.AddYears(years);
        _quickCalendarMonth = new DateTime(target.Year, _quickCalendarMonth.Month, 1);
        RefreshCalendarHost();
    }

    private Button CreateCalendarNavButton(string glyph)
    {
        return new Button
        {
            Content = glyph,
            Style = (Style)FindResource("GlyphButton"),
            Width = 24,
            Height = 24,
            FontSize = 10.5,
            Foreground = (Brush)FindResource("MutedBrush")
        };
    }

    private UniformGrid CreateWeekdayRow()
    {
        var row = new UniformGrid { Columns = 7, Rows = 1, Margin = new Thickness(8, 0, 8, 3) };
        foreach (var day in new[] { "一", "二", "三", "四", "五", "六", "日" })
        {
            row.Children.Add(new TextBlock
            {
                Text = day,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("MutedBrush"),
                TextAlignment = TextAlignment.Center
            });
        }

        return row;
    }

    private UniformGrid CreateCalendarDayGrid()
    {
        var grid = new UniformGrid { Columns = 7, Rows = 6, Margin = new Thickness(8, 0, 8, 7) };
        var firstOfMonth = _quickCalendarMonth;
        var mondayOffset = ((int)firstOfMonth.DayOfWeek + 6) % 7;
        var start = firstOfMonth.AddDays(-mondayOffset);

        for (var i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            grid.Children.Add(CreateCalendarDayButton(date));
        }

        return grid;
    }

    private Button CreateCalendarDayButton(DateTime date)
    {
        var isCurrentMonth = date.Month == _quickCalendarMonth.Month && date.Year == _quickCalendarMonth.Year;
        var isToday = date.Date == DateTime.Today;
        // 已经被选中的日期：编辑模式看 _editingTask.DueAt；快速添加模式看 _vm.CurrentChipDue
        var selectedDate = (_editingTask?.DueAt ?? _vm.CurrentChipDue)?.Date;
        var isSelected = selectedDate == date.Date;

        var button = new Button
        {
            Content = date.Day.ToString(),
            Style = (Style)FindResource("QuickCalendarDayButton"),
            Opacity = isCurrentMonth ? 1.0 : 0.45,
            Foreground = isCurrentMonth
                ? (Brush)FindResource("TextBrush2")
                : (Brush)FindResource("MutedBrush")
        };

        // 选中态优先级最高（深底白字），今天次之（浅底主色）
        if (isSelected)
        {
            button.Background = (Brush)FindResource("AccentBrush");
            button.Foreground = Brushes.White;
            button.FontWeight = FontWeights.SemiBold;
        }
        else if (isToday)
        {
            button.Background = (Brush)FindResource("AccentSoftBrush");
            button.BorderBrush = (Brush)FindResource("AccentBrush");
            button.Foreground = (Brush)FindResource("AccentInkBrush");
            button.FontWeight = FontWeights.SemiBold;
        }

        button.Click += (_, _) =>
        {
            if (_editingTask is { } t)
            {
                // Preserve existing time when only changing the date
                var existingTime = t.DueAt?.TimeOfDay ?? new TimeSpan(23, 59, 0);
                _vm.UpdateTaskDue(t, date.Date.Add(existingTime));
            }
            else
            {
                _vm.SetDueChip(date, hadDate: true);
            }
            // 不自动关 popup：用户可能还要选时间。
            // RefreshCalendarHost 内部已用 BeginInvoke 延迟，避免 Click 期间销毁自身按钮的崩溃。
            RefreshCalendarHost();
        };
        return button;
    }

    private void ShiftQuickCalendarMonth(int months)
    {
        var target = _quickCalendarMonth.AddMonths(months);
        _quickCalendarMonth = new DateTime(target.Year, target.Month, 1);
        RefreshCalendarHost();
    }

    private Button CreateQuickFlyoutButton(object content, Action action)
    {
        var button = new Button
        {
            Content = content,
            Style = (Style)FindResource("QuickFlyoutButton")
        };
        button.Click += (_, _) =>
        {
            action();
            QuickFlyout.IsOpen = false;
        };
        return button;
    }

    private Grid CreateFlyoutRow(string glyph, string text, string? hint, Brush? glyphBrush = null)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11.5,
            Foreground = glyphBrush ?? (Brush)FindResource("MutedBrush"),
            VerticalAlignment = VerticalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = text,
            FontSize = 12,
            Foreground = (Brush)FindResource("TextBrush2"),
            VerticalAlignment = VerticalAlignment.Center
        };

        row.Children.Add(icon);
        Grid.SetColumn(label, 1);
        row.Children.Add(label);

        if (!string.IsNullOrWhiteSpace(hint))
        {
            var hintText = new TextBlock
            {
                Text = hint,
                FontSize = 11,
                Foreground = (Brush)FindResource("MutedBrush"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            Grid.SetColumn(hintText, 2);
            row.Children.Add(hintText);
        }

        return row;
    }

    private Grid CreateTagRow(string name, string color)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var dot = new Ellipse
        {
            Width = 7,
            Height = 7,
            Fill = BrushFromHex(color),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var label = new TextBlock
        {
            Text = name,
            FontSize = 12,
            Foreground = (Brush)FindResource("TextBrush2"),
            VerticalAlignment = VerticalAlignment.Center
        };

        row.Children.Add(dot);
        Grid.SetColumn(label, 1);
        row.Children.Add(label);
        return row;
    }

    private Border CreateFlyoutDivider() => new()
    {
        Height = 1,
        Background = (Brush)FindResource("BorderBrushSoft"),
        Margin = new Thickness(4, 5, 4, 5)
    };

    private TaskItemViewModel? _editingTask;

    private void OpenQuickFlyout(FrameworkElement target, FrameworkElement content)
    {
        QuickFlyout.IsOpen = false;
        QuickFlyoutContent.Content = content;
        QuickFlyout.PlacementTarget = target;
        QuickFlyout.Placement = PlacementMode.Bottom;
        QuickFlyout.HorizontalOffset = -Math.Max(0, content.MinWidth - target.ActualWidth);
        QuickFlyout.Closed -= QuickFlyout_Closed;
        QuickFlyout.Closed += QuickFlyout_Closed;
        QuickFlyout.IsOpen = true;
    }

    /// <summary>
    /// Open a flyout from one of the quick-add row's icon buttons. Forces the row to its
    /// "active" highlight so it doesn't dim while focus moves into the flyout. Chip-based
    /// edits (on existing tasks) MUST NOT call this — they go through OpenQuickFlyout directly.
    /// </summary>
    private void OpenQuickAddFlyout(FrameworkElement target, FrameworkElement content)
    {
        NewTaskRow.Tag = "active";
        OpenQuickFlyout(target, content);
    }

    private void QuickFlyout_Closed(object? sender, EventArgs e)
    {
        NewTaskRow.Tag = null;
        _editingTask = null;
        _calendarHost = null;
        MoveQuickCaretToEnd();
    }

    private void UpsertQuickToken(Regex tokenRegex, string? token)
    {
        var text = NormalizeQuickInput(tokenRegex.Replace(_vm.QuickInput, " "));
        if (!string.IsNullOrWhiteSpace(token))
        {
            text = string.IsNullOrWhiteSpace(text) ? token : $"{text} {token}";
        }

        _vm.QuickInput = text;
        MoveQuickCaretToEnd();
    }

    private void MoveQuickCaretToEnd()
    {
        NewTaskBox.Focus();
        NewTaskBox.CaretIndex = NewTaskBox.Text.Length;
    }

    private static string NormalizeQuickInput(string text) => WhitespaceRegex.Replace(text, " ").Trim();

    private static string FormatQuickDateToken(DateTime date)
    {
        var today = DateTime.Today;
        if (date.Date == today) return "今天";
        if (date.Date == today.AddDays(1)) return "明天";
        if (date.Year == today.Year) return $"{date.Month}月{date.Day}日";
        return $"{date.Year}年{date.Month}月{date.Day}日";
    }

    private static SolidColorBrush BrushFromHex(string? hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex) is Color color)
            {
                return new SolidColorBrush(color);
            }
        }
        catch
        {
            // Fall through to muted color.
        }

        return new SolidColorBrush(Colors.Transparent);
    }

    private void BtnSearch_Click(object sender, RoutedEventArgs e)
    {
        // 🔍 是"进入搜索"动作，不是开关：已在搜索态再点只重新聚焦输入框
        // 退出搜索请用 Esc / 点击输入框外区域
        if (!_vm.IsSearchMode) _vm.IsSearchMode = true;
        Dispatcher.BeginInvoke(new Action(() => { NewTaskBox.Focus(); NewTaskBox.SelectAll(); }),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        // 有内容 → 仅清空；已为空再点 → 退出搜索模式
        if (!string.IsNullOrEmpty(_vm.QuickInput))
        {
            _vm.QuickInput = string.Empty; // OnQuickInputChanged 会同步 SearchText
            NewTaskBox.Focus();
        }
        else if (!_vm.IsSearchOnlyView)
        {
            _vm.IsSearchMode = false;
            Dispatcher.BeginInvoke(new Action(() => NewTaskBox.Focus()),
                System.Windows.Threading.DispatcherPriority.Input);
        }
        else
        {
            NewTaskBox.Focus();
        }
    }

    private void CompleteCheckbox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is TaskItemViewModel vm)
        {
            if (vm.Model.ParentId is not null
                && (_suppressCompleteClick || DateTime.UtcNow < _suppressSubtaskClickUntilUtc))
            {
                _suppressCompleteClick = false;
                e.Handled = true;
                return;
            }

            // 缩放脉冲做视觉反馈：1 → 1.25 → 1，~160ms
            var scale = new ScaleTransform(1, 1);
            cb.RenderTransform = scale;
            cb.RenderTransformOrigin = new Point(0.5, 0.5);
            var anim = new DoubleAnimation
            {
                From = 1.0,
                To = 1.25,
                Duration = TimeSpan.FromMilliseconds(80),
                AutoReverse = true,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, anim);

            var shouldMoveCompletedSubtask = vm.Model.ParentId is not null && !vm.Completed;
            var previousSubtaskBounds = shouldMoveCompletedSubtask ? CaptureSubtaskRowBounds() : null;

            _vm.ToggleCompleteCommand.Execute(vm);

            if (shouldMoveCompletedSubtask && previousSubtaskBounds is not null)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!_vm.MoveCompletedSubtaskToTail(vm)) return;

                    UpdateLayout();
                    AnimateSubtaskRowsFrom(previousSubtaskBounds);
                }), DispatcherPriority.ContextIdle);
            }
        }
    }

    private void TopTab_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        ClearTopTabPendingDrag();
        _topTabDragStartPoint = null;
        _topTabDragSource = null;

        if (e.ChangedButton != MouseButton.Left) return;

        if (FindTopTabFromSource(e.OriginalSource as DependencyObject) is not { } tab) return;
        _topTabDragStartPoint = e.GetPosition(this);
        _topTabDragSource = tab;
        _isTopTabDragPending = true;
        AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(TopTabPending_PreviewMouseMove), true);
        AddHandler(UIElement.PreviewMouseUpEvent, new MouseButtonEventHandler(TopTabPending_PreviewMouseUp), true);
    }

    private void TopTabPending_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isTopTabDragPending) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            ClearTopTabPendingDrag();
            return;
        }

        if (_topTabDragStartPoint is not { } start || _topTabDragSource is null) return;

        var current = e.GetPosition(this);
        var movedEnough =
            Math.Abs(current.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(current.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;
        if (!movedEnough) return;

        var source = _topTabDragSource;
        ClearTopTabPendingDrag(keepSource: true);
        BeginTopTabDrag(source, e.GetPosition(RootGrid));
        e.Handled = true;
    }

    private void TopTabPending_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        ClearTopTabPendingDrag();
    }

    private void ClearTopTabPendingDrag(bool keepSource = false)
    {
        if (_isTopTabDragPending)
        {
            RemoveHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(TopTabPending_PreviewMouseMove));
            RemoveHandler(UIElement.PreviewMouseUpEvent, new MouseButtonEventHandler(TopTabPending_PreviewMouseUp));
        }

        _isTopTabDragPending = false;
        _topTabDragStartPoint = null;
        if (!keepSource)
        {
            _topTabDragSource = null;
        }
    }

    private void TopTab_Click(object sender, RoutedEventArgs e)
    {
        if (!_suppressTopTabClick) return;

        _suppressTopTabClick = false;
        e.Handled = true;
    }

    private void SuppressNextTopTabClick()
    {
        _suppressTopTabClick = true;
        Dispatcher.BeginInvoke(new Action(() => _suppressTopTabClick = false), DispatcherPriority.ContextIdle);
    }

    private void BeginTopTabDrag(ToggleButton tab, Point rootPoint)
    {
        if (_isTopTabDragging) return;

        _isTopTabDragging = true;
        _topTabDragOriginalOrder = SnapshotTopTabOrder();
        _hasTopTabDragPreview = false;
        _lastTopTabDragTargetKey = null;
        _lastTopTabDragInsertAfter = false;
        _topTabDragSource = tab;
        _topTabDragSourceOpacity = tab.Opacity;

        var tabTopLeft = tab.TranslatePoint(new Point(0, 0), RootGrid);
        _topTabDragOffset = new Point(rootPoint.X - tabTopLeft.X, rootPoint.Y - tabTopLeft.Y);

        var snapshot = RenderElementSnapshot(tab);
        _topTabDragAdornerLayer = AdornerLayer.GetAdornerLayer(RootGrid);
        if (snapshot is not null && _topTabDragAdornerLayer is not null)
        {
            _topTabDragAdorner = new TaskDragAdorner(RootGrid, snapshot, tab.ActualWidth, tab.ActualHeight);
            _topTabDragAdornerLayer.Add(_topTabDragAdorner);
        }

        tab.Opacity = 0.24;
        Cursor = Cursors.SizeAll;

        AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(TopTabDragWindow_PreviewMouseMove), true);
        AddHandler(UIElement.PreviewMouseUpEvent, new MouseButtonEventHandler(TopTabDragWindow_PreviewMouseUp), true);
        CaptureMouse();
        LostMouseCapture += TopTabDragWindow_LostMouseCapture;

        UpdateTopTabDrag(rootPoint);
    }

    private void TopTabDragWindow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isTopTabDragging) return;
        UpdateTopTabDrag(e.GetPosition(RootGrid));
        e.Handled = true;
    }

    private void TopTabDragWindow_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!_isTopTabDragging) return;
        FinishTopTabDrag(commit: true);
        SuppressNextTopTabClick();
        e.Handled = true;
    }

    private void TopTabDragWindow_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (Mouse.Captured == this) return;
        if (_isFinishingTopTabDrag || !_isTopTabDragging) return;
        FinishTopTabDrag(commit: false);
    }

    private void UpdateTopTabDrag(Point rootPoint)
    {
        _topTabDragAdorner?.SetLocation(new Point(
            rootPoint.X - _topTabDragOffset.X,
            rootPoint.Y - _topTabDragOffset.Y));

        if (TryResolveTopTabPreviewTarget(rootPoint, out var target, out var insertAfter)
            && (_lastTopTabDragTargetKey != GetTopTabKey(target) || _lastTopTabDragInsertAfter != insertAfter))
        {
            if (MoveTopTabWithAnimation(target, insertAfter))
            {
                _lastTopTabDragTargetKey = GetTopTabKey(target);
                _lastTopTabDragInsertAfter = insertAfter;
            }
        }
    }

    private bool TryResolveTopTabPreviewTarget(Point rootPoint, out ToggleButton target, out bool insertAfter)
    {
        target = null!;
        insertAfter = false;
        if (_topTabDragSource is null) return false;

        var candidates = TopTabGrid.Children
            .OfType<ToggleButton>()
            .Where(tab => !ReferenceEquals(tab, _topTabDragSource))
            .Select(tab => new
            {
                Tab = tab,
                TopLeft = tab.TranslatePoint(new Point(0, 0), RootGrid)
            })
            .OrderBy(x => x.TopLeft.X)
            .ToList();

        if (candidates.Count == 0) return false;

        foreach (var candidate in candidates)
        {
            var midpoint = candidate.TopLeft.X + candidate.Tab.ActualWidth / 2;
            if (rootPoint.X < midpoint)
            {
                target = candidate.Tab;
                insertAfter = false;
                return true;
            }
        }

        target = candidates[^1].Tab;
        insertAfter = true;
        return true;
    }

    private bool MoveTopTabWithAnimation(ToggleButton target, bool insertAfter)
    {
        if (_topTabDragSource is null || ReferenceEquals(_topTabDragSource, target)) return false;

        var previousBounds = CaptureTopTabBounds();
        var children = TopTabGrid.Children;
        var oldIndex = children.IndexOf(_topTabDragSource);
        var targetIndex = children.IndexOf(target);
        if (oldIndex < 0 || targetIndex < 0) return false;

        var targetIndexAfterRemoval = oldIndex < targetIndex ? targetIndex - 1 : targetIndex;
        var newIndex = insertAfter ? targetIndexAfterRemoval + 1 : targetIndexAfterRemoval;
        if (newIndex == oldIndex) return false;

        children.RemoveAt(oldIndex);
        children.Insert(Math.Clamp(newIndex, 0, children.Count), _topTabDragSource);
        _hasTopTabDragPreview = true;
        UpdateLayout();
        ApplyTopTabPlaceholderOpacity();
        AnimateTopTabsFrom(previousBounds);
        return true;
    }

    private Dictionary<string, Rect> CaptureTopTabBounds()
    {
        return TopTabGrid.Children
            .OfType<ToggleButton>()
            .Where(tab => GetTopTabKey(tab) is not null)
            .ToDictionary(
                tab => GetTopTabKey(tab)!,
                tab =>
                {
                    var topLeft = tab.TranslatePoint(new Point(0, 0), RootGrid);
                    return new Rect(topLeft, new Size(tab.ActualWidth, tab.ActualHeight));
                });
    }

    private void AnimateTopTabsFrom(IReadOnlyDictionary<string, Rect> previousBounds)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);

        foreach (var tab in TopTabGrid.Children.OfType<ToggleButton>())
        {
            var key = GetTopTabKey(tab);
            if (key is null || !previousBounds.TryGetValue(key, out var previous)) continue;

            var currentTopLeft = tab.TranslatePoint(new Point(0, 0), RootGrid);
            var offsetX = previous.Left - currentTopLeft.X;
            var offsetY = previous.Top - currentTopLeft.Y;
            if (Math.Abs(offsetX) < 0.5 && Math.Abs(offsetY) < 0.5) continue;

            var transform = tab.RenderTransform as TranslateTransform;
            if (transform is null)
            {
                transform = new TranslateTransform();
                tab.RenderTransform = transform;
            }

            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.X = offsetX;
            transform.Y = offsetY;

            transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
        }
    }

    private void FinishTopTabDrag(bool commit)
    {
        if (!_isTopTabDragging) return;

        _isFinishingTopTabDrag = true;
        RemoveHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(TopTabDragWindow_PreviewMouseMove));
        RemoveHandler(UIElement.PreviewMouseUpEvent, new MouseButtonEventHandler(TopTabDragWindow_PreviewMouseUp));
        LostMouseCapture -= TopTabDragWindow_LostMouseCapture;

        if (Mouse.Captured == this)
        {
            ReleaseMouseCapture();
        }

        if (commit)
        {
            SaveTopTabOrder();
        }
        else if (_hasTopTabDragPreview)
        {
            RestoreTopTabOrderWithAnimation(_topTabDragOriginalOrder);
        }

        RestoreTopTabPlaceholderOpacity();

        if (_topTabDragAdorner is not null)
        {
            _topTabDragAdornerLayer?.Remove(_topTabDragAdorner);
        }

        Cursor = null;
        _topTabDragStartPoint = null;
        _topTabDragSource = null;
        _topTabDragOriginalOrder = null;
        _topTabDragAdorner = null;
        _topTabDragAdornerLayer = null;
        _isTopTabDragging = false;
        _lastTopTabDragTargetKey = null;
        _lastTopTabDragInsertAfter = false;
        _hasTopTabDragPreview = false;
        _isFinishingTopTabDrag = false;
    }

    private void ApplyTopTabPlaceholderOpacity()
    {
        if (_topTabDragSource is not null)
        {
            _topTabDragSource.Opacity = 0.24;
        }
    }

    private void RestoreTopTabPlaceholderOpacity()
    {
        if (_topTabDragSource is not null)
        {
            _topTabDragSource.Opacity = _topTabDragSourceOpacity;
        }
    }

    private void SaveTopTabOrder()
    {
        _settings.TopTabOrder = string.Join(",", SnapshotTopTabOrder());
    }

    private IReadOnlyList<string> SnapshotTopTabOrder()
    {
        return TopTabGrid.Children
            .OfType<ToggleButton>()
            .Select(GetTopTabKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .ToList();
    }

    private void RestoreTopTabOrderWithAnimation(IReadOnlyList<string>? order)
    {
        if (order is null || order.Count == 0) return;

        var previousBounds = CaptureTopTabBounds();
        if (!ApplyTopTabOrder(order))
        {
            return;
        }

        UpdateLayout();
        ApplyTopTabPlaceholderOpacity();
        AnimateTopTabsFrom(previousBounds);
    }

    private void ApplySavedTopTabOrder()
    {
        var order = (_settings.TopTabOrder ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (order.Length == 0) return;

        ApplyTopTabOrder(order);
    }

    private bool ApplyTopTabOrder(IReadOnlyList<string> order)
    {
        var tabs = TopTabGrid.Children
            .OfType<ToggleButton>()
            .Where(tab => GetTopTabKey(tab) is not null)
            .ToDictionary(tab => GetTopTabKey(tab)!, tab => tab);

        var orderedTabs = order
            .Where(tabs.ContainsKey)
            .Select(key => tabs[key])
            .Concat(tabs.Where(pair => !order.Contains(pair.Key)).Select(pair => pair.Value))
            .ToList();

        if (orderedTabs.Count != tabs.Count) return false;
        if (TopTabGrid.Children.OfType<ToggleButton>().SequenceEqual(orderedTabs)) return false;

        TopTabGrid.Children.Clear();
        foreach (var tab in orderedTabs)
        {
            TopTabGrid.Children.Add(tab);
        }

        return true;
    }

    private static string? GetTopTabKey(ToggleButton tab) => tab.Tag as string;

    private static ToggleButton? FindTopTabFromSource(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ToggleButton tab && GetTopTabKey(tab) is not null)
            {
                return tab;
            }

            d = VisualTreeHelper.GetParent(d);
        }

        return null;
    }

    private void TaskRow_LeftClick(object sender, MouseButtonEventArgs e)
    {
        if (_suppressTaskRowClick)
        {
            _suppressTaskRowClick = false;
            e.Handled = true;
            return;
        }

        if (e.ClickCount > 1)
        {
            e.Handled = true;
            return;
        }

        if (IsInsideElementNamed(e.OriginalSource as DependencyObject, "SubRow")) return;
        if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;
        if (sender is not FrameworkElement fe) return;
        if (fe.Tag is not TaskItemViewModel vm) return;

        _vm.ToggleExpandedCommand.Execute(vm);
        e.Handled = true;
    }

    private void TaskRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _taskDragStartPoint = null;
        _taskDragSource = null;

        if (!_vm.CanReorderTasks) return;
        if (IsInsideElementNamed(e.OriginalSource as DependencyObject, "SubRow")) return;
        if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;
        if (sender is not FrameworkElement fe || fe.Tag is not TaskItemViewModel vm) return;

        _taskDragStartPoint = e.GetPosition(this);
        _taskDragSource = vm;
    }

    private void TaskRow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isTaskDragging)
        {
            UpdateTaskDrag(e.GetPosition(RootGrid));
            e.Handled = true;
            return;
        }

        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (_taskDragStartPoint is not { } start || _taskDragSource is null) return;
        if (sender is not FrameworkElement row) return;

        var current = e.GetPosition(this);
        var movedEnough =
            Math.Abs(current.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(current.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;
        if (!movedEnough) return;

        BeginTaskDrag(row, e.GetPosition(RootGrid));
        e.Handled = true;
    }

    private void BeginTaskDrag(FrameworkElement row, Point rootPoint)
    {
        if (_taskDragSource is null || _isTaskDragging) return;

        _isTaskDragging = true;
        _taskDragOriginalOrder = _vm.SnapshotVisibleTaskOrder();
        _hasTaskDragPreview = false;
        _lastTaskDragTargetId = null;
        _lastTaskDragInsertAfter = false;

        var dragElement = ResolveTaskDragElement(row);
        _taskDragRow = dragElement;
        _taskDragRowOpacity = dragElement.Opacity;
        var rowTopLeft = dragElement.TranslatePoint(new Point(0, 0), RootGrid);
        _taskDragOffset = new Point(rootPoint.X - rowTopLeft.X, rootPoint.Y - rowTopLeft.Y);

        var snapshot = RenderElementSnapshot(dragElement);
        _taskDragAdornerLayer = AdornerLayer.GetAdornerLayer(RootGrid);
        if (snapshot is not null && _taskDragAdornerLayer is not null)
        {
            _taskDragAdorner = new TaskDragAdorner(RootGrid, snapshot, dragElement.ActualWidth, dragElement.ActualHeight);
            _taskDragAdornerLayer.Add(_taskDragAdorner);
        }

        dragElement.Opacity = 0.18;
        Cursor = Cursors.SizeAll;

        PreviewMouseMove += TaskDragWindow_PreviewMouseMove;
        PreviewMouseLeftButtonUp += TaskDragWindow_PreviewMouseLeftButtonUp;
        LostMouseCapture += TaskDragWindow_LostMouseCapture;
        CaptureMouse();

        UpdateTaskDrag(rootPoint);
    }

    private FrameworkElement ResolveTaskDragElement(FrameworkElement row)
    {
        return FindElementNamed(row, "TaskCard") ?? row;
    }

    private static ImageSource? RenderElementSnapshot(FrameworkElement row)
    {
        if (row.ActualWidth <= 0 || row.ActualHeight <= 0) return null;

        var dpi = VisualTreeHelper.GetDpi(row);
        var pixelWidth = Math.Max(1, (int)Math.Ceiling(row.ActualWidth * dpi.DpiScaleX));
        var pixelHeight = Math.Max(1, (int)Math.Ceiling(row.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(
            pixelWidth,
            pixelHeight,
            96 * dpi.DpiScaleX,
            96 * dpi.DpiScaleY,
            PixelFormats.Pbgra32);
        bitmap.Render(row);
        bitmap.Freeze();
        return bitmap;
    }

    private void TaskDragWindow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isTaskDragging) return;
        UpdateTaskDrag(e.GetPosition(RootGrid));
        e.Handled = true;
    }

    private void TaskDragWindow_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isTaskDragging) return;
        FinishTaskDrag(commit: true);
        _suppressTaskRowClick = true;
        e.Handled = true;
    }

    private void TaskDragWindow_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isFinishingTaskDrag || !_isTaskDragging) return;
        FinishTaskDrag(commit: false);
    }

    private void UpdateTaskDrag(Point rootPoint)
    {
        _taskDragAdorner?.SetLocation(new Point(
            rootPoint.X - _taskDragOffset.X,
            rootPoint.Y - _taskDragOffset.Y));

        if (TryResolveTaskPreviewTarget(rootPoint, out var target, out var insertAfter)
            && _taskDragSource is { } source
            && (_lastTaskDragTargetId != target.Model.Id || _lastTaskDragInsertAfter != insertAfter))
        {
            if (MoveTaskWithAnimation(source, target, insertAfter, persist: false))
            {
                _hasTaskDragPreview = true;
                _lastTaskDragTargetId = target.Model.Id;
                _lastTaskDragInsertAfter = insertAfter;
            }
        }
    }

    private bool TryResolveTaskPreviewTarget(Point rootPoint, out TaskItemViewModel target, out bool insertAfter)
    {
        target = null!;
        insertAfter = false;

        if (_taskDragSource is null) return false;

        var candidates = FindTaskCards()
            .Select(card => new
            {
                Card = card,
                Vm = (TaskItemViewModel)card.Tag,
                TopLeft = card.TranslatePoint(new Point(0, 0), RootGrid)
            })
            .Where(x => !ReferenceEquals(x.Vm, _taskDragSource) && _vm.CanMoveTask(_taskDragSource, x.Vm))
            .OrderBy(x => x.TopLeft.Y)
            .ToList();

        if (candidates.Count == 0) return false;

        foreach (var candidate in candidates)
        {
            var midpoint = candidate.TopLeft.Y + candidate.Card.ActualHeight / 2;
            if (rootPoint.Y < midpoint)
            {
                target = candidate.Vm;
                insertAfter = false;
                return true;
            }
        }

        target = candidates[^1].Vm;
        insertAfter = true;
        return true;
    }

    private void FinishTaskDrag(bool commit)
    {
        if (!_isTaskDragging) return;

        _isFinishingTaskDrag = true;
        PreviewMouseMove -= TaskDragWindow_PreviewMouseMove;
        PreviewMouseLeftButtonUp -= TaskDragWindow_PreviewMouseLeftButtonUp;
        LostMouseCapture -= TaskDragWindow_LostMouseCapture;

        if (Mouse.Captured == this)
        {
            ReleaseMouseCapture();
        }

        if (commit)
        {
            _vm.PersistOrderForTask(_taskDragSource);
        }
        else if (_hasTaskDragPreview)
        {
            RestoreTaskOrderWithAnimation(_taskDragOriginalOrder);
        }

        RestoreTaskDragRowOpacity();

        if (_taskDragAdorner is not null)
        {
            _taskDragAdornerLayer?.Remove(_taskDragAdorner);
        }

        Cursor = null;
        _taskDragStartPoint = null;
        _taskDragSource = null;
        _taskDragOriginalOrder = null;
        _taskDragRow = null;
        _taskDragAdorner = null;
        _taskDragAdornerLayer = null;
        _isTaskDragging = false;
        _hasTaskDragPreview = false;
        _lastTaskDragTargetId = null;
        _lastTaskDragInsertAfter = false;
        _isFinishingTaskDrag = false;
    }

    private void ApplyTaskDragPlaceholderOpacity()
    {
        if (_taskDragSource is null) return;

        foreach (var row in FindTaskCards()
                     .Where(card => card.Tag is TaskItemViewModel vm
                                    && vm.Model.Id == _taskDragSource.Model.Id))
        {
            row.Opacity = 0.18;
        }
    }

    private void RestoreTaskDragRowOpacity()
    {
        if (_taskDragSource is not null)
        {
            foreach (var row in FindTaskCards()
                         .Where(card => card.Tag is TaskItemViewModel vm
                                        && vm.Model.Id == _taskDragSource.Model.Id))
            {
                row.Opacity = _taskDragRowOpacity;
            }
        }

        if (_taskDragRow is not null)
        {
            _taskDragRow.Opacity = _taskDragRowOpacity;
        }
    }

    private bool MoveTaskWithAnimation(TaskItemViewModel source, TaskItemViewModel? target, bool insertAfter, bool persist)
    {
        var previousBounds = CaptureTaskRowBounds();
        var moved = persist
            ? _vm.MoveTask(source, target, insertAfter)
            : _vm.PreviewMoveTask(source, target, insertAfter);
        if (!moved)
        {
            return false;
        }

        UpdateLayout();
        if (_isTaskDragging)
        {
            ApplyTaskDragPlaceholderOpacity();
        }
        AnimateTaskRowsFrom(previousBounds);
        return true;
    }

    private void RestoreTaskOrderWithAnimation(IReadOnlyList<long>? originalOrder)
    {
        var previousBounds = CaptureTaskRowBounds();
        if (!_vm.RestoreVisibleTaskOrder(originalOrder))
        {
            return;
        }

        UpdateLayout();
        if (_isTaskDragging)
        {
            ApplyTaskDragPlaceholderOpacity();
        }
        AnimateTaskRowsFrom(previousBounds);
    }

    private Dictionary<long, Rect> CaptureTaskRowBounds()
    {
        var bounds = new Dictionary<long, Rect>();
        foreach (var row in FindTaskCards())
        {
            if (row.Tag is not TaskItemViewModel vm) continue;
            var topLeft = row.TranslatePoint(new Point(0, 0), MainScroller);
            bounds[vm.Model.Id] = new Rect(topLeft, new Size(row.ActualWidth, row.ActualHeight));
        }

        return bounds;
    }

    private IEnumerable<FrameworkElement> FindTaskCards()
    {
        return FindDescendants<StackPanel>(MainScroller)
            .Where(panel => panel.Name == "TaskCard" && panel.Tag is TaskItemViewModel)
            .Cast<FrameworkElement>();
    }

    private void AnimateTaskRowsFrom(IReadOnlyDictionary<long, Rect> previousBounds)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);

        foreach (var row in FindTaskCards())
        {
            if (row.Tag is not TaskItemViewModel vm) continue;
            if (!previousBounds.TryGetValue(vm.Model.Id, out var previous)) continue;

            var currentTopLeft = row.TranslatePoint(new Point(0, 0), MainScroller);
            var offsetX = previous.Left - currentTopLeft.X;
            var offsetY = previous.Top - currentTopLeft.Y;
            if (Math.Abs(offsetX) < 0.5 && Math.Abs(offsetY) < 0.5) continue;

            var transform = row.RenderTransform as TranslateTransform;
            if (transform is null)
            {
                transform = new TranslateTransform();
                row.RenderTransform = transform;
            }

            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.X = offsetX;
            transform.Y = offsetY;

            var xAnimation = new DoubleAnimation(0, duration)
            {
                EasingFunction = ease
            };
            var yAnimation = new DoubleAnimation(0, duration)
            {
                EasingFunction = ease
            };

            transform.BeginAnimation(TranslateTransform.XProperty, xAnimation);
            transform.BeginAnimation(TranslateTransform.YProperty, yAnimation);
        }
    }

    private void SubtaskRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _subtaskDragStartPoint = null;
        _subtaskDragSource = null;
        _subtaskDragStartedSinceMouseDown = false;

        var originalSource = e.OriginalSource as DependencyObject;
        if (IsInteractiveElement(originalSource) && !IsInsideCheckBox(originalSource)) return;
        if (sender is not FrameworkElement row || row.Tag is not TaskItemViewModel vm) return;

        _subtaskDragStartPoint = e.GetPosition(this);
        _subtaskDragSource = vm;
    }

    private void SubtaskRow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_isSubtaskDragging)
        {
            UpdateSubtaskDrag(e.GetPosition(RootGrid));
            e.Handled = true;
            return;
        }

        if (!_vm.CanReorderTasks) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (_subtaskDragStartPoint is not { } start || _subtaskDragSource is null) return;
        if (sender is not FrameworkElement row) return;

        var current = e.GetPosition(this);
        var movedEnough =
            Math.Abs(current.X - start.X) >= SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(current.Y - start.Y) >= SystemParameters.MinimumVerticalDragDistance;
        if (!movedEnough) return;

        BeginSubtaskDrag(row, e.GetPosition(RootGrid));
        e.Handled = true;
    }

    private void SubtaskRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isSubtaskDragging || _isFinishingSubtaskDrag || _subtaskDragStartedSinceMouseDown)
        {
            _subtaskDragStartedSinceMouseDown = false;
            e.Handled = true;
            return;
        }

        if (_suppressSubtaskClick || DateTime.UtcNow < _suppressSubtaskClickUntilUtc)
        {
            _suppressSubtaskClick = false;
            e.Handled = true;
            return;
        }

        if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;
        if (sender is not FrameworkElement row || row.Tag is not TaskItemViewModel vm) return;

        BeginInlineEdit(vm);
        e.Handled = true;
    }

    private void BeginSubtaskDrag(FrameworkElement row, Point rootPoint)
    {
        if (_subtaskDragSource is null || _isSubtaskDragging || _isTaskDragging) return;

        _isSubtaskDragging = true;
        _subtaskDragStartedSinceMouseDown = true;
        _subtaskDragOriginalOrder = _vm.SnapshotVisibleSubtaskOrder(_subtaskDragSource);
        _hasSubtaskDragPreview = false;
        _lastSubtaskDragTargetId = null;
        _lastSubtaskDragInsertAfter = false;

        _subtaskDragRow = row;
        _subtaskDragRowOpacity = row.Opacity;
        var rowTopLeft = row.TranslatePoint(new Point(0, 0), RootGrid);
        _subtaskDragOffset = new Point(rootPoint.X - rowTopLeft.X, rootPoint.Y - rowTopLeft.Y);

        var snapshot = RenderElementSnapshot(row);
        _subtaskDragAdornerLayer = AdornerLayer.GetAdornerLayer(RootGrid);
        if (snapshot is not null && _subtaskDragAdornerLayer is not null)
        {
            _subtaskDragAdorner = new TaskDragAdorner(RootGrid, snapshot, row.ActualWidth, row.ActualHeight);
            _subtaskDragAdornerLayer.Add(_subtaskDragAdorner);
        }

        row.Opacity = 0.18;
        Cursor = Cursors.SizeAll;

        AddHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(SubtaskDragWindow_PreviewMouseMove), true);
        AddHandler(UIElement.PreviewMouseUpEvent, new MouseButtonEventHandler(SubtaskDragWindow_PreviewMouseUp), true);
        CaptureMouse();
        LostMouseCapture += SubtaskDragWindow_LostMouseCapture;

        UpdateSubtaskDrag(rootPoint);
    }

    private void SubtaskDragWindow_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSubtaskDragging) return;
        UpdateSubtaskDrag(e.GetPosition(RootGrid));
        e.Handled = true;
    }

    private void SubtaskDragWindow_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (!_isSubtaskDragging) return;

        var dragged = _subtaskDragSource;
        var restoreOpacity = _subtaskDragRowOpacity;
        FinishSubtaskDrag(commit: true);
        SuppressNextSubtaskClick(dragged, restoreOpacity);
        e.Handled = true;
    }

    private void SubtaskDragWindow_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (Mouse.Captured == this) return;
        if (_isFinishingSubtaskDrag || !_isSubtaskDragging) return;
        FinishSubtaskDrag(commit: false);
    }

    private void UpdateSubtaskDrag(Point rootPoint)
    {
        _subtaskDragAdorner?.SetLocation(new Point(
            rootPoint.X - _subtaskDragOffset.X,
            rootPoint.Y - _subtaskDragOffset.Y));

        if (TryResolveSubtaskPreviewTarget(rootPoint, out var target, out var insertAfter)
            && _subtaskDragSource is { } source
            && (_lastSubtaskDragTargetId != target.Model.Id || _lastSubtaskDragInsertAfter != insertAfter))
        {
            if (MoveSubtaskWithAnimation(source, target, insertAfter))
            {
                _hasSubtaskDragPreview = true;
                _lastSubtaskDragTargetId = target.Model.Id;
                _lastSubtaskDragInsertAfter = insertAfter;
            }
        }
    }

    private bool TryResolveSubtaskPreviewTarget(Point rootPoint, out TaskItemViewModel target, out bool insertAfter)
    {
        target = null!;
        insertAfter = false;
        if (_subtaskDragSource is null) return false;

        var candidates = FindDescendants<Border>(MainScroller)
            .Where(b => b.Name == "SubRow" && b.Tag is TaskItemViewModel)
            .Select(row => new
            {
                Row = row,
                Vm = (TaskItemViewModel)row.Tag,
                TopLeft = row.TranslatePoint(new Point(0, 0), RootGrid)
            })
            .Where(x => !ReferenceEquals(x.Vm, _subtaskDragSource) && _vm.CanMoveSubtask(_subtaskDragSource, x.Vm))
            .OrderBy(x => x.TopLeft.Y)
            .ToList();

        if (candidates.Count == 0) return false;

        foreach (var candidate in candidates)
        {
            var midpoint = candidate.TopLeft.Y + candidate.Row.ActualHeight / 2;
            if (rootPoint.Y < midpoint)
            {
                target = candidate.Vm;
                insertAfter = false;
                return true;
            }
        }

        target = candidates[^1].Vm;
        insertAfter = true;
        return true;
    }

    private void FinishSubtaskDrag(bool commit)
    {
        if (!_isSubtaskDragging) return;

        _isFinishingSubtaskDrag = true;
        RemoveHandler(UIElement.PreviewMouseMoveEvent, new MouseEventHandler(SubtaskDragWindow_PreviewMouseMove));
        RemoveHandler(UIElement.PreviewMouseUpEvent, new MouseButtonEventHandler(SubtaskDragWindow_PreviewMouseUp));
        LostMouseCapture -= SubtaskDragWindow_LostMouseCapture;

        if (Mouse.Captured == this)
        {
            ReleaseMouseCapture();
        }

        if (commit)
        {
            _vm.PersistOrderForSubtask(_subtaskDragSource);
        }
        else if (_hasSubtaskDragPreview)
        {
            RestoreSubtaskOrderWithAnimation(_subtaskDragOriginalOrder);
        }

        RestoreSubtaskDragRowOpacity();

        if (_subtaskDragAdorner is not null)
        {
            _subtaskDragAdornerLayer?.Remove(_subtaskDragAdorner);
        }

        Cursor = null;
        _subtaskDragStartPoint = null;
        _subtaskDragSource = null;
        _subtaskDragOriginalOrder = null;
        _subtaskDragRow = null;
        _subtaskDragAdorner = null;
        _subtaskDragAdornerLayer = null;
        _isSubtaskDragging = false;
        _hasSubtaskDragPreview = false;
        _lastSubtaskDragTargetId = null;
        _lastSubtaskDragInsertAfter = false;
        _isFinishingSubtaskDrag = false;
    }

    private void ApplySubtaskDragPlaceholderOpacity()
    {
        if (_subtaskDragSource is null) return;

        foreach (var row in FindDescendants<Border>(MainScroller)
                     .Where(b => b.Name == "SubRow" && b.Tag is TaskItemViewModel vm
                                 && vm.Model.Id == _subtaskDragSource.Model.Id))
        {
            row.Opacity = 0.18;
        }
    }

    private void RestoreSubtaskDragRowOpacity()
    {
        if (_subtaskDragSource is not null)
        {
            foreach (var row in FindDescendants<Border>(MainScroller)
                         .Where(b => b.Name == "SubRow" && b.Tag is TaskItemViewModel vm
                                     && vm.Model.Id == _subtaskDragSource.Model.Id))
            {
                row.Opacity = _subtaskDragRowOpacity;
            }
        }

        if (_subtaskDragRow is not null)
        {
            _subtaskDragRow.Opacity = _subtaskDragRowOpacity;
        }
    }

    private bool MoveSubtaskWithAnimation(TaskItemViewModel source, TaskItemViewModel target, bool insertAfter)
    {
        var previousBounds = CaptureSubtaskRowBounds();
        if (!_vm.PreviewMoveSubtask(source, target, insertAfter))
        {
            return false;
        }

        UpdateLayout();
        if (_isSubtaskDragging)
        {
            ApplySubtaskDragPlaceholderOpacity();
        }
        AnimateSubtaskRowsFrom(previousBounds);
        return true;
    }

    private void RestoreSubtaskOrderWithAnimation(IReadOnlyList<long>? originalOrder)
    {
        var previousBounds = CaptureSubtaskRowBounds();
        if (!_vm.RestoreVisibleSubtaskOrder(_subtaskDragSource, originalOrder))
        {
            return;
        }

        UpdateLayout();
        if (_isSubtaskDragging)
        {
            ApplySubtaskDragPlaceholderOpacity();
        }
        AnimateSubtaskRowsFrom(previousBounds);
    }

    private Dictionary<long, Rect> CaptureSubtaskRowBounds()
    {
        var bounds = new Dictionary<long, Rect>();
        foreach (var row in FindDescendants<Border>(MainScroller)
                     .Where(b => b.Name == "SubRow" && b.Tag is TaskItemViewModel))
        {
            if (row.Tag is not TaskItemViewModel vm) continue;
            var topLeft = row.TranslatePoint(new Point(0, 0), MainScroller);
            bounds[vm.Model.Id] = new Rect(topLeft, new Size(row.ActualWidth, row.ActualHeight));
        }

        return bounds;
    }

    private void AnimateSubtaskRowsFrom(IReadOnlyDictionary<long, Rect> previousBounds)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);

        foreach (var row in FindDescendants<Border>(MainScroller)
                     .Where(b => b.Name == "SubRow" && b.Tag is TaskItemViewModel))
        {
            if (row.Tag is not TaskItemViewModel vm) continue;
            if (!previousBounds.TryGetValue(vm.Model.Id, out var previous)) continue;

            var currentTopLeft = row.TranslatePoint(new Point(0, 0), MainScroller);
            var offsetX = previous.Left - currentTopLeft.X;
            var offsetY = previous.Top - currentTopLeft.Y;
            if (Math.Abs(offsetX) < 0.5 && Math.Abs(offsetY) < 0.5) continue;

            var transform = row.RenderTransform as TranslateTransform;
            if (transform is null)
            {
                transform = new TranslateTransform();
                row.RenderTransform = transform;
            }

            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.BeginAnimation(TranslateTransform.YProperty, null);
            transform.X = offsetX;
            transform.Y = offsetY;

            transform.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
            transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration) { EasingFunction = ease });
        }
    }

    private void SuppressNextSubtaskClick(TaskItemViewModel? dragged, double restoreOpacity)
    {
        _suppressSubtaskClick = true;
        _suppressCompleteClick = true;
        _suppressSubtaskClickUntilUtc = DateTime.UtcNow.AddMilliseconds(500);
        if (dragged is not null)
        {
            dragged.IsInlineEditing = false;
            RestoreSubtaskVisual(dragged, restoreOpacity);
            _subtaskEditResetTimer?.Stop();
            var resetCount = 0;
            _subtaskEditResetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
            _subtaskEditResetTimer.Tick += (_, _) =>
            {
                dragged.IsInlineEditing = false;
                RestoreSubtaskVisual(dragged, restoreOpacity);
                resetCount++;
                if (resetCount >= 5)
                {
                    _subtaskEditResetTimer?.Stop();
                    _subtaskEditResetTimer = null;
                }
            };
            _subtaskEditResetTimer.Start();
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            _suppressSubtaskClick = false;
            _suppressCompleteClick = false;
            _subtaskDragStartedSinceMouseDown = false;
        }), DispatcherPriority.ContextIdle);
    }

    private void RestoreSubtaskVisual(TaskItemViewModel subtask, double restoreOpacity)
    {
        foreach (var row in FindDescendants<Border>(MainScroller)
                     .Where(b => b.Name == "SubRow" && b.Tag is TaskItemViewModel vm
                                 && vm.Model.Id == subtask.Model.Id))
        {
            row.Opacity = restoreOpacity;
        }
    }

    private void CategoryFilterScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroller) return;

        var offset = scroller.HorizontalOffset - e.Delta;
        scroller.ScrollToHorizontalOffset(Math.Clamp(offset, 0, scroller.ScrollableWidth));
        e.Handled = true;
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu is not null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private async void MenuCheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        var menuItem = sender as MenuItem;
        var previousHeader = menuItem?.Header;
        if (menuItem is not null)
        {
            menuItem.IsEnabled = false;
            menuItem.Header = "正在检查…";
        }

        try
        {
            var result = await new UpdateService().CheckLatestAsync();
            switch (result.Status)
            {
                case UpdateCheckStatus.UpdateAvailable:
                    var message = $"发现新版本 {result.LatestVersion}\n当前版本 {result.CurrentVersion}\n\n是否打开下载页面？";
                    if (MessageBox.Show(this, message, "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes
                        && !string.IsNullOrWhiteSpace(result.ReleaseUrl))
                    {
                        OpenExternalUrl(result.ReleaseUrl);
                    }
                    break;

                case UpdateCheckStatus.UpToDate:
                    MessageBox.Show(this, $"当前已是最新版本。\n当前版本 {result.CurrentVersion}", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;

                case UpdateCheckStatus.NoRelease:
                    MessageBox.Show(this, "还没有可用的 GitHub Release。\n发布第一个 Release 后，这里就能检测到新版本。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;

                case UpdateCheckStatus.Failed:
                    MessageBox.Show(this, $"检查更新失败：\n{result.ErrorMessage}", "检查更新", MessageBoxButton.OK, MessageBoxImage.Warning);
                    break;
            }
        }
        finally
        {
            if (menuItem is not null)
            {
                menuItem.Header = previousHeader;
                menuItem.IsEnabled = true;
            }
        }
    }

    private static void OpenExternalUrl(string url)
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void MenuCategories_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Views.CategoriesDialog(_vm) { Owner = this };
        dlg.ShowDialog();
        _vm.ReloadAll();
    }

    private void ChipDate_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not TaskItemViewModel vm) return;
        _editingTask = vm;
        _quickCalendarMonth = vm.DueAt is { } d
            ? new DateTime(d.Year, d.Month, 1)
            : new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        _yearGridStart = (_quickCalendarMonth.Year / 12) * 12;
        _calendarMode = CalendarPickerMode.Day;
        InitTimePickerSelection();
        OpenQuickFlyout(el, CreateQuickDateTimePanel());
        e.Handled = true;
    }

    private void ChipPriority_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not TaskItemViewModel vm) return;
        _editingTask = vm;
        OpenQuickFlyout(el, BuildPriorityPanel());
        e.Handled = true;
    }

    private void ChipCategory_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not TaskItemViewModel vm) return;
        _editingTask = vm;
        OpenQuickFlyout(el, BuildCategoryPanel());
        e.Handled = true;
    }

    // —— Chip × 删除 —— hover 时按钮可见，点击直接清空对应字段。
    // Button 内部会把 MouseLeftButtonUp 标记为 Handled，所以不会冒泡触发外层 chip 的 picker。
    private void ChipDateRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not TaskItemViewModel vm) return;
        _vm.UpdateTaskDue(vm, null);
        e.Handled = true;
    }

    private void ChipPriorityRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not TaskItemViewModel vm) return;
        _vm.UpdateTaskPriority(vm, "");
        e.Handled = true;
    }

    private void ChipCategoryRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement el || el.DataContext is not TaskItemViewModel vm) return;
        _vm.UpdateTaskCategory(vm, null);
        e.Handled = true;
    }

    private void AddSubtaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is TaskItemViewModel vm)
        {
            vm.DraftSubtaskTitle = string.Empty;
            vm.IsAddingSubtask = true;
        }
    }

    private void TaskTitle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is FrameworkElement el && el.DataContext is TaskItemViewModel vm)
        {
            BeginInlineEdit(vm);
            e.Handled = true;
        }
    }

    private static void BeginInlineEdit(TaskItemViewModel vm)
    {
        vm.EditingTitle = vm.Title;
        vm.IsInlineEditing = true;
    }

    private void SubtaskEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not TaskItemViewModel vm) return;
        if (e.Key == Key.Enter)
        {
            CommitSubtaskEdit(vm, tb.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            // 撤销：直接退出编辑态，不写回
            vm.IsInlineEditing = false;
            e.Handled = true;
        }
    }

    private void SubtaskEdit_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb || tb.DataContext is not TaskItemViewModel vm) return;
        if (!vm.IsInlineEditing) return;
        CommitSubtaskEdit(vm, tb.Text);
    }

    private void CommitSubtaskEdit(TaskItemViewModel vm, string newTitle)
    {
        var trimmed = newTitle?.Trim() ?? string.Empty;
        if (!string.IsNullOrEmpty(trimmed) && trimmed != vm.Title)
        {
            _vm.RenameTask(vm, trimmed);
        }
        vm.IsInlineEditing = false;
    }

    private void SubtaskEdit_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue && sender is TextBox tb)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                tb.Focus();
                tb.SelectAll();
                Keyboard.Focus(tb);
            }), DispatcherPriority.Input);
        }
    }

    private void NewSubtaskBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (tb.DataContext is not TaskItemViewModel parent) return;

        if (e.Key == Key.Enter)
        {
            var title = tb.Text.Trim();
            if (string.IsNullOrEmpty(title))
            {
                parent.IsAddingSubtask = false;
            }
            else
            {
                _vm.AddSubtaskWithTitle(parent, title);
                parent.DraftSubtaskTitle = string.Empty;
                tb.Focus();
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            parent.DraftSubtaskTitle = string.Empty;
            parent.IsAddingSubtask = false;
            e.Handled = true;
        }
    }

    private void NewSubtaskBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (tb.DataContext is not TaskItemViewModel parent) return;

        var title = tb.Text.Trim();
        if (!string.IsNullOrEmpty(title))
        {
            _vm.AddSubtaskWithTitle(parent, title);
        }
        parent.DraftSubtaskTitle = string.Empty;
        parent.IsAddingSubtask = false;
    }

    private void NewSubtaskBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue && sender is TextBox tb)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                tb.Focus();
                Keyboard.Focus(tb);
            }), DispatcherPriority.Input);
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        // 这个窗口默认 ShowInTaskbar=False，最小化后没有任务栏入口就找不回来了。
        // 临时把 ShowInTaskbar 打开，等用户从任务栏点回来时再恢复隐藏。
        ShowInTaskbar = true;
        WindowState = WindowState.Minimized;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        // 拖到屏幕顶端时 Windows 会用 Aero Snap 强制最大化，跟我们自己的"顶部贴边"
        // 撞车 —— 最大化态下 DragMove 不工作、ResizeMode 也失灵。
        // 拦下来：立刻还原 Normal，并替我们自己执行一次顶部贴边。
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            var wa = SystemParameters.WorkArea;
            Top = wa.Top;
            if (Left < wa.Left) Left = wa.Left;
            if (Left + Width > wa.Right) Left = wa.Right - Width;
            _settings.SnapEdge = "top";
            SaveBounds();
            return;
        }
        // 从最小化恢复后把 ShowInTaskbar 关回去，回到纯悬浮窗形态。
        if (WindowState == WindowState.Normal && ShowInTaskbar)
        {
            ShowInTaskbar = false;
        }
    }

    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (_isTopTabDragging && e.Key == Key.Escape)
        {
            FinishTopTabDrag(commit: false);
            e.Handled = true;
            return;
        }

        if (_isSubtaskDragging && e.Key == Key.Escape)
        {
            FinishSubtaskDrag(commit: false);
            e.Handled = true;
            return;
        }

        if (_isTaskDragging && e.Key == Key.Escape)
        {
            FinishTaskDrag(commit: false);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.N && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            NewTaskBox.Focus();
            NewTaskBox.SelectAll();
            e.Handled = true;
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        ClearTopTabPendingDrag();
        FinishTopTabDrag(commit: false);
        FinishSubtaskDrag(commit: false);
        FinishTaskDrag(commit: false);
        SaveBounds();
        _subtaskEditResetTimer?.Stop();
        _autoHideTimer.Stop();
    }

    // --- Win32 ---
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    private static bool IsInteractiveElement(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ButtonBase || d is TextBox || d is ComboBox)
            {
                return true;
            }

            d = VisualTreeHelper.GetParent(d);
        }

        return false;
    }

    private static bool IsInsideElementNamed(DependencyObject? d, string name)
    {
        return FindElementNamed(d, name) is not null;
    }

    private static FrameworkElement? FindElementNamed(DependencyObject? d, string name)
    {
        while (d != null)
        {
            if (d is FrameworkElement fe && fe.Name == name)
            {
                return fe;
            }

            d = VisualTreeHelper.GetParent(d);
        }

        return null;
    }

    private static bool IsInsideCheckBox(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is CheckBox)
            {
                return true;
            }

            d = VisualTreeHelper.GetParent(d);
        }

        return false;
    }

    private static bool IsInside(DependencyObject? d, DependencyObject ancestor)
    {
        while (d != null)
        {
            if (ReferenceEquals(d, ancestor)) return true;
            d = VisualTreeHelper.GetParent(d);
        }

        return false;
    }

    // ===== 顶栏自适应：窗口越窄，逐级折叠次要元素 =====
    // 阈值是按"刚好放得下下一档元素"反推的：
    //   ≥ 500px 全显
    //   < 500px 隐藏 tab 计数（"待办 6" → "待办"）
    //   < 360px 再隐藏搜索按钮（设置 / Min / Close 永远保留）
    private void ApplyResponsiveTopBar()
    {
        if (TodayCountText == null) return; // 尚未 InitializeComponent

        var w = ActualWidth;
        var showCounts = w >= 500;
        var showSearch = w >= 360;

        var countVis = showCounts ? Visibility.Visible : Visibility.Collapsed;
        TodayCountText.Visibility = countVis;
        UpcomingCountText.Visibility = countVis;
        InboxCountText.Visibility = countVis;
        CompletedCountText.Visibility = countVis;

        if (SearchButton != null)
            SearchButton.Visibility = showSearch ? Visibility.Visible : Visibility.Collapsed;
    }

    // ===== 底部分类条溢出处理 =====
    private void HookCategoryOverflowPanel()
    {
        if (_categoryOverflowPanel != null) return;
        if (CategoryFilterStrip == null) return;
        // 等 ItemsControl 把 ItemsHost 物化出来再钩。LayoutUpdated 是稳的兜底。
        EventHandler? handler = null;
        handler = (s, e) =>
        {
            var panel = FindDescendant<OverflowHidePanel>(CategoryFilterStrip);
            if (panel != null)
            {
                CategoryFilterStrip.LayoutUpdated -= handler!;
                _categoryOverflowPanel = panel;
                panel.OverflowChanged += CategoryOverflowPanel_OverflowChanged;
            }
        };
        CategoryFilterStrip.LayoutUpdated += handler;
    }

    private void CategoryOverflowPanel_OverflowChanged(object? sender, OverflowChangedEventArgs e)
    {
        _hiddenCategoryFilters = e.HiddenItems
            .OfType<CategoryFilterViewModel>()
            .ToList();

        if (_hiddenCategoryFilters.Count > 0)
        {
            CategoryMoreButton.Visibility = Visibility.Visible;
        }
        else
        {
            CategoryMoreButton.Visibility = Visibility.Collapsed;
            if (CategoryOverflowPopup != null) CategoryOverflowPopup.IsOpen = false;
        }
    }

    private void CategoryMoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (CategoryOverflowPopup == null) return;
        if (_hiddenCategoryFilters.Count == 0) return;
        CategoryOverflowList.ItemsSource = null;
        CategoryOverflowList.ItemsSource = _hiddenCategoryFilters;
        CategoryOverflowPopup.IsOpen = true;
    }

    private void CategoryOverflowItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is CategoryFilterViewModel vm)
        {
            _vm.SelectCategoryFilterCommand.Execute(vm);
        }
        if (CategoryOverflowPopup != null) CategoryOverflowPopup.IsOpen = false;
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null) return null;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) return t;
            var deeper = FindDescendant<T>(child);
            if (deeper != null) return deeper;
        }
        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root == null) yield break;

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }
}

internal sealed class TaskDragAdorner : Adorner
{
    private readonly ImageSource _image;
    private readonly double _width;
    private readonly double _height;
    private Point _location;

    public TaskDragAdorner(UIElement adornedElement, ImageSource image, double width, double height)
        : base(adornedElement)
    {
        _image = image;
        _width = width;
        _height = height;
        IsHitTestVisible = false;
    }

    public void SetLocation(Point location)
    {
        _location = location;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var rect = new Rect(_location, new Size(_width, _height));
        var shadowRect = new Rect(_location.X, _location.Y + 4, _width, _height);

        drawingContext.PushOpacity(0.16);
        drawingContext.DrawRoundedRectangle(Brushes.Black, null, shadowRect, 12, 12);
        drawingContext.Pop();

        drawingContext.PushOpacity(0.94);
        drawingContext.DrawImage(_image, rect);
        drawingContext.Pop();
    }
}

internal static class TimerExtensions
{
    public static void Restart(this DispatcherTimer t)
    {
        t.Stop();
        t.Start();
    }
}
