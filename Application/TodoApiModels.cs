using TodoFloat.Models;

namespace TodoFloat.Application;

public sealed record TodoTaskDto(
    long Id,
    string Title,
    string? Notes,
    long? ParentId,
    long? CategoryId,
    TaskPriority Priority,
    DateTime? DueAt,
    DateTime? RemindAt,
    bool Completed,
    DateTime? CompletedAt,
    int SortOrder,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public bool IsSubtask => ParentId.HasValue;
}

public sealed record CategoryDto(
    long Id,
    string Name,
    string Color,
    int SortOrder);

public sealed record TodoTaskQuery(
    bool IncludeCompleted = true,
    bool RootOnly = false,
    long? ParentId = null,
    long? CategoryId = null);

public sealed record CreateTaskRequest(
    string Title,
    string? Notes = null,
    long? ParentId = null,
    long? CategoryId = null,
    TaskPriority Priority = TaskPriority.None,
    DateTime? DueAt = null,
    DateTime? RemindAt = null,
    bool Completed = false,
    int? SortOrder = null);

public sealed record UpdateTaskRequest(
    long Id,
    string Title,
    string? Notes,
    long? ParentId,
    long? CategoryId,
    TaskPriority Priority,
    DateTime? DueAt,
    DateTime? RemindAt,
    bool Completed,
    DateTime? CompletedAt,
    int SortOrder);

public sealed record CreateCategoryRequest(
    string Name,
    string Color,
    int? SortOrder = null);

public sealed record UpdateCategoryRequest(
    long Id,
    string Name,
    string Color,
    int SortOrder);
