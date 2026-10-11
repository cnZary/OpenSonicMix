using System.Runtime.InteropServices;
using System.Windows;
using SonicCube.Panel.Ipc;
using SonicCube.Panel.Views;

namespace SonicCube.Panel;

public partial class App : Application
{
    private const string MutexName = @"Local\SonicCube.Panel.SingleInstance";
    private Mutex? _mutex;

    private void OnStartup(object sender, StartupEventArgs e)
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

        _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            ActivateExistingPanel();
            Shutdown();
            return;
        }

        bool simulate = e.Args.Any(a => a is "--simulate" or "-sim");
        var client = simulate ? null : new TrayClient();
        var win = new MainWindow(client, simulate);

        if (client != null)
        {
            string trayPath = System.IO.Path.Combine(AppContext.BaseDirectory, "SonicCube.Tray.exe");
            _ = Task.Run(async () => await client.EnsureConnectedAsync(trayPath));
        }

        win.Show();
        // 同步放在窗口显示之后,不阻塞首帧
        win.BeginSync();
    }

    private static void ActivateExistingPanel()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("SonicCube.Panel"))
            {
                if (p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero)
                {
                    ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                    SetForegroundWindow(p.MainWindowHandle);
                    break;
                }
            }
        }
        catch { }
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

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
