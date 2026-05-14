using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using Forms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;

namespace TodoFloat.InstallerLauncher;

public partial class MainWindow : Window
{
    private const string EmbeddedSetupResourceName = "VelopackSetup.exe";
    private const string AnnouncementResourceName = "VersionAnnouncement.txt";
    private const string AppExeName = "TodoFloat.exe";
    private const string DataPathFileName = "data-path.txt";
    private const string PreviewFirstInstallArgument = "--preview-first-install";

    private readonly string _defaultInstallDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MrQ.TodoFloat");
    private readonly string _defaultDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TodoFloat");
    private readonly bool _previewFirstInstall = HasArgument(PreviewFirstInstallArgument);

    private string? _setupPath;
    private bool _isInstalling;

    public MainWindow()
    {
        InitializeComponent();
        InstallPathTextBox.Text = _defaultInstallDir;
        DataPathTextBox.Text = ReadConfiguredDataDir() ?? _defaultDataDir;
        AnnouncementTextBox.Text = ReadEmbeddedText(AnnouncementResourceName) ?? "暂无版本公告。";

        _setupPath = ResolveSetupPath();
        var installed = !_previewFirstInstall && IsInstalled(InstallPathTextBox.Text);
        InstallButton.Content = installed ? "修复" : "安装";

        if (_setupPath is null && !_previewFirstInstall)
        {
            StatusText.Text = "未找到 Velopack Setup 安装包。请将本启动器与 *-Setup.exe 放在同一目录，或使用发布脚本生成嵌入式启动器。";
            InstallButton.IsEnabled = false;
            return;
        }

        StatusText.Text = _previewFirstInstall
            ? "准备安装 TodoFloat。你可以确认安装位置后开始安装。"
            : installed
            ? "检测到 TodoFloat 已安装。你可以运行修复安装。"
            : "准备安装 TodoFloat。你可以确认安装位置后开始安装。";
    }

    private void BrowseInstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryBrowseFolder("选择 TodoFloat 安装位置", InstallPathTextBox.Text, out var selectedPath))
        {
            InstallPathTextBox.Text = selectedPath;
            InstallButton.Content = !_previewFirstInstall && IsInstalled(selectedPath) ? "修复" : "安装";
        }
    }

    private void BrowseDataButton_Click(object sender, RoutedEventArgs e)
    {
        if (TryBrowseFolder("选择 TodoFloat 数据存储位置", DataPathTextBox.Text, out var selectedPath))
        {
            DataPathTextBox.Text = selectedPath;
        }
    }

    private static bool TryBrowseFolder(string description, string currentPath, out string selectedPath)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = description,
            SelectedPath = Directory.Exists(currentPath)
                ? currentPath
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            UseDescriptionForTitle = true
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            selectedPath = dialog.SelectedPath;
            return true;
        }

        selectedPath = string.Empty;
        return false;
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInstalling) return;

        var installDir = InstallPathTextBox.Text.Trim();
        var dataDir = DataPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(installDir))
        {
            WpfMessageBox.Show(this, "请选择安装位置。", "TodoFloat Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(dataDir))
        {
            WpfMessageBox.Show(this, "请选择数据存储位置。", "TodoFloat Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (_previewFirstInstall)
        {
            WpfMessageBox.Show(this, "这是本地首次安装界面预览，不会执行安装。", "TodoFloat Setup", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (_setupPath is null) return;

        SetInstallingState(true);
        try
        {
            Directory.CreateDirectory(installDir);
            Directory.CreateDirectory(dataDir);
            var exitCode = await RunSetupAsync(_setupPath, installDir);
            if (exitCode != 0)
            {
                WpfMessageBox.Show(this, $"安装程序退出码：{exitCode}", "安装失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            WriteConfiguredDataDir(dataDir);
            StatusText.Text = "安装完成。";
            if (LaunchAfterInstallCheckBox.IsChecked == true)
            {
                LaunchInstalledApp(installDir);
            }

            Close();
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(this, ex.Message, "安装失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetInstallingState(false);
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isInstalling) return;
        Close();
    }

    private void SetInstallingState(bool installing)
    {
        _isInstalling = installing;
        InstallButton.IsEnabled = !installing;
        InstallPathTextBox.IsEnabled = !installing;
        DataPathTextBox.IsEnabled = !installing;
        InstallProgress.Visibility = installing ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = installing ? "正在安装，请稍候…" : StatusText.Text;
    }

    private static async Task<int> RunSetupAsync(string setupPath, string installDir)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = setupPath,
            Arguments = $"--silent --installto {QuoteArgument(installDir)}",
            UseShellExecute = false
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动安装程序。");

        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static void LaunchInstalledApp(string installDir)
    {
        var exe = Path.Combine(installDir, "current", AppExeName);
        if (!File.Exists(exe)) return;

        Process.Start(new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = true
        });
    }

    private static bool IsInstalled(string installDir) =>
        File.Exists(Path.Combine(installDir, "current", AppExeName));

    private static string? ReadConfiguredDataDir()
    {
        var configPath = GetDataPathConfigPath(createDirectory: false);
        if (!File.Exists(configPath)) return null;

        var value = File.ReadAllText(configPath).Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static void WriteConfiguredDataDir(string dataDir)
    {
        var configPath = GetDataPathConfigPath(createDirectory: true);
        File.WriteAllText(configPath, dataDir);
    }

    private static string GetDataPathConfigPath(bool createDirectory)
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TodoFloat.config");

        if (createDirectory)
        {
            Directory.CreateDirectory(configDir);
        }

        return Path.Combine(configDir, DataPathFileName);
    }

    private static string? ResolveSetupPath()
    {
        var embedded = ExtractEmbeddedSetup();
        if (embedded is not null) return embedded;

        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        foreach (var dir in EnumerateSearchDirs(baseDir))
        {
            var setup = dir.EnumerateFiles("*-Setup.exe")
                .Where(file => !file.Name.Equals(Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();

            if (setup is not null) return setup.FullName;
        }

        return null;
    }

    private static IEnumerable<DirectoryInfo> EnumerateSearchDirs(DirectoryInfo start)
    {
        var current = start;
        while (current is not null)
        {
            yield return current;

            var releases = Path.Combine(current.FullName, "Releases");
            if (Directory.Exists(releases)) yield return new DirectoryInfo(releases);

            var testReleases = Path.Combine(current.FullName, "Releases-Test");
            if (Directory.Exists(testReleases)) yield return new DirectoryInfo(testReleases);

            current = current.Parent;
        }
    }

    private static string? ExtractEmbeddedSetup()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var resource = assembly.GetManifestResourceStream(EmbeddedSetupResourceName);
        if (resource is null) return null;

        var version = assembly.GetName().Version?.ToString() ?? "current";
        var extractDir = Path.Combine(Path.GetTempPath(), "TodoFloatSetup", version);
        var setupPath = Path.Combine(extractDir, "MrQ.TodoFloat-win-Setup.exe");
        Directory.CreateDirectory(extractDir);

        if (File.Exists(setupPath) && new FileInfo(setupPath).Length == resource.Length)
        {
            return setupPath;
        }

        using var output = File.Create(setupPath);
        resource.CopyTo(output);
        return setupPath;
    }

    private static string? ReadEmbeddedText(string resourceName)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var resource = assembly.GetManifestResourceStream(resourceName);
        if (resource is null) return null;

        using var reader = new StreamReader(resource);
        return reader.ReadToEnd().Trim();
    }

    private static bool HasArgument(string argument) =>
        Environment.GetCommandLineArgs()
            .Skip(1)
            .Any(arg => string.Equals(arg, argument, StringComparison.OrdinalIgnoreCase));

    private static string QuoteArgument(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
