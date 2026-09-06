using System.Windows;
using System.Windows.Threading;
using SayAll.Core;
using Application = System.Windows.Application;

namespace SayAll;

public partial class App : Application
{
    public static AppState? State { get; private set; }
    public static MainWindow? SettingsWindow { get; private set; }
    public static TrayIcon? Tray { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppLogger.Initialize();
        var settings = AppSettings.Load();
        L10n.SetLanguage(ParseLanguage(settings));

        State = new AppState(settings);

        Tray = new TrayIcon();
        Tray.Show();

        if (!e.Args.Contains("--tray"))
        {
            ShowSettings();
        }

        State.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Write("FATAL " + args.Exception);
            args.Handled = true;
        };
    }

    private static AppLanguage ParseLanguage(AppSettings settings)
    {
        return settings.LanguageRaw switch
        {
            "zh-Hans" or "zh_hans" => AppLanguage.zh_Hans,
            "en" => AppLanguage.en,
            _ => AppLanguage.System,
        };
    }

    public static void ShowSettings()
    {
        if (SettingsWindow is null || !SettingsWindow.IsLoaded)
        {
            SettingsWindow = new MainWindow(State!);
            SettingsWindow.Closed += (_, _) => SettingsWindow = null;
        }
        if (SettingsWindow.WindowState == WindowState.Minimized)
        {
            SettingsWindow.WindowState = WindowState.Normal;
        }
        SettingsWindow.Show();
        SettingsWindow.Activate();
        SettingsWindow.RefreshAll();
    }

    public static void Quit()
    {
        try
        {
            State?.Stop();
            Tray?.Dispose();
            State?.Dispose();
        }
        finally
        {
            Current?.Shutdown();
        }
    }
}

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--mcp-stdio"))
        {
            var app = new Core.SayAllMcpServer(new AppSettings());
            app.RunOnCurrentThread();
            return 0;
        }

        using var mutex = new Mutex(true, @"Local\SayAll.RemoteMic.Windows", out var createdNew);
        if (!createdNew)
        {
            return 0;
        }

        var application = new App();
        application.InitializeComponent(); // 加载 App.xaml 资源字典与 ShutdownMode
        application.Run();
        return 0;
    }
}