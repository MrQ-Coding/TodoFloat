using System.IO;

namespace TodoFloat.Services;

public enum StartupNoticeKind
{
    FirstInstall,
    Update
}

public sealed record StartupNotice(StartupNoticeKind Kind, string Version);

public static class VersionAnnouncementService
{
    private const string AnnouncementFileName = "VersionAnnouncement.txt";
    private const string PendingAnnouncementFileName = "pending-version-announcement.txt";

    public static void MarkFirstInstall(string? version)
    {
        WritePendingNotice(StartupNoticeKind.FirstInstall, version);
    }

    public static void MarkUpdated(string? version)
    {
        WritePendingNotice(StartupNoticeKind.Update, version);
    }

    public static StartupNotice? ConsumePendingNotice()
    {
        var path = GetPendingAnnouncementPath();
        if (!File.Exists(path)) return null;

        var raw = File.ReadAllText(path).Trim();
        File.Delete(path);
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var parts = raw.Split('|', 2);
        if (parts.Length == 2 && Enum.TryParse<StartupNoticeKind>(parts[0], out var kind))
        {
            return new StartupNotice(kind, NormalizeVersion(parts[1]));
        }

        return new StartupNotice(StartupNoticeKind.Update, NormalizeVersion(raw));
    }

    private static void WritePendingNotice(StartupNoticeKind kind, string? version)
    {
        var versionText = string.IsNullOrWhiteSpace(version) ? "unknown" : version.Trim();
        Directory.CreateDirectory(GetStateDirectory());
        File.WriteAllText(GetPendingAnnouncementPath(), $"{kind}|{versionText}");
    }

    public static string GetAnnouncementContent()
    {
        var path = Path.Combine(AppContext.BaseDirectory, AnnouncementFileName);
        if (!File.Exists(path))
        {
            return "暂无版本公告。";
        }

        var content = File.ReadAllText(path).Trim();
        return string.IsNullOrWhiteSpace(content) ? "暂无版本公告。" : content;
    }

    private static string GetPendingAnnouncementPath() =>
        Path.Combine(GetStateDirectory(), PendingAnnouncementFileName);

    private static string GetStateDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TodoFloat.config");

    private static string NormalizeVersion(string? version) =>
        string.IsNullOrWhiteSpace(version) ? "unknown" : version.Trim();
}
