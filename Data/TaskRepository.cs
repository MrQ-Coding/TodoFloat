using Dapper;
using TodoFloat.Models;

namespace TodoFloat.Data;

public class TaskRepository
{
    private static readonly string Cols =
        "id Id, title Title, notes Notes, parent_id ParentId, category_id CategoryId, " +
        "priority Priority, due_at DueAt, remind_at RemindAt, completed Completed, " +
        "completed_at CompletedAt, sort_order SortOrder, created_at CreatedAt, updated_at UpdatedAt";

    public IReadOnlyList<TodoTask> GetAll(bool includeCompleted = true)
    {
        using var conn = Database.Open();
        var sql = $@"SELECT {Cols} FROM tasks
                     WHERE parent_id IS NULL {(includeCompleted ? "" : "AND completed = 0")}
                     ORDER BY completed ASC, sort_order ASC, id ASC";
        return conn.Query<TodoTask>(sql).ToList();
    }

    public IReadOnlyList<TodoTask> GetAllTasks(bool includeCompleted = true)
    {
        using var conn = Database.Open();
        var sql = $@"SELECT {Cols} FROM tasks
                     WHERE 1=1 {(includeCompleted ? "" : "AND completed = 0")}
                     ORDER BY parent_id IS NOT NULL ASC, completed ASC, sort_order ASC, id ASC";
        return conn.Query<TodoTask>(sql).ToList();
    }

    public TodoTask? GetById(long id)
    {
        using var conn = Database.Open();
        return conn.QueryFirstOrDefault<TodoTask>(
            $"SELECT {Cols} FROM tasks WHERE id=@id",
            new { id });
    }

    public IReadOnlyList<TodoTask> GetSubtasks(long parentId)
    {
        using var conn = Database.Open();
        var sql = $"SELECT {Cols} FROM tasks WHERE parent_id = @parentId ORDER BY sort_order ASC, id ASC";
        return conn.Query<TodoTask>(sql, new { parentId }).ToList();
    }

    public IReadOnlyList<TodoTask> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return GetAllTasks();
        using var conn = Database.Open();
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parameters = new DynamicParameters();
        var clauses = tokens.Select((token, i) =>
        {
            parameters.Add($"p{i}", $"%{EscapeLike(token)}%");
            return $"(title LIKE @p{i} ESCAPE '\\' OR notes LIKE @p{i} ESCAPE '\\')";
        });
        var sql = $@"SELECT {Cols} FROM tasks
                     WHERE {string.Join(" AND ", clauses)}
                     ORDER BY parent_id IS NOT NULL ASC, completed ASC, sort_order ASC, id ASC";
        return conn.Query<TodoTask>(sql, parameters).ToList();
    }

    private static string EscapeLike(string value) =>
        value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");

    public long Insert(TodoTask t)
    {
        using var conn = Database.Open();
        var now = DateTime.UtcNow.ToString("o");
        var sql = @"INSERT INTO tasks
            (title, notes, parent_id, category_id, priority, due_at, remind_at,
             completed, completed_at, sort_order, created_at, updated_at)
            VALUES (@Title, @Notes, @ParentId, @CategoryId, @Priority, @DueAt, @RemindAt,
                    @Completed, @CompletedAt, @SortOrder, @createdAt, @updatedAt);
            SELECT last_insert_rowid();";
        return conn.ExecuteScalar<long>(sql, new
        {
            t.Title, t.Notes, t.ParentId, t.CategoryId,
            Priority = (int)t.Priority,
            DueAt = t.DueAt?.ToString("o"),
            RemindAt = t.RemindAt?.ToString("o"),
            Completed = t.Completed ? 1 : 0,
            CompletedAt = t.CompletedAt?.ToString("o"),
            t.SortOrder,
            createdAt = now,
            updatedAt = now
        });
    }

    public void Update(TodoTask t)
    {
        using var conn = Database.Open();
        var sql = @"UPDATE tasks SET
            title=@Title, notes=@Notes, parent_id=@ParentId, category_id=@CategoryId,
            priority=@Priority, due_at=@DueAt, remind_at=@RemindAt,
            completed=@Completed, completed_at=@CompletedAt, sort_order=@SortOrder,
            updated_at=@updatedAt
            WHERE id=@Id";
        conn.Execute(sql, new
        {
            t.Id, t.Title, t.Notes, t.ParentId, t.CategoryId,
            Priority = (int)t.Priority,
            DueAt = t.DueAt?.ToString("o"),
            RemindAt = t.RemindAt?.ToString("o"),
            Completed = t.Completed ? 1 : 0,
            CompletedAt = t.CompletedAt?.ToString("o"),
            t.SortOrder,
            updatedAt = DateTime.UtcNow.ToString("o")
        });
    }

    public bool SetCompleted(long id, bool completed)
    {
        using var conn = Database.Open();
        var now = DateTime.UtcNow.ToString("o");
        return conn.Execute(@"UPDATE tasks SET completed=@c, completed_at=@ca, updated_at=@now WHERE id=@id",
            new { id, c = completed ? 1 : 0, ca = completed ? now : null, now }) > 0;
    }

    public void Delete(long id)
    {
        using var conn = Database.Open();
        conn.Execute("DELETE FROM tasks WHERE id=@id", new { id });
    }

    public void Reorder(IEnumerable<long> orderedIds)
    {
        using var conn = Database.Open();
        using var tx = conn.BeginTransaction();
        var i = 0;
        foreach (var id in orderedIds)
        {
            conn.Execute("UPDATE tasks SET sort_order=@s WHERE id=@id", new { s = i++, id }, tx);
        }
        tx.Commit();
    }
}

public class SettingsRepository
{
    public string? Get(string key)
    {
        using var conn = Database.Open();
        return conn.QueryFirstOrDefault<string>("SELECT value FROM settings WHERE key=@key", new { key });
    }

    public void Set(string key, string? value)
    {
        using var conn = Database.Open();
        conn.Execute(@"INSERT INTO settings(key,value) VALUES(@key,@value)
                       ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            new { key, value });
    }
}

public class CategoryRepository
{
    public IReadOnlyList<Category> GetAll()
    {
        using var conn = Database.Open();
        return conn.Query<Category>(
            "SELECT id Id, name Name, color Color, sort_order SortOrder FROM categories ORDER BY sort_order, id")
            .ToList();
    }

    public Category? GetById(long id)
    {
        using var conn = Database.Open();
        return conn.QueryFirstOrDefault<Category>(
            "SELECT id Id, name Name, color Color, sort_order SortOrder FROM categories WHERE id=@id",
            new { id });
    }

    public long Insert(Category c)
    {
        using var conn = Database.Open();
        return conn.ExecuteScalar<long>(
            @"INSERT INTO categories(name,color,sort_order) VALUES(@Name,@Color,@SortOrder);
              SELECT last_insert_rowid();", c);
    }

    public void Update(Category c)
    {
        using var conn = Database.Open();
        conn.Execute(
            "UPDATE categories SET name=@Name, color=@Color, sort_order=@SortOrder WHERE id=@Id", c);
    }

    public void Delete(long id)
    {
        using var conn = Database.Open();
        conn.Execute("DELETE FROM categories WHERE id=@id", new { id });
    }
}
