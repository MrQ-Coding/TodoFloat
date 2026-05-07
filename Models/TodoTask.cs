namespace TodoFloat.Models;

public enum TaskPriority
{
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3
}

public class TodoTask
{
    public long Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Notes { get; set; }
    public long? ParentId { get; set; }
    public long? CategoryId { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.None;
    public DateTime? DueAt { get; set; }
    public DateTime? RemindAt { get; set; }
    public bool Completed { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class Category
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = "#7E57C2";
    public int SortOrder { get; set; }
}

public class Tag
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
}
