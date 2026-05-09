using TodoFloat.Models;

namespace TodoFloat.Application;

internal static class TodoApiMapping
{
    public static TodoTaskDto ToDto(TodoTask task) =>
        new(
            task.Id,
            task.Title,
            task.Notes,
            task.ParentId,
            task.CategoryId,
            task.Priority,
            task.DueAt,
            task.RemindAt,
            task.Completed,
            task.CompletedAt,
            task.SortOrder,
            task.CreatedAt,
            task.UpdatedAt);

    public static CategoryDto ToDto(Category category) =>
        new(category.Id, category.Name, category.Color, category.SortOrder);

    public static TodoTask ToModel(TodoTaskDto dto) =>
        new()
        {
            Id = dto.Id,
            Title = dto.Title,
            Notes = dto.Notes,
            ParentId = dto.ParentId,
            CategoryId = dto.CategoryId,
            Priority = dto.Priority,
            DueAt = dto.DueAt,
            RemindAt = dto.RemindAt,
            Completed = dto.Completed,
            CompletedAt = dto.CompletedAt,
            SortOrder = dto.SortOrder,
            CreatedAt = dto.CreatedAt,
            UpdatedAt = dto.UpdatedAt
        };

    public static Category ToModel(CategoryDto dto) =>
        new()
        {
            Id = dto.Id,
            Name = dto.Name,
            Color = dto.Color,
            SortOrder = dto.SortOrder
        };

    public static UpdateTaskRequest ToUpdateRequest(TodoTask task) =>
        new(
            task.Id,
            task.Title,
            task.Notes,
            task.ParentId,
            task.CategoryId,
            task.Priority,
            task.DueAt,
            task.RemindAt,
            task.Completed,
            task.CompletedAt,
            task.SortOrder);
}
