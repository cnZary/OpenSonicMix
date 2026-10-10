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

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
