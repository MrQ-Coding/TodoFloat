using Microsoft.Win32;

namespace TodoFloat.Services;

public static class AutostartService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "TodoFloat";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(AppName) is string;
        }
    }

    public static void Enable()
    {
        var exe = Environment.ProcessPath ?? AppContext.BaseDirectory;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true)
            ?? Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(AppName, $"\"{exe}\"");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (key?.GetValue(AppName) != null) key.DeleteValue(AppName, false);
    }
}
