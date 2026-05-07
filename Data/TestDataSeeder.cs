using Dapper;

namespace TodoFloat.Data;

/// <summary>
/// Wipes all tasks and inserts a curated set of test rows that exercises every state:
/// today/tomorrow/upcoming/overdue/inbox, all 4 priorities, subtasks, completed items, notes.
/// Run via `dotnet run -- --seed-test-data` then re-launch normally.
/// </summary>
internal static class TestDataSeeder
{
    public static void Run()
    {
        using var conn = Database.Open();
        using var tx = conn.BeginTransaction();

        // Wipe tasks (FTS triggers fire automatically — Microsoft.Data.Sqlite has fts5 baked in)
        conn.Execute("DELETE FROM tasks", transaction: tx);
        conn.Execute("DELETE FROM sqlite_sequence WHERE name='tasks'", transaction: tx);

        // Resolve category IDs by name (created lazily by EnsureStarterCategories on first MainWindow launch)
        var workId = conn.ExecuteScalar<long?>("SELECT id FROM categories WHERE name='工作'");
        var personalId = conn.ExecuteScalar<long?>("SELECT id FROM categories WHERE name='个人'");
        var studyId = conn.ExecuteScalar<long?>("SELECT id FROM categories WHERE name='学习'");

        // Anchor "today" to local-noon then derive everything else, so the data adapts to whatever
        // timezone the user runs in. UTC strings get round-tripped via DateTime.ToLocalTime() on load.
        var today = DateTime.Today;
        DateTime At(int dayOffset, int hour, int minute) =>
            today.AddDays(dayOffset).AddHours(hour).AddMinutes(minute).ToUniversalTime();
        DateTime EndOfDay(int dayOffset) => At(dayOffset, 23, 59);

        var createdLong = today.AddDays(-1).ToUniversalTime();
        var createdShort = today.ToUniversalTime();

        long Insert(string title, string? notes, long? parentId, long? categoryId,
                    int priority, DateTime? dueAt, bool completed, DateTime? completedAt,
                    int sortOrder, DateTime created)
        {
            return conn.ExecuteScalar<long>(
                @"INSERT INTO tasks (title, notes, parent_id, category_id, priority, due_at,
                    completed, completed_at, sort_order, created_at, updated_at)
                  VALUES (@title, @notes, @parentId, @categoryId, @priority, @dueAt,
                    @completed, @completedAt, @sortOrder, @created, @created);
                  SELECT last_insert_rowid();",
                new
                {
                    title,
                    notes,
                    parentId,
                    categoryId,
                    priority,
                    dueAt = dueAt?.ToString("o"),
                    completed = completed ? 1 : 0,
                    completedAt = completedAt?.ToString("o"),
                    sortOrder,
                    created = created.ToString("o")
                },
                transaction: tx);
        }

        // ============ Top-level tasks ============
        var quarterly = Insert("完成季度汇报", "重点放在平台投入分析，附上数据图表",
            null, workId, 3, At(0, 9, 0), false, null, 0, createdLong);

        Insert("健身 30 分钟", null, null, personalId, 2,
            At(0, 19, 0), false, null, 1, createdLong);

        Insert("回复客户邮件", null, null, workId, 2,
            EndOfDay(0), false, null, 2, createdLong);

        Insert("整理今日笔记", null, null, studyId, 1,
            EndOfDay(0), false, null, 3, createdLong);

        var weekly = Insert("准备周会演讲", "主题：Q3 平台路线图",
            null, workId, 3, At(1, 14, 0), false, null, 4, createdLong);

        var reactStudy = Insert("学习 React Hooks", null, null, studyId, 1,
            EndOfDay(3), false, null, 5, createdLong);

        Insert("提交上月报销单", null, null, workId, 3,
            EndOfDay(-2), false, null, 6, today.AddDays(-3).ToUniversalTime());

        Insert("阅读《设计心理学》", null, null, studyId, 1,
            null, false, null, 7, createdLong);

        Insert("续护照", "需要带 2 张证件照", null, personalId, 3,
            null, false, null, 8, createdLong);

        Insert("整理书架", null, null, personalId, 1,
            null, false, null, 9, createdLong);

        Insert("修复登录页 bug", null, null, workId, 2,
            EndOfDay(-1), true, today.AddDays(-1).AddHours(16).ToUniversalTime(),
            10, today.AddDays(-2).ToUniversalTime());

        // ============ Subtasks ============
        Insert("草稿大纲", null, quarterly, workId, 0,
            null, true, At(0, 8, 30), 0, createdLong);
        Insert("数据图表", null, quarterly, workId, 0,
            null, true, At(0, 8, 45), 1, createdLong);
        Insert("上传给 leader", null, quarterly, workId, 0,
            null, false, null, 2, createdLong);

        Insert("演讲稿", null, weekly, workId, 0, null, false, null, 0, createdLong);
        Insert("PPT", null, weekly, workId, 0, null, false, null, 1, createdLong);

        Insert("看 useState 文档", null, reactStudy, studyId, 0, null, false, null, 0, createdLong);
        Insert("看 useEffect 文档", null, reactStudy, studyId, 0, null, false, null, 1, createdLong);

        tx.Commit();
    }
}
