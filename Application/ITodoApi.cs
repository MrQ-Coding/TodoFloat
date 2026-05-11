namespace TodoFloat.Application;

public interface ITodoApi
{
    IReadOnlyList<TodoTaskDto> ListTasks(TodoTaskQuery? query = null);
    TodoTaskDto? GetTask(long id);
    IReadOnlyList<TodoTaskDto> GetSubtasks(long parentId, bool includeCompleted = true);
    IReadOnlyList<TodoTaskDto> SearchTasks(string query, bool includeCompleted = true);
    long CreateTask(CreateTaskRequest request);
    bool UpdateTask(UpdateTaskRequest request);
    bool RenameTask(long id, string title);
    bool SetCompleted(long id, bool completed);
    bool DeleteTask(long id);
    void ReorderRootTasks(IEnumerable<long> orderedIds);

    IReadOnlyList<CategoryDto> ListCategories();
    CategoryDto? GetCategory(long id);
    long CreateCategory(CreateCategoryRequest request);
    bool UpdateCategory(UpdateCategoryRequest request);
    bool DeleteCategory(long id);
}
