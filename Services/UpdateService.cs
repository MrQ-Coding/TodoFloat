using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace TodoFloat.Services;

public enum UpdateCheckStatus
{
    UpdateAvailable,
    UpToDate,
    PendingRestart,
    NotInstalled,
    Failed
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string CurrentVersion,
    string? LatestVersion = null,
    string? ErrorMessage = null,
    UpdateInfo? UpdateInfo = null,
    VelopackAsset? PendingRestart = null);

public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/MrQ-Coding/TodoFloat";
    private static readonly TimeSpan UpdateCheckTimeout = TimeSpan.FromSeconds(20);
    public const string ReleasesUrl = RepoUrl + "/releases";

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var manager = CreateManager();
            var currentText = ToDisplayVersion(manager.CurrentVersion) ?? GetAssemblyVersionText();

            if (!manager.IsInstalled)
            {
                return new UpdateCheckResult(UpdateCheckStatus.NotInstalled, currentText);
            }

            if (manager.UpdatePendingRestart is { } pending)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.PendingRestart,
                    currentText,
                    ToDisplayVersion(pending.Version),
                    PendingRestart: pending);
            }

            var updateInfo = await CheckForUpdatesWithTimeoutAsync(manager, cancellationToken);
            if (updateInfo is null)
            {
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, currentText);
            }

            return new UpdateCheckResult(
                UpdateCheckStatus.UpdateAvailable,
                currentText,
                ToDisplayVersion(updateInfo.TargetFullRelease.Version),
                UpdateInfo: updateInfo);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(
                UpdateCheckStatus.Failed,
                GetAssemblyVersionText(),
                ErrorMessage: ex.Message);
        }
    }

    public async Task<VelopackAsset> DownloadUpdatesAsync(
        UpdateInfo updateInfo,
        Action<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var manager = CreateManager();
        if (!manager.IsInstalled)
        {
            throw new InvalidOperationException("当前运行的不是安装版，无法自动更新。");
        }

        await manager.DownloadUpdatesAsync(updateInfo, progress, cancellationToken);
        return updateInfo.TargetFullRelease;
    }

    public void ApplyUpdatesAndRestart(VelopackAsset? targetRelease)
    {
        var manager = CreateManager();
        manager.ApplyUpdatesAndRestart(targetRelease, Array.Empty<string>());
    }

    public bool IsInstalled()
    {
        try
        {
            return CreateManager().IsInstalled;
        }
        catch
        {
            return false;
        }
    }

    public string GetCurrentVersionText()
    {
        try
        {
            return ToDisplayVersion(CreateManager().CurrentVersion) ?? GetAssemblyVersionText();
        }
        catch
        {
            return GetAssemblyVersionText();
        }
    }

    public static string GetReleaseNotesUrl(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || version.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return ReleasesUrl;
        }

        var tag = version.StartsWith('v') ? version : $"v{version}";
        return $"{ReleasesUrl}/tag/{tag}";
    }

    private static UpdateManager CreateManager()
    {
        var source = new GithubSource(RepoUrl, string.Empty, false, null!);
        return new UpdateManager(source, null!, null!);
    }

    private static async Task<UpdateInfo?> CheckForUpdatesWithTimeoutAsync(
        UpdateManager manager,
        CancellationToken cancellationToken)
    {
        var checkTask = manager.CheckForUpdatesAsync();
        var timeoutTask = Task.Delay(UpdateCheckTimeout, cancellationToken);
        var completedTask = await Task.WhenAny(checkTask, timeoutTask);

        if (completedTask == timeoutTask)
        {
            throw new TimeoutException("检查更新超时，请稍后重试。");
        }

        return await checkTask;
    }

    private static string? ToDisplayVersion(object? version)
    {
        return version?.ToString();
    }

    private static string GetAssemblyVersionText()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        var normalized = new Version(
            version.Major,
            version.Minor,
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0));

        return normalized.Revision > 0
            ? normalized.ToString(4)
            : normalized.ToString(3);
    }
}
