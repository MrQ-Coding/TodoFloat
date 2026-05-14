using System.IO;
using Microsoft.Data.Sqlite;
using TodoFloat.Data;

namespace TodoFloat.Services;

public sealed record DataStorageSwitchResult(string? CleanupWarning);

public sealed class DataStorageService
{
    public string CurrentDataDirectory => ResolvePhysicalDirectory(Database.DataDirectory);

    public bool TargetHasExistingDatabase(string targetDirectory)
    {
        var target = ResolvePhysicalDirectory(targetDirectory);
        return File.Exists(Path.Combine(target, "todo.db"));
    }

    public bool IsCurrentDataDirectory(string targetDirectory)
    {
        var source = ResolvePhysicalDirectory(CurrentDataDirectory);
        var target = ResolvePhysicalDirectory(targetDirectory);
        return PathsEqual(source, target);
    }

    public DataStorageSwitchResult SwitchDataDirectory(string targetDirectory, bool migrateCurrentData)
    {
        var source = ResolvePhysicalDirectory(CurrentDataDirectory);
        var target = ResolvePhysicalDirectory(targetDirectory);

        if (PathsEqual(source, target))
        {
            Database.SetDataDirectory(target);
            Database.Initialize();
            return new DataStorageSwitchResult(null);
        }

        if (migrateCurrentData)
        {
            EnsureSourceCanBeRemovedAfterMigration(source, target);
        }

        Directory.CreateDirectory(target);

        var copiedCurrentDatabase = false;
        if (migrateCurrentData)
        {
            Database.Checkpoint();
            SqliteConnection.ClearAllPools();
            BackupExistingTargetDatabase(target);
            copiedCurrentDatabase = CopyMainDatabaseFile(source, target);
        }

        Database.SetDataDirectory(target);
        Database.Initialize();

        if (migrateCurrentData && copiedCurrentDatabase)
        {
            SqliteConnection.ClearAllPools();
            return new DataStorageSwitchResult(TryDeleteMigratedSourceDirectory(source));
        }

        return new DataStorageSwitchResult(null);
    }

    private static bool CopyMainDatabaseFile(string source, string target)
    {
        var sourcePath = Path.Combine(source, "todo.db");
        if (!File.Exists(sourcePath)) return false;

        File.Copy(sourcePath, Path.Combine(target, "todo.db"), overwrite: true);
        return true;
    }

    private static void BackupExistingTargetDatabase(string target)
    {
        var path = Path.Combine(target, "todo.db");
        if (!File.Exists(path)) return;

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        File.Copy(path, Path.Combine(target, $"todo.db.backup-before-migrate-{stamp}"), overwrite: false);

        foreach (var sidecarName in new[] { "todo.db-wal", "todo.db-shm" })
        {
            var sidecarPath = Path.Combine(target, sidecarName);
            if (File.Exists(sidecarPath))
            {
                File.Delete(sidecarPath);
            }
        }
    }

    private static string NormalizeDirectory(string directory) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory.Trim()))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string ResolvePhysicalDirectory(string directory)
    {
        var normalized = NormalizeDirectory(directory);
        var info = new DirectoryInfo(normalized);
        if (!info.Exists) return normalized;

        var target = info.ResolveLinkTarget(returnFinalTarget: true);
        return target?.FullName is { Length: > 0 }
            ? NormalizeDirectory(target.FullName)
            : normalized;
    }

    private static void EnsureSourceCanBeRemovedAfterMigration(string source, string target)
    {
        if (!Directory.Exists(source)) return;

        if (IsRootDirectory(source))
        {
            throw new InvalidOperationException("当前数据目录是磁盘根目录，不能在迁移后自动删除。请选择一个专用的数据目录。");
        }

        if (IsSameOrChildDirectory(target, source))
        {
            throw new InvalidOperationException("目标目录不能放在当前数据目录内部，否则迁移后删除旧目录会同时删除新目录。");
        }
    }

    private static string? TryDeleteMigratedSourceDirectory(string source)
    {
        if (!Directory.Exists(source)) return null;
        if (!File.Exists(Path.Combine(source, "todo.db"))) return null;

        var directory = new DirectoryInfo(source);
        var entries = directory.EnumerateFileSystemInfos().ToList();
        var unmanagedEntries = entries
            .Where(entry => !IsTodoFloatManagedSourceEntry(entry))
            .Select(entry => entry.Name)
            .ToList();

        if (unmanagedEntries.Count > 0)
        {
            return $"旧目录包含非 TodoFloat 数据，已保留未删除：{source}";
        }

        try
        {
            foreach (var file in entries.OfType<FileInfo>())
            {
                file.Delete();
            }

            directory.Delete();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"数据已迁移到新位置，但旧目录删除失败：{source}";
        }
    }

    private static bool IsTodoFloatManagedSourceEntry(FileSystemInfo entry)
    {
        if (entry is not FileInfo) return false;

        return entry.Name.StartsWith("todo.db", StringComparison.OrdinalIgnoreCase)
            || entry.Name.StartsWith("todo.backup-", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRootDirectory(string directory)
    {
        var normalized = NormalizeDirectory(directory);
        var root = Path.GetPathRoot(normalized)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return !string.IsNullOrWhiteSpace(root) && PathsEqual(normalized, root);
    }

    private static bool IsSameOrChildDirectory(string candidate, string parent)
    {
        var normalizedCandidate = EnsureTrailingSeparator(NormalizeDirectory(candidate));
        var normalizedParent = EnsureTrailingSeparator(NormalizeDirectory(parent));
        return normalizedCandidate.StartsWith(normalizedParent, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingSeparator(string directory) =>
        directory.EndsWith(Path.DirectorySeparatorChar) || directory.EndsWith(Path.AltDirectorySeparatorChar)
            ? directory
            : directory + Path.DirectorySeparatorChar;

    private static bool PathsEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
