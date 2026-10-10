using Microsoft.Win32;

namespace SonicCube.Tray.Startup;

/// <summary>
/// Windows 登录自启管理(见需求第十二节)。
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run,仅启动 Tray,不需要管理员权限。
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SonicCube.Tray";

    /// <summary>应用或移除自启项。路径加引号,处理 EXE 移动后的路径更新。</summary>
    public static void Apply(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null)
            {
                Logging.Logger.Warn("Startup", "无法打开 Run 注册表项");
                return;
            }
            if (enable)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                key.SetValue(ValueName, $"\"{exe}\"");
                Logging.Logger.Info("Startup", "已启用 Windows 登录自启");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Logging.Logger.Info("Startup", "已禁用 Windows 登录自启");
            }
        }
        catch (Exception ex)
        {
            Logging.Logger.Error("Startup", $"设置自启失败: {ex.Message}");
        }
    }

    /// <summary>检查当前自启状态(并与实际 EXE 路径同步,处理移动)。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) != null;
        }
        catch { return false; }
    }
}
