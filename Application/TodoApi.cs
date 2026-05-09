using TodoFloat.Data;
using TodoFloat.Models;

namespace TodoFloat.Application;

public sealed class TodoApi : ITodoApi
{
    private const string DefaultCategoryColor = "#7B98E8";

    private readonly TaskRepository _tasks;
    private readonly CategoryRepository _categories;

    public TodoApi(TaskRepository tasks, CategoryRepository categories)
    {
        _tasks = tasks;
        _categories = categories;
    }

    public IReadOnlyList<TodoTaskDto> ListTasks(TodoTaskQuery? query = null)
    {
        query ??= new TodoTaskQuery();

        IEnumerable<TodoTask> tasks = query.ParentId is { } parentId
            ? _tasks.GetSubtasks(parentId)
            : query.RootOnly
                ? _tasks.GetAll(query.IncludeCompleted)
                : _tasks.GetAllTasks(query.IncludeCompleted);

        if (!query.IncludeCompleted)
        {
            tasks = tasks.Where(t => !t.Completed);
        }

        if (query.CategoryId is { } categoryId)
        {
            tasks = tasks.Where(t => t.CategoryId == categoryId);
        }

        return tasks.Select(TodoApiMapping.ToDto).ToList();
    }

    public TodoTaskDto? GetTask(long id) =>
        _tasks.GetById(id) is { } task ? TodoApiMapping.ToDto(task) : null;

    public IReadOnlyList<TodoTaskDto> GetSubtasks(long parentId, bool includeCompleted = true) =>
        ListTasks(new TodoTaskQuery(includeCompleted, ParentId: parentId));

    public IReadOnlyList<TodoTaskDto> SearchTasks(string query, bool includeCompleted = true)
    {
        var results = _tasks.Search(query);
        if (!includeCompleted)
        {
            results = results.Where(t => !t.Completed).ToList();
        }

        return results.Select(TodoApiMapping.ToDto).ToList();
    }

    public long CreateTask(CreateTaskRequest request)
    {
        var title = RequireName(request.Title, "任务标题");
        var completedAt = request.Completed ? (DateTime?)DateTime.UtcNow : null;
        var sortOrder = request.SortOrder ?? NextSortOrder(request.ParentId);

        return _tasks.Insert(new TodoTask
        {
            Title = title,
            Notes = NullIfWhiteSpace(request.Notes),
            ParentId = request.ParentId,
            CategoryId = request.CategoryId,
            Priority = request.Priority,
            DueAt = request.DueAt,
            RemindAt = request.RemindAt,
            Completed = request.Completed,
            CompletedAt = completedAt,
            SortOrder = sortOrder
        });
    }

    public bool UpdateTask(UpdateTaskRequest request)
    {
        var existing = _tasks.GetById(request.Id);
        if (existing is null) return false;

        var title = RequireName(request.Title, "任务标题");
        var completedAt = request.Completed
            ? request.CompletedAt ?? existing.CompletedAt ?? (DateTime?)DateTime.UtcNow
            : null;

        _tasks.Update(new TodoTask
        {
            Id = request.Id,
            Title = title,
            Notes = NullIfWhiteSpace(request.Notes),
            ParentId = request.ParentId,
            CategoryId = request.CategoryId,
            Priority = request.Priority,
            DueAt = request.DueAt,
            RemindAt = request.RemindAt,
            Completed = request.Completed,
            CompletedAt = completedAt,
            SortOrder = request.SortOrder,
            CreatedAt = existing.CreatedAt,
            UpdatedAt = existing.UpdatedAt
        });

        return true;
    }

    public bool RenameTask(long id, string title)
    {
        var existing = _tasks.GetById(id);
        if (existing is null) return false;

        existing.Title = RequireName(title, "任务标题");
        _tasks.Update(existing);
        return true;
    }

    public bool SetCompleted(long id, bool completed) =>
        _tasks.SetCompleted(id, completed);

    public bool DeleteTask(long id)
    {
        if (_tasks.GetById(id) is null) return false;
        _tasks.Delete(id);
        return true;
    }

    public void ReorderRootTasks(IEnumerable<long> orderedIds) =>
        _tasks.Reorder(orderedIds);

    public IReadOnlyList<CategoryDto> ListCategories() =>
        _categories.GetAll().Select(TodoApiMapping.ToDto).ToList();

    public CategoryDto? GetCategory(long id) =>
        _categories.GetById(id) is { } category ? TodoApiMapping.ToDto(category) : null;

    public long CreateCategory(CreateCategoryRequest request)
    {
        var existing = _categories.GetAll();
        var sortOrder = request.SortOrder ?? existing.Count;
        return _categories.Insert(new Category
        {
            Name = RequireName(request.Name, "分类名称"),
            Color = NormalizeColor(request.Color),
            SortOrder = sortOrder
        });
    }

    public bool UpdateCategory(UpdateCategoryRequest request)
    {
        if (_categories.GetById(request.Id) is null) return false;

        _categories.Update(new Category
        {
            Id = request.Id,
            Name = RequireName(request.Name, "分类名称"),
            Color = NormalizeColor(request.Color),
            SortOrder = request.SortOrder
        });

        return true;
    }

    public bool DeleteCategory(long id)
    {
        if (_categories.GetById(id) is null) return false;
        _categories.Delete(id);
        return true;
    }

    private int NextSortOrder(long? parentId) =>
        parentId is { } id
            ? _tasks.GetSubtasks(id).Count
            : _tasks.GetAll(includeCompleted: true).Count;

    private static string RequireName(string value, string label)
    {
        var trimmed = value.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new ArgumentException($"{label}不能为空。", nameof(value));
        }

        return trimmed;
    }

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static string NormalizeColor(string? color) =>
        string.IsNullOrWhiteSpace(color) ? DefaultCategoryColor : color.Trim();
}
