using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TodoFloat.Data;
using TodoFloat.Models;
using TodoFloat.Services;

namespace TodoFloat.ViewModels;

public enum TodoView
{
    Today,
    Upcoming,
    Inbox
}

public partial class MainViewModel : ObservableObject
{
    private static readonly Regex TagRegex = new(@"#([\p{L}\p{N}_-]+)", RegexOptions.Compiled);
    private static readonly Regex TimeRegex = new(@"\b(?<h>[01]?\d|2[0-3]):(?<m>[0-5]\d)\b", RegexOptions.Compiled);
    private static readonly Regex TodayRegex = new(@"\btoday\b|今天", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TomorrowRegex = new(@"\btomorrow\b|明天", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ChineseDateRegex = new(@"(?<!\S)(?:(?<y>\d{4})年)?(?<m>\d{1,2})月(?<d>\d{1,2})日(?!\S)", RegexOptions.Compiled);
    private static readonly Regex PriorityTokenRegex = new(@"(?<!\S)(?<p>!!|!高|!中|!低|!)(?!\S)", RegexOptions.Compiled);

    // 提取式 Regex：要求 token 后面是空白或字符串末尾，避免误吞 "明天的会议" 这类文本
    private static readonly Regex LiveTagRegex = new(@"(?<!\S)#(?<n>[\p{L}\p{N}_-]+)(?=\s|$)", RegexOptions.Compiled);
    private static readonly Regex LivePriorityRegex = new(@"(?<!\S)(?<p>!!|!高|!中|!低|!)(?=\s|$)", RegexOptions.Compiled);
    private static readonly Regex LiveTodayRegex = new(@"(?<!\S)(今天|today)(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LiveTomorrowRegex = new(@"(?<!\S)(明天|tomorrow)(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex LiveChineseDateRegex = new(@"(?<!\S)(?:(?<y>\d{4})年)?(?<m>\d{1,2})月(?<d>\d{1,2})日(?=\s|$)", RegexOptions.Compiled);
    private static readonly Regex LiveTimeRegex = new(@"(?<!\S)(?<h>[01]?\d|2[0-3]):(?<min>[0-5]\d)(?=\s|$)", RegexOptions.Compiled);
    private static readonly string[] CategoryPalette =
    [
        "#E8915E",
        "#7B98E8",
        "#5FB58A",
        "#C57BB8",
        "#D9A235",
        "#6CA6A6"
    ];

    private readonly TaskRepository _tasks = new();
    private readonly CategoryRepository _categories = new();
    private readonly SettingsService _settings;

    public ObservableCollection<TaskItemViewModel> Tasks { get; } = new();
    public ObservableCollection<TaskGroupViewModel> TaskGroups { get; } = new();
    public ObservableCollection<Category> Categories { get; } = new();
    public ObservableCollection<CategoryFilterViewModel> CategoryFilters { get; } = new();

    [ObservableProperty] private string _quickInput = string.Empty;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private long? _filterCategoryId;
    [ObservableProperty] private bool _showCompleted;
    [ObservableProperty] private int _activeCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _completedCount;
    [ObservableProperty] private int _todayCount;
    [ObservableProperty] private int _upcomingCount;
    [ObservableProperty] private int _inboxCount;
    [ObservableProperty] private bool _autostartEnabled;
    [ObservableProperty] private bool _autoHideEnabled;
    [ObservableProperty] private TodoView _currentView = TodoView.Today;

    public MainViewModel(SettingsService settings)
    {
        _settings = settings;
        _showCompleted = settings.ShowCompleted;
        _autoHideEnabled = settings.AutoHide;
        _autostartEnabled = AutostartService.IsEnabled;
        ReloadAll();
    }

    public bool IsTodayView
    {
        get => CurrentView == TodoView.Today;
        set { if (value) CurrentView = TodoView.Today; }
    }

    public bool IsUpcomingView
    {
        get => CurrentView == TodoView.Upcoming;
        set { if (value) CurrentView = TodoView.Upcoming; }
    }

    public bool IsInboxView
    {
        get => CurrentView == TodoView.Inbox;
        set { if (value) CurrentView = TodoView.Inbox; }
    }

    public string ViewTitle => CurrentView switch
    {
        TodoView.Today => "待办",
        TodoView.Upcoming => "接下来",
        TodoView.Inbox => ShowCompleted ? "全部任务" : "全部待办",
        _ => "任务"
    };

    public string EmptyTitle => CurrentView switch
    {
        TodoView.Today => "暂无待办",
        TodoView.Upcoming => "后续没有安排",
        TodoView.Inbox => "收件箱已清空",
        _ => "没有任务"
    };

    public bool HasTaskGroups => TaskGroups.Count > 0;

    public double CompletionPercent => TotalCount == 0 ? 0 : CompletedCount * 100.0 / TotalCount;

    // —— Quick add chip state（实际 chips，与文本解耦）——
    private bool _suppressExtract;
    private DateTime? _chipDue;          // 完整日期+时间
    private bool _chipDueHasDate;        // 是否已设置日期（否则只设置了时间，base 用 today）
    private string? _chipPriorityRaw;    // 原始 token: "!!"/"!"/"!低"/"!高"/"!中"
    private string? _chipCategoryName;

    public string? QuickChipDueLabel => _chipDue is { } d ? FormatDueLabel(d) : null;
    public string? QuickChipPriorityKey => _chipPriorityRaw switch
    {
        "!!" or "!高" => "high",
        "!" or "!中" => "med",
        "!低" => "low",
        _ => null
    };
    public string? QuickChipPriorityLabel => _chipPriorityRaw switch
    {
        "!!" or "!高" => "高",
        "!" or "!中" => "中",
        "!低" => "低",
        _ => null
    };
    public Brush? QuickChipPriorityBrush => _chipPriorityRaw switch
    {
        "!!" or "!高" => BrushFromHexStatic("#D85A45"),
        "!" or "!中" => BrushFromHexStatic("#E8915E"),
        "!低" => BrushFromHexStatic("#A8A8B2"),
        _ => null
    };
    public string? QuickChipCategoryName => _chipCategoryName;
    public Brush? QuickChipCategoryBrush
    {
        get
        {
            if (string.IsNullOrEmpty(_chipCategoryName)) return null;
            var existing = Categories.FirstOrDefault(c =>
                string.Equals(LocalizeCategoryName(c.Name), _chipCategoryName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.Name, _chipCategoryName, StringComparison.OrdinalIgnoreCase));
            return BrushFromHexStatic(existing?.Color ?? "#7E57C2");
        }
    }
    public bool HasQuickChips => _chipDue is not null || _chipPriorityRaw is not null || _chipCategoryName is not null;

    public void SetDueChip(DateTime due, bool hadDate)
    {
        var current = _chipDue ?? DateTime.Today;
        DateTime next;
        if (hadDate)
        {
            // 保留已有时间，否则置 23:59
            var time = _chipDue?.TimeOfDay ?? new TimeSpan(23, 59, 0);
            next = due.Date.Add(time);
        }
        else
        {
            // 只设了时间，日期沿用现有 chip 或 today
            var baseDate = (_chipDue ?? current).Date;
            next = baseDate.Add(due.TimeOfDay);
            if (next < DateTime.Now) next = next.AddDays(1);
        }
        _chipDue = next;
        _chipDueHasDate = _chipDueHasDate || hadDate;
        RaiseChipChange();
    }
    public void ClearDueChip()
    {
        _chipDue = null;
        _chipDueHasDate = false;
        RaiseChipChange();
    }
    public void SetPriorityChip(string raw)
    {
        _chipPriorityRaw = raw;
        RaiseChipChange();
    }
    public void ClearPriorityChip()
    {
        _chipPriorityRaw = null;
        RaiseChipChange();
    }
    public void SetCategoryChip(string name)
    {
        _chipCategoryName = name;
        RaiseChipChange();
    }
    public void ClearCategoryChip()
    {
        _chipCategoryName = null;
        RaiseChipChange();
    }

    [RelayCommand] private void ClearDueChipCmd() => ClearDueChip();
    [RelayCommand] private void ClearPriorityChipCmd() => ClearPriorityChip();
    [RelayCommand] private void ClearCategoryChipCmd() => ClearCategoryChip();

    private void RaiseChipChange()
    {
        OnPropertyChanged(nameof(QuickChipDueLabel));
        OnPropertyChanged(nameof(QuickChipPriorityKey));
        OnPropertyChanged(nameof(QuickChipPriorityLabel));
        OnPropertyChanged(nameof(QuickChipPriorityBrush));
        OnPropertyChanged(nameof(QuickChipCategoryName));
        OnPropertyChanged(nameof(QuickChipCategoryBrush));
        OnPropertyChanged(nameof(HasQuickChips));
    }

    private void ClearAllChips()
    {
        _chipDue = null;
        _chipDueHasDate = false;
        _chipPriorityRaw = null;
        _chipCategoryName = null;
        RaiseChipChange();
    }

    public string Greeting
    {
        get
        {
            var hour = DateTime.Now.Hour;
            if (hour < 12) return "早上好";
            if (hour < 18) return "下午好";
            return "晚上好";
        }
    }

    public void ReloadAll()
    {
        EnsureStarterCategories();

        Categories.Clear();
        foreach (var c in _categories.GetAll()) Categories.Add(c);

        ReloadTasks();
    }

    public void ReloadTasks()
    {
        var allRoots = _tasks.GetAll(includeCompleted: true).Where(t => t.ParentId is null).ToList();
        RecountGlobal(allRoots);
        RefreshCategoryFilters(allRoots);

        var roots = string.IsNullOrWhiteSpace(SearchText)
            ? allRoots
            : SearchRoots(SearchText);

        var filtered = roots
            .Where(IsVisibleInCurrentView)
            .OrderBy(t => t.Completed)
            .ThenBy(t => t.DueAt ?? DateTime.MaxValue)
            .ThenBy(t => t.SortOrder)
            .ThenBy(t => t.Id)
            .ToList();

        var catLookup = Categories.ToDictionary(c => c.Id);
        Tasks.Clear();
        TaskGroups.Clear();

        foreach (var group in BuildGroups(filtered, catLookup))
        {
            TaskGroups.Add(group);
            foreach (var task in group.Tasks)
            {
                Tasks.Add(task);
            }
        }

        ActiveCount = Tasks.Count(t => !t.Completed) + Tasks.Sum(t => t.Subtasks.Count(s => !s.Completed));
        OnPropertyChanged(nameof(HasTaskGroups));
        OnPropertyChanged(nameof(EmptyTitle));
    }

    private IReadOnlyList<TodoTask> SearchRoots(string query)
    {
        try
        {
            return _tasks.Search(query).Where(t => t.ParentId is null).ToList();
        }
        catch
        {
            var q = query.Trim();
            return _tasks.GetAll(includeCompleted: true)
                .Where(t => t.ParentId is null)
                .Where(t => t.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                            || (t.Notes?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
                .ToList();
        }
    }

    private IEnumerable<TaskGroupViewModel> BuildGroups(
        IReadOnlyList<TodoTask> filtered,
        Dictionary<long, Category> catLookup)
    {
        return CurrentView switch
        {
            TodoView.Today => BuildTodayGroups(filtered, catLookup),
            TodoView.Upcoming => BuildUpcomingGroups(filtered, catLookup),
            TodoView.Inbox => BuildInboxGroups(filtered, catLookup),
            _ => []
        };
    }

    private IEnumerable<TaskGroupViewModel> BuildTodayGroups(
        IReadOnlyList<TodoTask> filtered,
        Dictionary<long, Category> catLookup)
    {
        var group = BuildGroup(
            "待办",
            $"剩余 {filtered.Count(t => !t.Completed)} 项",
            true,
            filtered,
            catLookup,
            showHeader: false);

        if (group is not null) yield return group;
    }

    private IEnumerable<TaskGroupViewModel> BuildUpcomingGroups(
        IReadOnlyList<TodoTask> filtered,
        Dictionary<long, Category> catLookup)
    {
        var today = DateTime.Today;
        var tomorrow = filtered.Where(t => t.DueAt?.ToLocalTime().Date == today.AddDays(1)).ToList();
        var week = filtered.Where(t =>
        {
            var due = t.DueAt?.ToLocalTime().Date;
            return due > today.AddDays(1) && due <= today.AddDays(7);
        }).ToList();
        var later = filtered.Where(t => t.DueAt?.ToLocalTime().Date > today.AddDays(7)).ToList();

        var tomorrowGroup = BuildGroup("明天", DateTime.Today.AddDays(1).ToString("M月d日 ddd"), false, tomorrow, catLookup);
        var weekGroup = BuildGroup("本周", "未来 7 天", false, week, catLookup);
        var laterGroup = BuildGroup("稍后", "已安排", false, later, catLookup);

        if (tomorrowGroup is not null) yield return tomorrowGroup;
        if (weekGroup is not null) yield return weekGroup;
        if (laterGroup is not null) yield return laterGroup;
    }

    private IEnumerable<TaskGroupViewModel> BuildInboxGroups(
        IReadOnlyList<TodoTask> filtered,
        Dictionary<long, Category> catLookup)
    {
        var group = BuildGroup(
            ShowCompleted ? "全部任务" : "全部待办",
            $"{filtered.Count(t => !t.Completed)} 项任务",
            false,
            filtered,
            catLookup,
            showHeader: false);

        if (group is not null) yield return group;
    }

    private TaskGroupViewModel? BuildGroup(
        string title,
        string subtitle,
        bool accent,
        IReadOnlyList<TodoTask> roots,
        Dictionary<long, Category> catLookup,
        bool showHeader = true)
    {
        if (roots.Count == 0) return null;

        var group = new TaskGroupViewModel(title, subtitle, accent, showHeader);
        for (var i = 0; i < roots.Count; i++)
        {
            var vm = BuildVm(roots[i], catLookup);
            vm.IsExpanded = TaskGroups.Count == 0 && group.Tasks.Count == 0;

            foreach (var s in _tasks.GetSubtasks(roots[i].Id).Where(s => ShowCompleted || !s.Completed))
            {
                vm.Subtasks.Add(BuildVm(s, catLookup));
            }

            vm.NotifySubtasksChanged();
            group.Tasks.Add(vm);
        }

        return group;
    }

    private TaskItemViewModel BuildVm(TodoTask t, Dictionary<long, Category> catLookup)
    {
        var vm = new TaskItemViewModel(t);
        if (t.CategoryId is { } cid && catLookup.TryGetValue(cid, out var cat))
        {
            vm.CategoryName = LocalizeCategoryName(cat.Name);
            vm.CategoryColor = cat.Color;
        }
        return vm;
    }

    private bool IsVisibleInCurrentView(TodoTask t)
    {
        if (!ShowCompleted && t.Completed) return false;
        if (FilterCategoryId is { } catId && t.CategoryId != catId) return false;

        return CurrentView switch
        {
            TodoView.Today => IsTodayTask(t),
            TodoView.Upcoming => IsUpcomingTask(t),
            TodoView.Inbox => true,
            _ => true
        };
    }

    // 待办 = 今天 + 逾期 + 未排期。一切"该现在做的"都收到这里，未来才单独划进规划。
    private static bool IsTodayTask(TodoTask t)
    {
        if (t.DueAt is null) return true;
        return t.DueAt.Value.ToLocalTime().Date <= DateTime.Today;
    }

    private static bool IsUpcomingTask(TodoTask t)
    {
        if (t.DueAt is null) return false;
        return t.DueAt.Value.ToLocalTime().Date > DateTime.Today;
    }

    private void RecountGlobal(IReadOnlyList<TodoTask> roots)
    {
        var open = roots.Where(t => !t.Completed).ToList();
        TodayCount = open.Count(IsTodayTask);
        UpcomingCount = open.Count(IsUpcomingTask);
        InboxCount = open.Count;

        var todayRoots = roots.Where(IsTodayTask).ToList();
        TotalCount = todayRoots.Count;
        CompletedCount = todayRoots.Count(t => t.Completed);
        OnPropertyChanged(nameof(CompletionPercent));
    }

    private void RefreshCategoryFilters(IReadOnlyList<TodoTask> roots)
    {
        CategoryFilters.Clear();

        var open = roots.Where(t => !t.Completed).ToList();
        CategoryFilters.Add(new CategoryFilterViewModel(null, "全部", "#E8915E", open.Count, FilterCategoryId is null));

        foreach (var c in Categories)
        {
            var count = open.Count(t => t.CategoryId == c.Id);
            CategoryFilters.Add(new CategoryFilterViewModel(c.Id, LocalizeCategoryName(c.Name), c.Color, count, FilterCategoryId == c.Id));
        }
    }

    private void EnsureStarterCategories()
    {
        if (_categories.GetAll().Count > 0) return;
        if (_tasks.GetAll(includeCompleted: true).Count > 0) return;

        var starter = new[]
        {
            ("工作", "#E8915E"),
            ("个人", "#7B98E8"),
            ("设计", "#5FB58A"),
            ("家庭", "#C57BB8")
        };

        for (var i = 0; i < starter.Length; i++)
        {
            _categories.Insert(new Category
            {
                Name = starter[i].Item1,
                Color = starter[i].Item2,
                SortOrder = i
            });
        }
    }

    [RelayCommand]
    private void QuickAdd()
    {
        // 提交前再扫一遍，把没带空格的尾部 token 也当作 chip 提走
        CommitPendingTokens();

        var title = (QuickInput ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title)) return;

        var due = _chipDue ?? CurrentView switch
        {
            TodoView.Upcoming => DateTime.Today.AddDays(1).AddHours(23).AddMinutes(59),
            _ => null
        };

        var priority = _chipPriorityRaw switch
        {
            "!!" or "!高" => TaskPriority.High,
            "!" or "!中" => TaskPriority.Medium,
            "!低" => TaskPriority.Low,
            _ => TaskPriority.Medium
        };

        var categoryId = ResolveCategoryId(_chipCategoryName) ?? FilterCategoryId;
        var t = new TodoTask
        {
            Title = title,
            CategoryId = categoryId,
            Priority = priority,
            DueAt = due?.ToUniversalTime(),
            SortOrder = Tasks.Count
        };

        _tasks.Insert(t);

        _suppressExtract = true;
        QuickInput = string.Empty;
        _suppressExtract = false;
        ClearAllChips();

        ReloadAll();
    }

    // 等同 OnQuickInputChanged 的提取逻辑，但不要求尾部空白
    private void CommitPendingTokens()
    {
        var work = QuickInput ?? string.Empty;
        bool any = false;

        var tagM = TagRegex.Match(work);
        if (tagM.Success)
        {
            SetCategoryChip(tagM.Groups[1].Value);
            work = work.Remove(tagM.Index, tagM.Length);
            any = true;
        }

        var priM = PriorityTokenRegex.Match(work);
        if (priM.Success)
        {
            SetPriorityChip(priM.Groups["p"].Value);
            work = work.Remove(priM.Index, priM.Length);
            any = true;
        }

        if (TomorrowRegex.IsMatch(work))
        {
            SetDueChip(DateTime.Today.AddDays(1), hadDate: true);
            work = TomorrowRegex.Replace(work, " ");
            any = true;
        }
        else if (TodayRegex.IsMatch(work))
        {
            SetDueChip(DateTime.Today, hadDate: true);
            work = TodayRegex.Replace(work, " ");
            any = true;
        }
        else
        {
            var dm = ChineseDateRegex.Match(work);
            if (dm.Success && TryResolveChineseDate(dm, out var pd))
            {
                SetDueChip(pd, hadDate: true);
                work = work.Remove(dm.Index, dm.Length);
                any = true;
            }
        }

        var tm = TimeRegex.Match(work);
        if (tm.Success
            && int.TryParse(tm.Groups["h"].Value, out var h)
            && int.TryParse(tm.Groups["m"].Value, out var min))
        {
            SetDueChip(DateTime.Today.Date.Add(new TimeSpan(h, min, 0)), hadDate: false);
            work = work.Remove(tm.Index, tm.Length);
            any = true;
        }

        if (any)
        {
            var cleaned = Regex.Replace(work, @"\s+", " ").Trim();
            _suppressExtract = true;
            QuickInput = cleaned;
            _suppressExtract = false;
        }
    }

    [RelayCommand]
    private void ToggleComplete(TaskItemViewModel? vm)
    {
        if (vm is null) return;

        vm.Completed = !vm.Completed;
        vm.Model.Completed = vm.Completed;
        vm.Model.CompletedAt = vm.Completed ? DateTime.UtcNow : null;
        _tasks.SetCompleted(vm.Model.Id, vm.Completed);
        ReloadTasks();
    }

    [RelayCommand]
    private void ToggleExpanded(TaskItemViewModel? vm)
    {
        if (vm is null) return;
        vm.IsExpanded = !vm.IsExpanded;
    }

    [RelayCommand]
    private void Delete(TaskItemViewModel? vm)
    {
        if (vm is null) return;
        _tasks.Delete(vm.Model.Id);
        ReloadTasks();
    }

    [RelayCommand]
    private void AddSubtask(TaskItemViewModel? parent)
    {
        if (parent is null) return;
        var t = new TodoTask
        {
            Title = "新子任务",
            ParentId = parent.Model.Id,
            SortOrder = parent.Subtasks.Count
        };
        _tasks.Insert(t);
        ReloadTasks();
    }

    public void AddSubtaskWithTitle(TaskItemViewModel? parent, string title)
    {
        if (parent is null) return;
        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return;

        var t = new TodoTask
        {
            Title = trimmed,
            ParentId = parent.Model.Id,
            SortOrder = parent.Subtasks.Count
        };
        _tasks.Insert(t);

        var catLookup = Categories.ToDictionary(c => c.Id);
        parent.Subtasks.Add(BuildVm(t, catLookup));
        parent.NotifySubtasksChanged();
    }

    /// <summary>Apply a new due date/time to an existing task and persist.</summary>
    public void UpdateTaskDue(TaskItemViewModel? task, DateTime? due)
    {
        if (task is null) return;
        task.DueAt = due;
        task.Model.DueAt = due?.ToUniversalTime();
        _tasks.Update(task.Model);
        ReloadTasks();
    }

    /// <summary>Apply a priority (raw token like "!!"/"!"/"!低") to an existing task and persist.</summary>
    public void UpdateTaskPriority(TaskItemViewModel? task, string raw)
    {
        if (task is null) return;
        var priority = raw switch
        {
            "!!" or "!高" => TaskPriority.High,
            "!" or "!中" => TaskPriority.Medium,
            "!低" => TaskPriority.Low,
            _ => TaskPriority.None
        };
        task.Priority = priority;
        task.Model.Priority = priority;
        _tasks.Update(task.Model);
    }

    /// <summary>Apply a category (by name) to an existing task and persist.</summary>
    public void UpdateTaskCategory(TaskItemViewModel? task, string? name)
    {
        if (task is null) return;
        Category? cat = null;
        if (!string.IsNullOrWhiteSpace(name))
        {
            cat = Categories.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        task.Model.CategoryId = cat?.Id;
        task.CategoryName = cat is null ? null : LocalizeCategoryName(cat.Name);
        task.CategoryColor = cat?.Color;
        _tasks.Update(task.Model);
        ReloadTasks();
    }

    [RelayCommand]
    private void Edit(TaskItemViewModel? vm)
    {
        if (vm is null) return;
        var dlg = new Views.EditTaskDialog(vm, Categories.ToList(), _tasks, _categories)
        {
            Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                    ?? Application.Current.MainWindow
        };
        if (dlg.ShowDialog() == true)
        {
            ReloadAll();
        }
    }

    [RelayCommand]
    private void SetPriority((TaskItemViewModel vm, TaskPriority p) args)
    {
        var (vm, p) = args;
        if (vm is null) return;
        vm.Priority = p;
        vm.Model.Priority = p;
        _tasks.Update(vm.Model);
        ReloadTasks();
    }

    [RelayCommand]
    private void SelectCategoryFilter(CategoryFilterViewModel? filter)
    {
        FilterCategoryId = filter?.Id;
    }

    [RelayCommand]
    private void Search() => ReloadTasks();

    [RelayCommand]
    private void ToggleAutostart()
    {
        if (AutostartEnabled) AutostartService.Enable();
        else AutostartService.Disable();
    }

    [RelayCommand]
    private void ToggleAutoHide()
    {
        _settings.AutoHide = AutoHideEnabled;
    }

    [RelayCommand]
    private void ToggleShowCompleted()
    {
        _settings.ShowCompleted = ShowCompleted;
        ReloadTasks();
    }

    [RelayCommand]
    private void ApplyFilter() => ReloadTasks();

    public void PersistOrder()
    {
        _tasks.Reorder(Tasks.Select(t => t.Model.Id));
    }

    private long? ResolveCategoryId(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var existing = Categories.FirstOrDefault(c =>
            string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(LocalizeCategoryName(c.Name), name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing.Id;

        var color = CategoryPalette[Categories.Count % CategoryPalette.Length];
        var displayName = ToTitleCase(name);

        var id = _categories.Insert(new Category
        {
            Name = displayName,
            Color = color,
            SortOrder = Categories.Count
        });

        Categories.Add(new Category
        {
            Id = id,
            Name = displayName,
            Color = color,
            SortOrder = Categories.Count
        });
        return id;
    }

    private static ParsedQuickTask ParseQuickInput(string input)
    {
        var raw = input.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return new ParsedQuickTask(string.Empty, TaskPriority.Medium, null, null);

        var categoryName = TagRegex.Match(raw) is { Success: true } tag ? tag.Groups[1].Value : null;
        var title = TagRegex.Replace(raw, " ");

        var priority = TaskPriority.Medium;
        var priorityMatch = PriorityTokenRegex.Matches(title).LastOrDefault();
        if (priorityMatch is not null)
        {
            priority = priorityMatch.Groups["p"].Value switch
            {
                "!!" or "!高" => TaskPriority.High,
                "!" or "!中" => TaskPriority.Medium,
                "!低" => TaskPriority.Low,
                _ => TaskPriority.Medium
            };
            title = PriorityTokenRegex.Replace(title, " ");
        }

        DateTime? dueDate = null;
        var explicitDate = false;
        if (TomorrowRegex.IsMatch(title))
        {
            dueDate = DateTime.Today.AddDays(1);
            explicitDate = true;
            title = TomorrowRegex.Replace(title, " ");
        }
        else if (TodayRegex.IsMatch(title))
        {
            dueDate = DateTime.Today;
            explicitDate = true;
            title = TodayRegex.Replace(title, " ");
        }
        else
        {
            var dateMatch = ChineseDateRegex.Match(title);
            if (dateMatch.Success && TryResolveChineseDate(dateMatch, out var parsedDate))
            {
                dueDate = parsedDate;
                explicitDate = true;
                title = ChineseDateRegex.Replace(title, " ");
            }
        }

        TimeSpan? time = null;
        var timeMatch = TimeRegex.Match(title);
        if (timeMatch.Success
            && int.TryParse(timeMatch.Groups["h"].Value, out var h)
            && int.TryParse(timeMatch.Groups["m"].Value, out var m))
        {
            time = new TimeSpan(h, m, 0);
            title = TimeRegex.Replace(title, " ");
        }

        DateTime? due = null;
        if (dueDate is not null || time is not null)
        {
            var baseDate = dueDate ?? DateTime.Today;
            due = baseDate.Date.Add(time ?? new TimeSpan(23, 59, 0));
            if (!explicitDate && due < DateTime.Now)
            {
                due = due.Value.AddDays(1);
            }
        }

        title = Regex.Replace(title, @"\s+", " ").Trim();
        if (string.IsNullOrWhiteSpace(title)) title = raw;

        return new ParsedQuickTask(title, priority, due, categoryName);
    }

    private static bool TryResolveChineseDate(Match match, out DateTime date)
    {
        date = default;
        if (!int.TryParse(match.Groups["m"].Value, out var month)
            || !int.TryParse(match.Groups["d"].Value, out var day))
        {
            return false;
        }

        var hasYear = match.Groups["y"].Success;
        var year = hasYear && int.TryParse(match.Groups["y"].Value, out var parsedYear)
            ? parsedYear
            : DateTime.Today.Year;

        try
        {
            date = new DateTime(year, month, day);
            if (!hasYear && date.Date < DateTime.Today)
            {
                date = date.AddYears(1);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ToTitleCase(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return trimmed;
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    private static string LocalizeCategoryName(string name)
    {
        return name.Trim().ToLowerInvariant() switch
        {
            "work" => "工作",
            "personal" => "个人",
            "design" => "设计",
            "home" => "家庭",
            _ => name
        };
    }

    partial void OnSearchTextChanged(string value) => ReloadTasks();
    partial void OnFilterCategoryIdChanged(long? value) => ReloadTasks();

    partial void OnQuickInputChanged(string value)
    {
        if (_suppressExtract) return;
        if (string.IsNullOrEmpty(value)) return;

        // 仅当文本以空白结尾时尝试提取，避免误吞用户正在输入的词
        if (!char.IsWhiteSpace(value[^1])) return;

        var work = value;
        bool any = false;

        var tagM = LiveTagRegex.Match(work);
        if (tagM.Success)
        {
            SetCategoryChip(tagM.Groups["n"].Value);
            work = work.Remove(tagM.Index, tagM.Length);
            any = true;
        }

        var priM = LivePriorityRegex.Match(work);
        if (priM.Success)
        {
            SetPriorityChip(priM.Groups["p"].Value);
            work = work.Remove(priM.Index, priM.Length);
            any = true;
        }

        Match? dateMatch = null;
        bool dateMatched = false;
        DateTime? dateOnly = null;

        var todayM = LiveTodayRegex.Match(work);
        if (todayM.Success) { dateMatch = todayM; dateOnly = DateTime.Today; dateMatched = true; }
        else
        {
            var tomorrowM = LiveTomorrowRegex.Match(work);
            if (tomorrowM.Success) { dateMatch = tomorrowM; dateOnly = DateTime.Today.AddDays(1); dateMatched = true; }
            else
            {
                var cd = LiveChineseDateRegex.Match(work);
                if (cd.Success && TryResolveChineseDate(cd, out var pd)) { dateMatch = cd; dateOnly = pd; dateMatched = true; }
            }
        }
        if (dateMatched && dateMatch is not null && dateOnly is { } d)
        {
            SetDueChip(d, hadDate: true);
            work = work.Remove(dateMatch.Index, dateMatch.Length);
            any = true;
        }

        var timeM = LiveTimeRegex.Match(work);
        if (timeM.Success
            && int.TryParse(timeM.Groups["h"].Value, out var h)
            && int.TryParse(timeM.Groups["min"].Value, out var min))
        {
            SetDueChip(DateTime.Today.Date.Add(new TimeSpan(h, min, 0)), hadDate: false);
            work = work.Remove(timeM.Index, timeM.Length);
            any = true;
        }

        if (any)
        {
            var cleaned = Regex.Replace(work, @"\s+", " ").TrimStart();
            _suppressExtract = true;
            QuickInput = cleaned;
            _suppressExtract = false;
        }
    }

    private static string FormatDueLabel(DateTime due)
    {
        var today = DateTime.Today;
        string datePart = due.Date == today ? "今天"
            : due.Date == today.AddDays(1) ? "明天"
            : due.Year == today.Year ? $"{due.Month}月{due.Day}日"
            : $"{due.Year}年{due.Month}月{due.Day}日";
        bool noTime = due.TimeOfDay == new TimeSpan(23, 59, 0) || due.TimeOfDay == TimeSpan.Zero;
        return noTime ? datePart : $"{datePart} {due:HH:mm}";
    }

    private static SolidColorBrush BrushFromHexStatic(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return new SolidColorBrush(Colors.Transparent);
        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color c) return new SolidColorBrush(c);
        }
        catch { /* fall through */ }
        return new SolidColorBrush(Colors.Transparent);
    }

    partial void OnCurrentViewChanged(TodoView value)
    {
        OnPropertyChanged(nameof(IsTodayView));
        OnPropertyChanged(nameof(IsUpcomingView));
        OnPropertyChanged(nameof(IsInboxView));
        OnPropertyChanged(nameof(ViewTitle));
        OnPropertyChanged(nameof(EmptyTitle));
        ReloadTasks();
    }

    partial void OnShowCompletedChanged(bool value)
    {
        OnPropertyChanged(nameof(ViewTitle));
        OnPropertyChanged(nameof(EmptyTitle));
    }

    private sealed record ParsedQuickTask(
        string Title,
        TaskPriority Priority,
        DateTime? DueLocal,
        string? CategoryName);
}

public partial class TaskGroupViewModel : ObservableObject
{
    public TaskGroupViewModel(string title, string subtitle, bool isAccent, bool showHeader = true)
    {
        Title = title.ToUpperInvariant();
        Subtitle = subtitle;
        IsAccent = isAccent;
        ShowHeader = showHeader;
    }

    public string Title { get; }
    public string Subtitle { get; }
    public bool IsAccent { get; }
    public bool ShowHeader { get; }
    public ObservableCollection<TaskItemViewModel> Tasks { get; } = new();
}

public partial class CategoryFilterViewModel : ObservableObject
{
    public CategoryFilterViewModel(long? id, string name, string color, int count, bool isSelected)
    {
        Id = id;
        Name = name;
        Color = color;
        Count = count;
        IsSelected = isSelected;
    }

    public long? Id { get; }
    public string Name { get; }
    public string Color { get; }
    public int Count { get; }
    public bool IsSelected { get; }
    public Brush Brush => BrushFromHex(Color);

    private static SolidColorBrush BrushFromHex(string? hex)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex) is Color c)
            {
                return new SolidColorBrush(c);
            }
        }
        catch
        {
            // Ignore invalid custom colors.
        }

        return new SolidColorBrush(Colors.Transparent);
    }
}
