using SonicCube.Panel.Forms;
using SonicCube.Panel.Ipc;

namespace SonicCube.Panel;

static class Program
{
    private const string MutexName = @"Local\SonicCube.Panel.SingleInstance";

    [STAThread]
    static void Main()
    {
        // 单实例:重复打开时激活已有 Panel(由 OS/后续激活逻辑处理)。
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // 已有 Panel 在运行:激活其主窗口后退出。
            ActivateExistingPanel();
            return;
        }

        ApplicationConfiguration.Initialize();

        var client = new TrayClient();
        // Tray 与 Panel 同目录;连接失败时尝试启动 Tray。
        string trayPath = Path.Combine(AppContext.BaseDirectory, "SonicCube.Tray.exe");
        _ = Task.Run(async () => await client.EnsureConnectedAsync(trayPath));

        var form = new MainForm(client);
        form.Shown += async (_, _) => await form.InitializeAsync();
        Application.Run(form);
    }

    private static void ActivateExistingPanel()
    {
        try
        {
            // 找到已有 Panel 主窗口并置前。
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("SonicCube.Panel"))
            {
                if (p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero)
                {
                    NativeMethods.SetForegroundWindow(p.MainWindowHandle);
                    NativeMethods.ShowWindow(p.MainWindowHandle, 9); // SW_RESTORE
                    break;
                }
            }
        }
        catch { }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }
}
