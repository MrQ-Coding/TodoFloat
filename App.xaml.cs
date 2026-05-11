using System.Globalization;
using System.Windows;
using TodoFloat.Data;
using TodoFloat.Services;
using Velopack;

namespace TodoFloat;

public partial class App : Application
{
    public static SettingsService Settings { get; } = new();
    public static ReminderService Reminders { get; private set; } = null!;

    private static System.Threading.Mutex? _singleInstanceMutex;

    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        var culture = new CultureInfo("zh-CN");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        base.OnStartup(e);

        _singleInstanceMutex = new System.Threading.Mutex(true, "TodoFloat_SingleInstance", out var owns);
        if (!owns)
        {
            MessageBox.Show("TodoFloat 已在运行。", "TodoFloat", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Database.Initialize();

        if (e.Args.Contains("--seed-test-data"))
        {
            TestDataSeeder.Run();
            Shutdown();
            return;
        }

        Reminders = new ReminderService(new TaskRepository());
        Reminders.Start();

        var win = new MainWindow();
        win.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Reminders?.Dispose();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
