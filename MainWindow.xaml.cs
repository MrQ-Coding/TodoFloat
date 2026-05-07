using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
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

    private enum CalendarPickerMode { Day, Month, Year }

    private readonly MainViewModel _vm;
    private readonly SettingsService _settings = App.Settings;
    private readonly DispatcherTimer _saveDebounce;
    private readonly DispatcherTimer _autoHideTimer;
    private bool _isHidden;
    private bool _isAnimating;
    private const double PeekWidth = 5;
    private const double TriggerWidth = 6;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(_settings);
        DataContext = _vm;

        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _saveDebounce.Tick += (_, _) => { _saveDebounce.Stop(); SaveBounds(); };

        _autoHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _autoHideTimer.Tick += AutoHideTick;

        SourceInitialized += MainWindow_SourceInitialized;
        LocationChanged += (_, _) => _saveDebounce.Restart();
        SizeChanged += (_, _) => _saveDebounce.Restart();
        PreviewMouseDown += MainWindow_PreviewMouseDown;
        MouseLeftButtonDown += MainWindow_MouseLeftButtonDown;
        KeyDown += MainWindow_KeyDown;
        Closing += MainWindow_Closing;

        Loaded += (_, _) =>
        {
            RestoreWindowBounds();
            _autoHideTimer.Start();
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
        if (IsInside(e.OriginalSource as DependencyObject, NewTaskRow)) return;

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
        try { DragMove(); } catch { /* drag race */ }
        SnapToEdge();
    }

    private bool IsInDragBar(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is Button) return false;
            if (d is Grid g && g.Name == "DragBar") return true;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }
        return false;
    }

    private void SnapToEdge()
    {
        var wa = SystemParameters.WorkArea;
        const double snapDistance = 24;
        if (Math.Abs(Left + Width - wa.Right) < snapDistance)
        {
            Left = wa.Right - Width;
            _settings.SnapEdge = "right";
        }
        else if (Math.Abs(Left - wa.Left) < snapDistance)
        {
            Left = wa.Left;
            _settings.SnapEdge = "left";
        }
        else
        {
            _settings.SnapEdge = "none";
        }
        SaveBounds();
    }

    private int _awayTicks;
    private const int HideAfterTicks = 3; // ~360ms at 120ms interval

    private void AutoHideTick(object? sender, EventArgs e)
    {
        if (!_vm.AutoHideEnabled || _settings.SnapEdge == "none")
        {
            if (_isHidden) ShowSidebar();
            _awayTicks = 0;
            return;
        }
        if (_isAnimating) return;

        var edge = _settings.SnapEdge;
        var wa = SystemParameters.WorkArea;

        if (!GetCursorPos(out var p)) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var mx = p.X / dpi.DpiScaleX;
        var my = p.Y / dpi.DpiScaleY;

        // Use the snapped (visible) position as reference, not current Left (which is offscreen when hidden)
        double visLeft = edge == "right" ? wa.Right - Width : wa.Left;
        double visRight = visLeft + Width;

        var overVisibleArea = mx >= visLeft - 2 && mx <= visRight + 2 && my >= Top - 2 && my <= Top + Height + 2;
        var keyboardFocus = IsKeyboardFocusWithin || IsActive;

        bool inTriggerZone = edge == "right"
            ? mx >= wa.Right - TriggerWidth && my >= Top && my <= Top + Height
            : mx <= wa.Left + TriggerWidth && my >= Top && my <= Top + Height;

        if (!_isHidden)
        {
            // Don't auto-hide while user is interacting
            if (overVisibleArea || keyboardFocus || inTriggerZone)
            {
                _awayTicks = 0;
            }
            else if (++_awayTicks >= HideAfterTicks)
            {
                HideSidebar(edge);
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
        var target = edge == "right" ? wa.Right - PeekWidth : wa.Left - Width + PeekWidth;
        var anim = new DoubleAnimation
        {
            From = Left,
            To = target,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (_, _) => { _isAnimating = false; _isHidden = true; };
        BeginAnimation(LeftProperty, anim);
    }

    private void ShowSidebar()
    {
        if (!_isHidden && !_isAnimating) return;
        _isAnimating = true;
        var wa = SystemParameters.WorkArea;
        var edge = _settings.SnapEdge;
        var target = edge == "left" ? wa.Left : wa.Right - Width;
        var anim = new DoubleAnimation
        {
            From = Left,
            To = target,
            Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        anim.Completed += (_, _) =>
        {
            _isAnimating = false;
            _isHidden = false;
            BeginAnimation(LeftProperty, null);
            Left = target;
            _awayTicks = 0;
        };
        BeginAnimation(LeftProperty, anim);
    }

    private void NewTaskBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SubmitQuickAddOrFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            _vm.QuickInput = string.Empty;
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void QuickAddButton_Click(object sender, RoutedEventArgs e)
    {
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

        // Reset to today on every open — previous navigation is intentionally discarded
        _quickCalendarMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        _yearGridStart = (DateTime.Today.Year / 12) * 12;
        _calendarMode = CalendarPickerMode.Day;
        OpenQuickAddFlyout(button, CreateQuickCalendarPanel());
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
        panel.Children.Add(CreateFlyoutDivider());
        panel.Children.Add(CreateQuickFlyoutButton(CreateFlyoutRow("\xE711", "清除标记", null), () => ApplyPriority(null)));
        return panel;
    }

    private StackPanel BuildCategoryPanel()
    {
        var panel = CreateFlyoutStack();
        foreach (var category in _vm.Categories)
        {
            var name = category.Name.Trim();
            if (string.IsNullOrWhiteSpace(name)) continue;
            panel.Children.Add(CreateQuickFlyoutButton(CreateTagRow(name, category.Color), () => ApplyCategory(name)));
        }
        return panel;
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

    private StackPanel CreateFlyoutStack() => new() { MinWidth = 156 };

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

    private int? _pendingHour;
    private int? _pendingMinute;
    private Button? _selectedHourButton;
    private Button? _selectedMinuteButton;

    private void QuickTimeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        _pendingHour = null;
        _pendingMinute = null;
        _selectedHourButton = null;
        _selectedMinuteButton = null;
        OpenQuickAddFlyout(button, CreateTimePickerPanel());
        e.Handled = true;
    }

    private StackPanel CreateTimePickerPanel()
    {
        var panel = new StackPanel { MinWidth = 180 };
        panel.Children.Add(new TextBlock
        {
            Text = "选择时间",
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
            hourStack.Children.Add(CreateTimeListCell(h.ToString("00"), value: h, isMinute: false));
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
            minuteStack.Children.Add(CreateTimeListCell(m.ToString("00"), value: m, isMinute: true));
        }
        minuteScroll.Content = minuteStack;
        Grid.SetColumn(minuteScroll, 2);
        grid.Children.Add(minuteScroll);

        panel.Children.Add(grid);
        return panel;
    }

    private Button CreateTimeListCell(string label, int value, bool isMinute)
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
                QuickFlyout.IsOpen = false;
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
            QuickFlyoutContent.Content = CreateQuickCalendarPanel();
        };
        var monthBtn = CreateCalendarTitleButton($"{_quickCalendarMonth.Month}月");
        monthBtn.Click += (_, _) =>
        {
            _calendarMode = CalendarPickerMode.Month;
            QuickFlyoutContent.Content = CreateQuickCalendarPanel();
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
            QuickFlyoutContent.Content = CreateQuickCalendarPanel();
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
            QuickFlyoutContent.Content = CreateQuickCalendarPanel();
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
            QuickFlyoutContent.Content = CreateQuickCalendarPanel();
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
                QuickFlyoutContent.Content = CreateQuickCalendarPanel();
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
                QuickFlyoutContent.Content = CreateQuickCalendarPanel();
            };
            grid.Children.Add(btn);
        }
        return grid;
    }

    private void ShiftQuickCalendarYear(int years)
    {
        var target = _quickCalendarMonth.AddYears(years);
        _quickCalendarMonth = new DateTime(target.Year, _quickCalendarMonth.Month, 1);
        QuickFlyoutContent.Content = CreateQuickCalendarPanel();
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
        var button = new Button
        {
            Content = date.Day.ToString(),
            Style = (Style)FindResource("QuickCalendarDayButton"),
            Opacity = isCurrentMonth ? 1.0 : 0.45,
            Foreground = isCurrentMonth
                ? (Brush)FindResource("TextBrush2")
                : (Brush)FindResource("MutedBrush")
        };

        if (isToday)
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
            QuickFlyout.IsOpen = false;
        };
        return button;
    }

    private void ShiftQuickCalendarMonth(int months)
    {
        var target = _quickCalendarMonth.AddMonths(months);
        _quickCalendarMonth = new DateTime(target.Year, target.Month, 1);
        QuickFlyoutContent.Content = CreateQuickCalendarPanel();
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
            Text = $"#{name}",
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
        if (SearchPanel.Visibility == Visibility.Visible)
        {
            SearchPanel.Visibility = Visibility.Collapsed;
            _vm.SearchText = string.Empty;
        }
        else
        {
            SearchPanel.Visibility = Visibility.Visible;
            SearchBox.Focus();
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        _vm.SearchText = string.Empty;
        SearchBox.Focus();
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _vm.SearchText = string.Empty;
            SearchPanel.Visibility = Visibility.Collapsed;
            e.Handled = true;
        }
    }

    private void CompleteCheckbox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox cb && cb.DataContext is TaskItemViewModel vm)
        {
            _vm.ToggleCompleteCommand.Execute(vm);
        }
    }

    private void TaskRow_LeftClick(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;
        if (sender is not FrameworkElement fe) return;
        if (fe.Tag is not TaskItemViewModel vm) return;

        _vm.ToggleExpandedCommand.Execute(vm);
        e.Handled = true;
    }

    private void CategoryFilterScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scroller) return;

        var offset = scroller.HorizontalOffset - e.Delta;
        scroller.ScrollToHorizontalOffset(Math.Clamp(offset, 0, scroller.ScrollableWidth));
        e.Handled = true;
    }

    private void TaskRow_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.Tag is not TaskItemViewModel vm) return;
        var menu = new ContextMenu();

        AddMenuItem(menu, "✎ 编辑", () => _vm.EditCommand.Execute(vm));
        AddMenuItem(menu, "+ 子任务", () => _vm.AddSubtaskCommand.Execute(vm));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "● 高优先级", () => _vm.SetPriorityCommand.Execute((vm, Models.TaskPriority.High)));
        AddMenuItem(menu, "● 中优先级", () => _vm.SetPriorityCommand.Execute((vm, Models.TaskPriority.Medium)));
        AddMenuItem(menu, "● 低优先级", () => _vm.SetPriorityCommand.Execute((vm, Models.TaskPriority.Low)));
        AddMenuItem(menu, "○ 无优先级", () => _vm.SetPriorityCommand.Execute((vm, Models.TaskPriority.None)));
        menu.Items.Add(new Separator());
        AddMenuItem(menu, "🗑 删除", () => _vm.DeleteCommand.Execute(vm));

        menu.PlacementTarget = fe;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private static void AddMenuItem(ContextMenu m, string header, Action onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => onClick();
        m.Items.Add(mi);
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
        OpenQuickFlyout(el, CreateQuickCalendarPanel());
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

    private void AddSubtaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.DataContext is TaskItemViewModel vm)
        {
            vm.DraftSubtaskTitle = string.Empty;
            vm.IsAddingSubtask = true;
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

    private void BtnPin_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        if (sender is Button b) b.Opacity = Topmost ? 1.0 : 0.4;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.N && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            NewTaskBox.Focus();
            NewTaskBox.SelectAll();
            e.Handled = true;
        }
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveBounds();
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

    private static bool IsInside(DependencyObject? d, DependencyObject ancestor)
    {
        while (d != null)
        {
            if (ReferenceEquals(d, ancestor)) return true;
            d = VisualTreeHelper.GetParent(d);
        }

        return false;
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
