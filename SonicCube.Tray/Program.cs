using SonicCube.Tray.Device;
using SonicCube.Tray.Ipc;
using SonicCube.Tray.Logging;
using SonicCube.Tray.Native;
using SonicCube.Tray.Settings;

namespace SonicCube.Tray;

/// <summary>
/// SonicCube.Tray 入口:单实例 Mutex → 配置/日志 → 命令协调器 → 工作模式 → IPC → 托盘消息循环。
/// </summary>
internal static class Program
{
    private const string MutexName = @"Local\SonicCube.Tray.SingleInstance";

    [STAThread]
    private static int Main()
    {
        // 单实例:重复启动不得创建第二套托盘/MIDI 服务。
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // 已有一个 Tray 在运行,直接退出(可选择激活已有实例的图标)。
            return 0;
        }

        var settings = new SettingsStore();
        settings.Load();

        // 语言
        Localization.TrayStrings.Language = Localization.TrayStrings.Resolve(settings.Current.Language);

        // 日志
        Logger.Start(SettingsStore.LogsDir,
            settings.Current.Logging.MaxFileSizeMB * 1024L * 1024L,
            settings.Current.Logging.MaxFiles);
        Logger.Info("Tray", "=== SonicCube.Tray 启动 ===");

        try
        {
            // 自启状态与实际注册表同步(EXE 移动后修正路径)。
            if (settings.Current.StartWithWindows)
                Startup.StartupManager.Apply(true);

            var coordinator = new DeviceCoordinator();
            var modeManager = new WorkModeManager(coordinator, settings);
            var ipc = new IpcServer(coordinator, modeManager, settings);
            ipc.Start();

            // 语言变更时更新托盘菜单文案。
            settings.Changed += s => Localization.TrayStrings.Language = Localization.TrayStrings.Resolve(s.Language);

            // 启动工作模式(含启动自动动作/快速模式发现)。
            modeManager.Start();

            using var app = new TrayApplication(coordinator, modeManager, settings, ipc);
            int code = app.Run();

            modeManager.Shutdown();
            ipc.Stop();
            coordinator.Shutdown();
            Logger.Info("Tray", "=== SonicCube.Tray 退出 ===");
            Logger.Stop();
            return code;
        }
        catch (Exception ex)
        {
            Logger.Error("Tray", $"未处理异常: {ex}");
            Logger.Stop();
            return 1;
        }
    }
}
