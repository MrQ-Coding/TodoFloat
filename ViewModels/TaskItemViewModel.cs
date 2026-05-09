using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using TodoFloat.Models;

namespace TodoFloat.ViewModels;

public partial class TaskItemViewModel : ObservableObject
{
    public TodoTask Model { get; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _completed;
    [ObservableProperty] private TaskPriority _priority;
    [ObservableProperty] private DateTime? _dueAt;
    [ObservableProperty] private string? _categoryName;
    [ObservableProperty] private string? _categoryColor;
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isAddingSubtask;
    [ObservableProperty] private string _draftSubtaskTitle = string.Empty;
    [ObservableProperty] private bool _isInlineEditing;
    [ObservableProperty] private string _editingTitle = string.Empty;
    [ObservableProperty] private ObservableCollection<TaskItemViewModel> _subtasks = new();
    private int _subtaskTotalCount;
    private int _subtaskCompletedCount;

    public TaskItemViewModel(TodoTask t)
    {
        Model = t;
        _title = t.Title;
        _completed = t.Completed;
        _priority = t.Priority;
        _dueAt = t.DueAt?.ToLocalTime();
    }

    public string? Notes => Model.Notes;

    public CornerRadius RowCornerRadius => IsExpanded
        ? new CornerRadius(12, 12, 0, 0)
        : new CornerRadius(12);

    public CornerRadius DetailCornerRadius => new(0, 0, 12, 12);

    public Brush PriorityBrush => Priority switch
    {
        TaskPriority.High => BrushFromHex("#D92D20"),
        TaskPriority.Medium => BrushFromHex("#F59E0B"),
        TaskPriority.Low => BrushFromHex("#4F7DF3"),
        _ => Brushes.Transparent
    };

    public string PriorityKey => Priority switch
    {
        TaskPriority.High => "high",
        TaskPriority.Medium => "med",
        TaskPriority.Low => "low",
        _ => "none"
    };

    public string PriorityLabel => Priority switch
    {
        TaskPriority.High => "高",
        TaskPriority.Medium => "中",
        TaskPriority.Low => "低",
        _ => "普通"
    };

    public Brush CategoryBrush => BrushFromHex(CategoryColor);

    public string DueLabel
    {
        get
        {
            if (DueAt is not { } d) return string.Empty;

            var now = DateTime.Now;
            var suffix = d.Hour == 23 && d.Minute == 59 ? string.Empty : $" {d:HH:mm}";

            if (d.Date == now.Date) return $"今天{suffix}";
            if (d.Date == now.Date.AddDays(1)) return $"明天{suffix}";
            if (d.Year == now.Year) return $"{d:M月d日}{suffix}";
            return $"{d:yyyy年M月d日}{suffix}";
        }
    }

    public bool IsOverdue
    {
        get
        {
            if (DueAt is not { } d || Completed) return false;
            if (d.Hour == 23 && d.Minute == 59) return d.Date < DateTime.Today;
            return d < DateTime.Now;
        }
    }

    public string SubtaskSummary
    {
        get
        {
            if (_subtaskTotalCount == 0) return string.Empty;
            return $"{_subtaskCompletedCount}/{_subtaskTotalCount}";
        }
    }

    public bool HasSubtasks => _subtaskTotalCount > 0;

    public double SubtaskProgress
    {
        get
        {
            if (_subtaskTotalCount == 0) return 0;
            return _subtaskCompletedCount * 100.0 / _subtaskTotalCount;
        }
    }

    partial void OnPriorityChanged(TaskPriority value)
    {
        OnPropertyChanged(nameof(PriorityBrush));
        OnPropertyChanged(nameof(PriorityKey));
        OnPropertyChanged(nameof(PriorityLabel));
    }

    partial void OnDueAtChanged(DateTime? value)
    {
        OnPropertyChanged(nameof(DueLabel));
        OnPropertyChanged(nameof(IsOverdue));
    }

    partial void OnCompletedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsOverdue));
    }

    partial void OnCategoryColorChanged(string? value)
    {
        OnPropertyChanged(nameof(CategoryBrush));
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(RowCornerRadius));
        OnPropertyChanged(nameof(DetailCornerRadius));
    }

    public void NotifySubtasksChanged()
    {
        SetSubtaskStats(Subtasks.Count, Subtasks.Count(s => s.Completed));
    }

    public void SetSubtaskStats(int totalCount, int completedCount)
    {
        _subtaskTotalCount = Math.Max(0, totalCount);
        _subtaskCompletedCount = Math.Clamp(completedCount, 0, _subtaskTotalCount);
        OnPropertyChanged(nameof(SubtaskSummary));
        OnPropertyChanged(nameof(HasSubtasks));
        OnPropertyChanged(nameof(SubtaskProgress));
    }

    private static SolidColorBrush BrushFromHex(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return new SolidColorBrush(Colors.Transparent);

        try
        {
            if (ColorConverter.ConvertFromString(hex) is Color c)
            {
                return new SolidColorBrush(c);
            }
        }
        catch
        {
            // Fall through to transparent for invalid custom category colors.
        }

        return new SolidColorBrush(Colors.Transparent);
    }
}
