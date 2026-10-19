using System.Windows;
using SonicCube.Panel.Ipc;
using SonicCube.Panel.Localization;
using SonicCube.Panel.Startup;
using SonicCube.Panel.Views;

namespace SonicCube.Panel;

public partial class App : Application
{
    private PanelInstance? _instance;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        // 全局异常记录:崩溃时留日志到 %AppData%\SonicCubePanel\panel_crash.log,便于定位。
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("Dispatcher", args.Exception);
            args.Handled = true; // 尽量拦截,避免直接退出
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
            LogCrash("Task", args.Exception);

        try
        {
            bool simulate = e.Args.Any(a => a is "--simulate" or "-sim");
            _instance = new PanelInstance(simulate);
            if (!_instance.IsOwner)
            {
                if (!await _instance.ActivateExistingAsync())
                    MessageBox.Show(Loc.Get("PanelActivationFailed"), Loc.Get("AppTitle"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
                return;
            }

            var client = simulate ? null : new TrayClient();
            var win = new MainWindow(client, simulate);
            MainWindow = win;
            _instance.RegisterWindow(win);

            if (client != null)
            {
                string trayPath = System.IO.Path.Combine(AppContext.BaseDirectory, "SonicCube.Tray.exe");
                _ = Task.Run(async () => await client.EnsureConnectedAsync(trayPath));
            }

            win.Show();
            // 同步放在窗口显示之后,不阻塞首帧
            win.BeginSync();
        }
        catch (Exception ex)
        {
            LogCrash("Startup", ex);
            MessageBox.Show(Loc.Format("PanelStartupFailed", ex.Message), Loc.Get("AppTitle"),
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SonicCubePanel");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "panel_crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}: {ex}\n\n");
        }
        catch { }
    }
}
