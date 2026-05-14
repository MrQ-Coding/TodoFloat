using System.IO;
using Microsoft.Data.Sqlite;

namespace TodoFloat.Data;

public static class Database
{
    private static string? _dbPath;
    private const string DataPathFileName = "data-path.txt";

    public static string DbPath
    {
        get
        {
            if (_dbPath != null) return _dbPath;
            var dir = ResolveDataDirectory();
            Directory.CreateDirectory(dir);
            _dbPath = Path.Combine(dir, "todo.db");
            return _dbPath;
        }
    }

    public static string DataDirectory => Path.GetDirectoryName(DbPath)!;

    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TodoFloat");

    public static string DataPathConfigPath => GetDataPathConfigPath();

    public static SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={DbPath};Foreign Keys=True;");
        conn.Open();
        return conn;
    }

    public static void Initialize()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Schema;
        cmd.ExecuteNonQuery();
    }

    public static void SetDataDirectory(string directory)
    {
        var normalized = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory.Trim()));
        Directory.CreateDirectory(normalized);
        File.WriteAllText(GetDataPathConfigPath(), normalized);
        _dbPath = Path.Combine(normalized, "todo.db");
    }

    public static void Checkpoint()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }

    private static string ResolveDataDirectory()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("TODOFLOAT_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Environment.ExpandEnvironmentVariables(fromEnvironment.Trim());
        }

        var configPath = GetDataPathConfigPath();
        if (File.Exists(configPath))
        {
            var configured = File.ReadAllText(configPath).Trim();
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Environment.ExpandEnvironmentVariables(configured);
            }
        }

        return DefaultDataDirectory;
    }

    private static string GetDataPathConfigPath()
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TodoFloat.config");
        Directory.CreateDirectory(configDir);
        return Path.Combine(configDir, DataPathFileName);
    }

    private const string Schema = @"
PRAGMA journal_mode=WAL;
PRAGMA foreign_keys=ON;

CREATE TABLE IF NOT EXISTS categories (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    name         TEXT    NOT NULL,
    color        TEXT    NOT NULL DEFAULT '#7E57C2',
    sort_order   INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE IF NOT EXISTS tasks (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    title        TEXT    NOT NULL,
    notes        TEXT,
    parent_id    INTEGER REFERENCES tasks(id) ON DELETE CASCADE,
    category_id  INTEGER REFERENCES categories(id) ON DELETE SET NULL,
    priority     INTEGER NOT NULL DEFAULT 0,
    due_at       TEXT,
    remind_at    TEXT,
    completed    INTEGER NOT NULL DEFAULT 0,
    completed_at TEXT,
    sort_order   INTEGER NOT NULL DEFAULT 0,
    created_at   TEXT    NOT NULL,
    updated_at   TEXT    NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_tasks_parent   ON tasks(parent_id);
CREATE INDEX IF NOT EXISTS idx_tasks_category ON tasks(category_id);
CREATE INDEX IF NOT EXISTS idx_tasks_due      ON tasks(due_at);
CREATE INDEX IF NOT EXISTS idx_tasks_remind   ON tasks(remind_at);

CREATE TABLE IF NOT EXISTS tags (
    id   INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT    NOT NULL UNIQUE
);

CREATE TABLE IF NOT EXISTS task_tags (
    task_id INTEGER NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    tag_id  INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE,
    PRIMARY KEY (task_id, tag_id)
);

CREATE VIRTUAL TABLE IF NOT EXISTS tasks_fts USING fts5(
    title, notes, content='tasks', content_rowid='id', tokenize='unicode61'
);

CREATE TRIGGER IF NOT EXISTS tasks_ai AFTER INSERT ON tasks BEGIN
    INSERT INTO tasks_fts(rowid, title, notes) VALUES (new.id, new.title, new.notes);
END;
CREATE TRIGGER IF NOT EXISTS tasks_ad AFTER DELETE ON tasks BEGIN
    INSERT INTO tasks_fts(tasks_fts, rowid, title, notes) VALUES('delete', old.id, old.title, old.notes);
END;
CREATE TRIGGER IF NOT EXISTS tasks_au AFTER UPDATE ON tasks BEGIN
    INSERT INTO tasks_fts(tasks_fts, rowid, title, notes) VALUES('delete', old.id, old.title, old.notes);
    INSERT INTO tasks_fts(rowid, title, notes) VALUES (new.id, new.title, new.notes);
END;

CREATE TABLE IF NOT EXISTS settings (
    key   TEXT PRIMARY KEY,
    value TEXT
);
";
}
