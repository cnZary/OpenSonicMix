using System.Text.Json;

namespace SonicCube.Tray.Settings;

/// <summary>
/// 配置加载/保存,目录 %AppData%\SonicCubePanel\settings.json。
/// 保留未知字段以外的语义;保存时序列化全部已知字段。
/// </summary>
public sealed class SettingsStore
{
    public static string ConfigDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SonicCubePanel");

    public static string SettingsPath { get; } = Path.Combine(ConfigDir, "settings.json");
    public static string LogsDir { get; } = Path.Combine(ConfigDir, "logs");

    public AppSettings Current { get; private set; } = new();

    /// <summary>配置变更事件(保存后触发)。</summary>
    public event Action<AppSettings>? Changed;

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var s = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);
                if (s != null) Current = s;
            }
        }
        catch (Exception ex)
        {
            Logging.Logger.Warn("Settings", $"读取配置失败,使用默认值: {ex.Message}");
        }
        return Current;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var json = JsonSerializer.Serialize(Current, SettingsJsonContext.Default.AppSettings);
            var tmp = SettingsPath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, SettingsPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Logging.Logger.Error("Settings", $"保存配置失败: {ex.Message}");
        }
        Changed?.Invoke(Current);
    }

    /// <summary>应用来自 IPC 的部分更新(仅更新提供的字段),保存并广播。</summary>
    public void ApplyUpdate(Action<AppSettings> mutate)
    {
        mutate(Current);
        Save();
    }
}
